module FsLangMcp.Tests.FcsBridgeTests

open System
open System.IO
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open Xunit

// Helpers

let private checker =
    FSharpChecker.Create(
        keepAssemblyContents = true,
        keepAllBackgroundResolutions = true,
        keepAllBackgroundSymbolUses = true
    )

let private asTask workflow =
    Async.StartAsTask(workflow, cancellationToken = CancellationToken.None)

let private normalizePath (path: string) = Path.GetFullPath(path)

/// Write a temp .fs file, return its path
let private writeTempFs (content: string) =
    let path = Path.Combine(Path.GetTempPath(), $"fslangmcp_test_{Guid.NewGuid()}.fs")
    File.WriteAllText(path, content)
    path

[<Fact>]
let ``fcs_parse_and_check_file succeeds on valid F# snippet`` () =
    let src = """
module TestModule

let add x y = x + y

let result = add 1 2
"""
    let path = writeTempFs src
    try
        let sourceText = SourceText.ofString src
        let getOptions =
            task {
                let! opts, _ = checker.GetProjectOptionsFromScript(path, sourceText) |> asTask
                return opts
            }
        let opts = getOptions.GetAwaiter().GetResult()
        let parsingOpts, _ = checker.GetParsingOptionsFromProjectOptions(opts)
        let parseTask = checker.ParseFile(path, sourceText, parsingOpts) |> asTask
        let parseResults = parseTask.GetAwaiter().GetResult()
        let checkTask = checker.ParseAndCheckFileInProject(path, 0, sourceText, opts) |> asTask
        let _, checkAnswer = checkTask.GetAwaiter().GetResult()

        match checkAnswer with
        | FSharpCheckFileAnswer.Succeeded results ->
            Assert.True(results.HasFullTypeCheckInfo, "Should have full type check info")
            Assert.False(parseResults.ParseHadErrors, "Parse should not have errors")
        | FSharpCheckFileAnswer.Aborted ->
            Assert.Fail("Type checking was aborted unexpectedly")
    finally
        if File.Exists(path) then File.Delete(path)

[<Fact>]
let ``fcs_file_symbols returns expected symbols from a simple module`` () =
    let src = """
module SymbolModule

let myValue = 42

let myFunction x = x + 1

type MyRecord = { Field1: int; Field2: string }
"""
    let path = writeTempFs src
    try
        let sourceText = SourceText.ofString src
        let getOptions =
            task {
                let! opts, _ = checker.GetProjectOptionsFromScript(path, sourceText) |> asTask
                return opts
            }
        let opts = getOptions.GetAwaiter().GetResult()
        let parsingOpts, _ = checker.GetParsingOptionsFromProjectOptions(opts)
        let parseTask = checker.ParseFile(path, sourceText, parsingOpts) |> asTask
        let _ = parseTask.GetAwaiter().GetResult()
        let checkTask = checker.ParseAndCheckFileInProject(path, 0, sourceText, opts) |> asTask
        let _, checkAnswer = checkTask.GetAwaiter().GetResult()

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

[<Fact>]
let ``fcs_project_symbol_uses finds a symbol by name`` () =
    let src = """
module SearchModule

let targetFunction x y = x * y

let callSite = targetFunction 3 4
"""
    let path = writeTempFs src
    try
        let sourceText = SourceText.ofString src
        let getOptions =
            task {
                let! opts, _ = checker.GetProjectOptionsFromScript(path, sourceText) |> asTask
                return opts
            }
        let opts = getOptions.GetAwaiter().GetResult()
        let projectTask = checker.ParseAndCheckProject(opts) |> asTask
        let projectResults = projectTask.GetAwaiter().GetResult()

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
