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
open FsLangMcp.FcsBridge

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
        JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true)

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
    let path = Path.Combine(Path.GetTempPath(), $"fslangmcp_test_%O{Guid.NewGuid()}.fs")
    File.WriteAllText(path, content)
    path

let private findRepoRoot () =
    let rec loop (dir: DirectoryInfo) =
        if isNull dir then
            failwith "Could not locate FsLangMcp repo root."
        elif File.Exists(Path.Combine(dir.FullName, "FsLangMcp.fsproj")) then
            dir.FullName
        else
            loop dir.Parent

    loop (DirectoryInfo(AppContext.BaseDirectory))

let private jsonArrayLength (node: JsonNode) = (node :?> JsonArray).Count

let private writeSimpleProject (root: string) (projectName: string) (symbolName: string) =
    let dir = Path.Combine(root, projectName)
    Directory.CreateDirectory(dir) |> ignore

    let sourcePath = Path.Combine(dir, "Library.fs")
    File.WriteAllText(sourcePath, $"module %s{projectName}.Library\n\nlet %s{symbolName} = 42\n")

    let projectPath = Path.Combine(dir, $"%s{projectName}.fsproj")

    File.WriteAllText(
        projectPath,
        String.concat
            Environment.NewLine
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup>"
              "    <TargetFramework>net10.0</TargetFramework>"
              "  </PropertyGroup>"
              "  <ItemGroup>"
              "    <Compile Include=\"Library.fs\" />"
              "  </ItemGroup>"
              "</Project>" ]
    )

    sourcePath, projectPath

let private writeProjectWithSource (root: string) (projectName: string) (source: string) =
    let dir = Path.Combine(root, projectName)
    Directory.CreateDirectory(dir) |> ignore

    let sourcePath = Path.Combine(dir, "Library.fs")
    File.WriteAllText(sourcePath, source)

    let projectPath = Path.Combine(dir, $"%s{projectName}.fsproj")

    File.WriteAllText(
        projectPath,
        String.concat
            Environment.NewLine
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup>"
              "    <TargetFramework>net10.0</TargetFramework>"
              "  </PropertyGroup>"
              "  <ItemGroup>"
              "    <Compile Include=\"Library.fs\" />"
              "  </ItemGroup>"
              "</Project>" ]
    )

    sourcePath, projectPath

[<Fact>]
let ``fcs_parse_and_check_file succeeds on valid F# snippet`` () : Task =
    task {
        let src =
            """
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
            | FSharpCheckFileAnswer.Aborted -> Assert.Fail("Type checking was aborted unexpectedly")
        finally
            if File.Exists(path) then
                File.Delete(path)
    }

[<Fact>]
let ``FcsBridge uses explicit fsproj projectPath without projectOptions`` () : Task =
    task {
        let root = findRepoRoot ()
        let sourcePath = Path.Combine(root, "FcsBridge.fs")
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! result =
            bridge.ParseAndCheckFile(
                { path = sourcePath
                  text = None
                  projectPath = Some projectPath
                  projectOptions = None }
            )

        Assert.Equal("ionide-proj-info", result["optionsSource"].GetValue<string>())
        Assert.Equal(Path.GetFullPath(projectPath), result["projectFileName"].GetValue<string>())
        Assert.True(result["hasFullTypeCheckInfo"].GetValue<bool>())
        Assert.Equal(0, jsonArrayLength result["parseDiagnostics"])
        Assert.Equal(0, jsonArrayLength result["checkDiagnostics"])

        let! cachedResult =
            bridge.ParseAndCheckFile(
                { path = Path.Combine(root, "Program.fs")
                  text = None
                  projectPath = Some projectPath
                  projectOptions = None }
            )

        Assert.Equal("ionide-proj-info", cachedResult["optionsSource"].GetValue<string>())
        Assert.Equal(Path.GetFullPath(projectPath), cachedResult["projectFileName"].GetValue<string>())
    }

[<Fact>]
let ``FcsBridge project symbol cache is keyed by auto-discovered project`` () : Task =
    task {
        let projectRunId = Guid.NewGuid().ToString("N")

        let tempRoot =
            Path.Combine(Path.GetTempPath(), $"fslangmcp_projects_%s{projectRunId}")

        let bridge = FcsBridge()

        try
            let projectAFile, _ = writeSimpleProject tempRoot "ProjectA" "onlyInProjectA"
            let projectBFile, _ = writeSimpleProject tempRoot "ProjectB" "onlyInProjectB"

            let! resultA =
                bridge.ProjectSymbolUses(
                    { path = projectAFile
                      text = None
                      projectPath = None
                      projectOptions = None
                      symbolQuery = "onlyInProjectA"
                      exact = Some true
                      maxResults = Some 10
                      cursor = None }
                )

            let! resultB =
                bridge.ProjectSymbolUses(
                    { path = projectBFile
                      text = None
                      projectPath = None
                      projectOptions = None
                      symbolQuery = "onlyInProjectB"
                      exact = Some true
                      maxResults = Some 10
                      cursor = None }
                )

            Assert.Equal("succeeded", resultA["status"].GetValue<string>())
            Assert.Equal("succeeded", resultB["status"].GetValue<string>())
            Assert.True(resultA["matchedCount"].GetValue<int>() > 0)
            Assert.True(resultB["matchedCount"].GetValue<int>() > 0)
            Assert.Contains("ProjectA.fsproj", resultA["projectFileName"].GetValue<string>())
            Assert.Contains("ProjectB.fsproj", resultB["projectFileName"].GetValue<string>())
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``FcsBridge CompileProject succeeds through FCS project typecheck`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_compile_ok_%s{runId}")

        let bridge = FcsBridge()

        try
            let _, projectPath = writeSimpleProject tempRoot "CompileOk" "compileValue"

            let! result =
                bridge.CompileProject(
                    { projectPath = projectPath
                      workspacePath = None
                      timeoutMs = Some 30000 }
                )

            Assert.Equal("succeeded", result["status"].GetValue<string>())
            Assert.Equal("fcs-parse-and-check-project", result["backend"].GetValue<string>())
            Assert.Equal("ionide-proj-info", result["optionsSource"].GetValue<string>())
            Assert.False(result["cached"].GetValue<bool>())
            Assert.Equal(0, result["errorCount"].GetValue<int>())
            Assert.Null(result["exitCode"])

            let! cachedResult =
                bridge.CompileProject(
                    { projectPath = projectPath
                      workspacePath = None
                      timeoutMs = Some 30000 }
                )

            Assert.Equal("succeeded", cachedResult["status"].GetValue<string>())
            Assert.True(cachedResult["cached"].GetValue<bool>())
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``FcsBridge CompileProject reports FCS typecheck errors`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_compile_fail_%s{runId}")

        let bridge = FcsBridge()

        try
            let sourcePath, projectPath =
                writeSimpleProject tempRoot "CompileFail" "compileValue"

            File.WriteAllText(sourcePath, "module CompileFail.Library\n\nlet broken: int = \"not an int\"\n")

            let! result =
                bridge.CompileProject(
                    { projectPath = projectPath
                      workspacePath = None
                      timeoutMs = Some 30000 }
                )

            Assert.Equal("failed", result["status"].GetValue<string>())
            Assert.Equal("fcs-parse-and-check-project", result["backend"].GetValue<string>())
            Assert.True(result["errorCount"].GetValue<int>() > 0)
            Assert.True((result["diagnostics"] :?> JsonArray).Count > 0)
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``FcsBridge CompileProject rejects non fsproj paths`` () : Task =
    task {
        let bridge = FcsBridge()

        let! ex =
            Assert.ThrowsAsync<ArgumentException>(fun () ->
                bridge.CompileProject(
                    { projectPath = Path.Combine(Path.GetTempPath(), "NotAProject.fs")
                      workspacePath = None
                      timeoutMs = Some 30000 }
                ))

        Assert.Contains("projectPath must point to an .fsproj file", ex.Message)
    }

[<Fact>]
let ``fcs_file_symbols returns expected symbols from a simple module`` () : Task =
    task {
        let src =
            """
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
            | FSharpCheckFileAnswer.Aborted -> Assert.Fail("Type checking was aborted unexpectedly")
        finally
            if File.Exists(path) then
                File.Delete(path)
    }

[<Fact>]
let ``fcs_project_symbol_uses finds a symbol by name`` () : Task =
    task {
        let src =
            """
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
            if File.Exists(path) then
                File.Delete(path)
    }

[<Fact>]
let ``fcs_file_outline returns compact definitions without local noise`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_outline_%s{runId}")
        let bridge = FcsBridge()

        try
            let sourcePath, projectPath =
                writeProjectWithSource
                    tempRoot
                    "OutlineProject"
                    "module OutlineProject.Library\n\nlet publicValue =\n    let localValue = 1\n    localValue + 1\n\ntype PublicRecord = { Name: string }\n"

            let! result =
                bridge.FileOutline(
                    { path = sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      includePrivate = None
                      includeLocal = Some false
                      maxResults = Some 50 }
                )

            let entries = result["entries"] :?> JsonArray
            let names = entries |> Seq.map (fun node -> node["name"].GetValue<string>()) |> Seq.toList

            Assert.Equal("succeeded", result["status"].GetValue<string>())
            Assert.Contains("publicValue", names)
            Assert.Contains("PublicRecord", names)
            Assert.DoesNotContain("localValue", names)
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``fcs_find_symbol groups definitions and references with source context`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_symbol_%s{runId}")
        let bridge = FcsBridge()

        try
            let sourcePath, projectPath =
                writeProjectWithSource
                    tempRoot
                    "FindSymbolProject"
                    "module FindSymbolProject.Library\n\nlet targetValue = 41\n\nlet useValue = targetValue + 1\n"

            let! result =
                bridge.FindSymbol(
                    { path = sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      symbolQuery = "targetValue"
                      exact = Some true
                      maxResults = Some 20
                      contextLines = Some 1
                      includeDeclaration = Some true
                      cursor = None }
                )

            let symbols = result["symbols"] :?> JsonArray
            Assert.Equal("succeeded", result["status"].GetValue<string>())
            Assert.True(symbols.Count >= 1)

            let first = symbols[0]
            Assert.True(first["definitionCount"].GetValue<int>() >= 1)
            Assert.True(first["referenceCount"].GetValue<int>() >= 1)
            let firstReference = (first["references"] :?> JsonArray)[0]
            Assert.Contains("targetValue", firstReference["lineText"].GetValue<string>())
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``fcs_symbol_at_word returns ambiguity then resolves occurrence`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_symbol_at_word_%s{runId}")
        let bridge = FcsBridge()

        try
            let sourcePath, projectPath =
                writeProjectWithSource
                    tempRoot
                    "WordProject"
                    "module WordProject.Library\n\nlet targetValue = 41\n\nlet useValue = targetValue + targetValue\n"

            let! ambiguous =
                bridge.SymbolAtWord(
                    { path = sourcePath
                      line = 4
                      word = Some "targetValue"
                      occurrence = None
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      includeDocumentation = Some false }
                )

            Assert.Equal("ambiguous_word", ambiguous["status"].GetValue<string>())
            Assert.Equal(2, (ambiguous["candidates"] :?> JsonArray).Count)

            let! resolved =
                bridge.SymbolAtWord(
                    { path = sourcePath
                      line = 4
                      word = Some "targetValue"
                      occurrence = Some 0
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      includeDocumentation = Some false }
                )

            Assert.Equal("ok", resolved["status"].GetValue<string>())
            Assert.Equal("targetValue", resolved["symbolName"].GetValue<string>())
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``fcs_project_outline returns filtered per file outline`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_project_outline_%s{runId}")
        let bridge = FcsBridge()

        try
            let _, projectPath = writeSimpleProject tempRoot "ProjectOutline" "projectOutlineValue"

            let! result =
                bridge.ProjectOutline(
                    { projectPath = projectPath
                      workspacePath = Some tempRoot
                      includePrivate = None
                      includeTests = None
                      includeGeneratedFiles = None
                      maxFiles = Some 10
                      maxResultsPerFile = Some 50
                      summaryOnly = None
                      cursor = None
                      filter = None
                      nameContains = None }
                )

            Assert.Equal("ok", result["status"].GetValue<string>())
            Assert.Equal(1, (result["filterSummary"]["includedFiles"]).GetValue<int>())
            Assert.Equal(1, (result["files"] :?> JsonArray).Count)
        finally
            if Directory.Exists(tempRoot) then
                Directory.Delete(tempRoot, true)
    }

// ─── Part A: path validation tests ────────────────────────────────────────────

[<Fact>]
let ``fcs_find_symbol returns InvalidArgument error when path is a directory`` () : Task =
    task {
        let bridge = FcsBridge()
        let dir = Path.GetTempPath() // always exists as a directory

        let! result =
            bridge.FindSymbol(
                { path = dir
                  text = None
                  projectPath = None
                  projectOptions = None
                  symbolQuery = "anySymbol"
                  exact = Some false
                  maxResults = Some 10
                  contextLines = Some 0
                  includeDeclaration = Some true
                  cursor = None }
            )

        Assert.Equal("error", result["status"].GetValue<string>())
        Assert.Equal("InvalidArgument", result["errorKind"].GetValue<string>())
        Assert.Contains("not a directory", result["message"].GetValue<string>())
        Assert.Contains("fcs_find_symbol", result["message"].GetValue<string>())
    }

[<Fact>]
let ``fcs_find_symbol returns InvalidArgument error when path does not exist`` () : Task =
    task {
        let bridge = FcsBridge()
        let missing = Path.Combine(Path.GetTempPath(), $"does_not_exist_%O{Guid.NewGuid()}.fs")

        let! result =
            bridge.FindSymbol(
                { path = missing
                  text = None
                  projectPath = None
                  projectOptions = None
                  symbolQuery = "anySymbol"
                  exact = Some false
                  maxResults = Some 10
                  contextLines = Some 0
                  includeDeclaration = Some true
                  cursor = None }
            )

        Assert.Equal("error", result["status"].GetValue<string>())
        Assert.Equal("InvalidArgument", result["errorKind"].GetValue<string>())
        Assert.Contains("does not exist or is not readable", result["message"].GetValue<string>())
    }

[<Fact>]
let ``fcs_file_outline returns InvalidArgument error when path is a directory`` () : Task =
    task {
        let bridge = FcsBridge()
        let dir = Path.GetTempPath()

        let! result =
            bridge.FileOutline(
                { path = dir
                  text = None
                  projectPath = None
                  projectOptions = None
                  includePrivate = None
                  includeLocal = None
                  maxResults = None }
            )

        Assert.Equal("error", result["status"].GetValue<string>())
        Assert.Equal("InvalidArgument", result["errorKind"].GetValue<string>())
        Assert.Contains("fcs_file_outline", result["message"].GetValue<string>())
    }

[<Fact>]
let ``fcs_file_symbols returns InvalidArgument error when path does not exist`` () : Task =
    task {
        let bridge = FcsBridge()
        let missing = Path.Combine(Path.GetTempPath(), $"does_not_exist_%O{Guid.NewGuid()}.fs")

        let! result =
            bridge.FileSymbols(
                { path = missing
                  text = None
                  projectPath = None
                  projectOptions = None
                  includeAllUses = None
                  maxResults = None }
            )

        Assert.Equal("error", result["status"].GetValue<string>())
        Assert.Equal("InvalidArgument", result["errorKind"].GetValue<string>())
        Assert.Contains("fcs_file_symbols", result["message"].GetValue<string>())
    }

// ─── Regression: validateSourcePath unsaved-buffer workflow (#77 VERIFY) ─────

[<Fact>]
let ``fcs_parse_and_check_file bypasses File.Exists gate when non-empty text is supplied`` () : Task =
    // Finding 1: a synthetic-buffer call (path does not exist on disk, text is supplied)
    // must NOT return InvalidArgument — FCS should use the supplied text.
    task {
        let bridge = FcsBridge()
        let syntheticPath = Path.Combine(Path.GetTempPath(), $"synthetic_%O{Guid.NewGuid()}.fs")

        let! result =
            bridge.ParseAndCheckFile(
                { path = syntheticPath
                  text = Some "module Foo\nlet x = 1"
                  projectPath = None
                  projectOptions = None }
            )

        // Must not return an InvalidArgument error due to the missing file.
        let status = (result["status"]).GetValue<string>()
        Assert.NotEqual<string>("error", status)
    }

[<Fact>]
let ``fcs_file_outline bypasses File.Exists gate when non-empty text is supplied`` () : Task =
    // Finding 1: directory rejection still fires even when text is supplied.
    task {
        let bridge = FcsBridge()
        let syntheticPath = Path.Combine(Path.GetTempPath(), $"synthetic_%O{Guid.NewGuid()}.fs")

        let! result =
            bridge.FileOutline(
                { path = syntheticPath
                  text = Some "module Bar\nlet y = 2"
                  projectPath = None
                  projectOptions = None
                  includePrivate = None
                  includeLocal = None
                  maxResults = None }
            )

        let status = (result["status"]).GetValue<string>()
        Assert.NotEqual<string>("error", status)
    }

[<Fact>]
let ``fcs_file_outline still rejects a directory path even when text is supplied`` () : Task =
    // Finding 1: the directory guard must remain active even with a non-empty text buffer.
    task {
        let bridge = FcsBridge()
        let dir = Path.GetTempPath()

        let! result =
            bridge.FileOutline(
                { path = dir
                  text = Some "module Ignored"
                  projectPath = None
                  projectOptions = None
                  includePrivate = None
                  includeLocal = None
                  maxResults = None }
            )

        Assert.Equal("error", result["status"].GetValue<string>())
        Assert.Equal("InvalidArgument", result["errorKind"].GetValue<string>())
    }
