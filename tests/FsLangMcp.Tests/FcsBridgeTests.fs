module FsLangMcp.Tests.FcsBridgeTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.Types
open FsLangMcp.Tools

// ─── ToolError serialization tests ────────────────────────────────────────────
// These tests call the real toolErrorToJson from Tools.fs so that regressions
// in the serialization logic are caught directly.

[<Fact>]
let ``ToolError InvalidArgs serializes with correct errorKind and message`` () =
    let json = toolErrorToJson (InvalidArgs "bad argument")
    Assert.Contains("\"errorKind\":\"InvalidArgs\"", json)
    Assert.Contains("bad argument", json)

[<Fact>]
let ``ToolError NotReady serializes with correct errorKind`` () =
    let json = toolErrorToJson (NotReady "not loaded")
    Assert.Contains("\"errorKind\":\"NotReady\"", json)
    Assert.Contains("not loaded", json)

[<Fact>]
let ``ToolError InfraFailure serializes with correct errorKind`` () =
    let ex = Exception("oops")
    let json = toolErrorToJson (InfraFailure ex)
    Assert.Contains("\"errorKind\":\"InfraFailure\"", json)
    Assert.Contains("oops", json)

[<Fact>]
let ``ToolError FcsAborted serializes with correct errorKind`` () =
    let json = toolErrorToJson (FcsAborted "cancelled")
    Assert.Contains("\"errorKind\":\"FcsAborted\"", json)
    Assert.Contains("cancelled", json)

[<Fact>]
let ``ToolError FileNotFound serializes with correct errorKind`` () =
    let json = toolErrorToJson (FileNotFound "/some/path")
    Assert.Contains("\"errorKind\":\"FileNotFound\"", json)
    Assert.Contains("/some/path", json)

[<Fact>]
let ``ToolError message with quotes is properly escaped`` () =
    let json = toolErrorToJson (InvalidArgs "message with \"quotes\"")
    Assert.Contains("\"errorKind\":\"InvalidArgs\"", json)
    // System.Text.Json with UnsafeRelaxedJsonEscaping escapes inner quotes as \" (not \u0022)
    Assert.Contains("\\\"quotes\\\"", json)

[<Fact>]
let ``renderToken preserves F# type signature characters without unicode escaping`` () =
    // Regression for STJ default encoder mangling 'a -> 'b to \u0027a -\u003E \u0027b
    let renderOpts =
        JsonSerializerOptions(
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true)
    let node = JsonObject()
    node["typeString"] <- JsonValue.Create("'a -> 'b when 'a : comparison")
    let rendered = JsonSerializer.Serialize(node, renderOpts)
    Assert.Contains("'a -> 'b", rendered)
    Assert.DoesNotContain("\\u0027", rendered)
    Assert.DoesNotContain("\\u003E", rendered)

// Helpers

let private checker =
    FSharpChecker.Create(
        keepAssemblyContents = true,
        keepAllBackgroundResolutions = true,
        keepAllBackgroundSymbolUses = true
    )

let private asTask workflow =
    Async.StartAsTask(workflow, cancellationToken = CancellationToken.None)

/// Write a temp .fs file, return its path
let private writeTempFs (content: string) =
    let path = Path.Combine(Path.GetTempPath(), $"fslangmcp_test_{Guid.NewGuid()}.fs")
    File.WriteAllText(path, content)
    path

[<Fact>]
let ``fcs_parse_and_check_file succeeds on valid F# snippet`` () : Task =
    task {
        let src = """
module TestModule

let add x y = x + y

let result = add 1 2
"""
        let path = writeTempFs src
        try
            let sourceText = SourceText.ofString src
            let! opts, _ = checker.GetProjectOptionsFromScript(path, sourceText) |> asTask
            let parsingOpts, _ = checker.GetParsingOptionsFromProjectOptions(opts)
            let! parseResults = checker.ParseFile(path, sourceText, parsingOpts) |> asTask
            let! _, checkAnswer = checker.ParseAndCheckFileInProject(path, 0, sourceText, opts) |> asTask

            match checkAnswer with
            | FSharpCheckFileAnswer.Succeeded results ->
                Assert.True(results.HasFullTypeCheckInfo, "Should have full type check info")
                Assert.False(parseResults.ParseHadErrors, "Parse should not have errors")
            | FSharpCheckFileAnswer.Aborted ->
                Assert.Fail("Type checking was aborted unexpectedly")
        finally
            if File.Exists(path) then File.Delete(path)
    }

[<Fact>]
let ``fcs_file_symbols returns expected symbols from a simple module`` () : Task =
    task {
        let src = """
module SymbolModule

let myValue = 42

let myFunction x = x + 1

type MyRecord = { Field1: int; Field2: string }
"""
        let path = writeTempFs src
        try
            let sourceText = SourceText.ofString src
            let! opts, _ = checker.GetProjectOptionsFromScript(path, sourceText) |> asTask
            let parsingOpts, _ = checker.GetParsingOptionsFromProjectOptions(opts)
            let! _ = checker.ParseFile(path, sourceText, parsingOpts) |> asTask
            let! _, checkAnswer = checker.ParseAndCheckFileInProject(path, 0, sourceText, opts) |> asTask

            match checkAnswer with
            | FSharpCheckFileAnswer.Succeeded results ->
                let definedSymbols =
                    results.GetAllUsesOfAllSymbolsInFile()
                    |> Seq.filter (fun su -> su.IsFromDefinition)
                    |> Seq.map (fun su -> su.Symbol.DisplayName)
                    |> Seq.toList

                // Should contain our defined names
                Assert.Contains("myValue", definedSymbols)
                Assert.Contains("myFunction", definedSymbols)
                Assert.Contains("MyRecord", definedSymbols)
            | FSharpCheckFileAnswer.Aborted ->
                Assert.Fail("Type checking was aborted unexpectedly")
        finally
            if File.Exists(path) then File.Delete(path)
    }

[<Fact>]
let ``fcs_project_symbol_uses finds a symbol by name`` () : Task =
    task {
        let src = """
module SearchModule

let targetFunction x y = x * y

let callSite = targetFunction 3 4
"""
        let path = writeTempFs src
        try
            let sourceText = SourceText.ofString src
            let! opts, _ = checker.GetProjectOptionsFromScript(path, sourceText) |> asTask
            let! projectResults = checker.ParseAndCheckProject(opts) |> asTask

            let allUses = projectResults.GetAllUsesOfAllSymbols()

            let targetUses =
                allUses
                |> Seq.filter (fun su ->
                    su.Symbol.DisplayName.Contains("targetFunction", StringComparison.OrdinalIgnoreCase))
                |> Seq.toArray

            Assert.True(targetUses.Length > 0, "Should find at least one use of targetFunction")

            // Should find both definition and call site
            let hasDefinition = targetUses |> Array.exists (fun su -> su.IsFromDefinition)
            Assert.True(hasDefinition, "Should find the definition of targetFunction")
        finally
            if File.Exists(path) then File.Delete(path)
    }
