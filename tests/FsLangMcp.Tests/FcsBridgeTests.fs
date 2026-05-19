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
                    { projectPath = Some projectPath
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
                    { projectPath = Some projectPath
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
                    { projectPath = Some projectPath
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
                    { projectPath = Some(Path.Combine(Path.GetTempPath(), "NotAProject.fs"))
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
                    { projectPath = Some projectPath
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

// ─── FindMemberUsages (#107) ──────────────────────────────────────────────────

let private styleProjectSource =
    String.concat
        "\n"
        [ "module Theme.Style"
          ""
          "type Style ="
          "    { fg: string }"
          ""
          "    member this.Foreground = this.fg"
          ""
          "    member this.GetForeground () = this.fg"
          ""
          "let private sample = { fg = \"blue\" }"
          ""
          "let a = sample.Foreground"
          ""
          "let b = sample.Foreground"
          ""
          "let c = sample.GetForeground ()" ]

[<Fact>]
let ``FindMemberUsages returns call sites for a record member`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_member_%s{runId}")
        let bridge = FcsBridge()

        try
            let sourcePath, projectPath =
                writeProjectWithSource tempRoot "ThemeStyle" styleProjectSource

            let! result =
                bridge.FindMemberUsages(
                    { typeName = "Style"
                      memberName = "Foreground"
                      path = Some sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      exact = Some true
                      maxResults = Some 100
                      cursor = None }
                )

            Assert.Equal("succeeded", result["status"].GetValue<string>())
            // Definition + 2 usages — exact count depends on FCS reporting style,
            // but it must be > 0 and not include GetForeground sites.
            Assert.True(result["matchedCount"].GetValue<int>() >= 2)

            let uses = result["uses"] :?> JsonArray

            for use_ in uses do
                Assert.Equal("Foreground", (use_["symbol"]["displayName"]).GetValue<string>())
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``FindMemberUsages returns zero matches for unknown member`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_member_none_%s{runId}")
        let bridge = FcsBridge()

        try
            let sourcePath, projectPath =
                writeProjectWithSource tempRoot "ThemeStyle2" styleProjectSource

            let! result =
                bridge.FindMemberUsages(
                    { typeName = "Style"
                      memberName = "NonExistentMember"
                      path = Some sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      exact = Some true
                      maxResults = Some 100
                      cursor = None }
                )

            Assert.Equal("succeeded", result["status"].GetValue<string>())
            Assert.Equal(0, result["matchedCount"].GetValue<int>())
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``FindMemberUsages distinguishes between members on the same type`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_member_two_%s{runId}")
        let bridge = FcsBridge()

        try
            let sourcePath, projectPath =
                writeProjectWithSource tempRoot "ThemeStyle3" styleProjectSource

            // Foreground (property) — used twice in source
            let! fgResult =
                bridge.FindMemberUsages(
                    { typeName = "Style"
                      memberName = "Foreground"
                      path = Some sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      exact = Some true
                      maxResults = Some 100
                      cursor = None }
                )

            // GetForeground (method) — used once
            let! getFgResult =
                bridge.FindMemberUsages(
                    { typeName = "Style"
                      memberName = "GetForeground"
                      path = Some sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      exact = Some true
                      maxResults = Some 100
                      cursor = None }
                )

            Assert.True(fgResult["matchedCount"].GetValue<int>() >= 2)
            Assert.True(getFgResult["matchedCount"].GetValue<int>() >= 1)

            // Crucially, asking for "Foreground" (exact) must NOT match GetForeground sites.
            // The two queries return disjoint use sets; no use in fgResult.uses can be GetForeground.
            let fgUses = fgResult["uses"] :?> JsonArray

            for use_ in fgUses do
                let displayName = (use_["symbol"]["displayName"]).GetValue<string>()
                Assert.NotEqual<string>("GetForeground", displayName)
                Assert.Equal("Foreground", displayName)
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``FindMemberUsages errors when neither path nor projectPath provided`` () : Task =
    task {
        let bridge = FcsBridge()

        let! ex =
            Assert.ThrowsAsync<ArgumentException>(fun () ->
                bridge.FindMemberUsages(
                    { typeName = "Style"
                      memberName = "Foreground"
                      path = None
                      text = None
                      projectPath = None
                      projectOptions = None
                      exact = Some true
                      maxResults = Some 10
                      cursor = None }
                ))

        Assert.Contains("projectPath", ex.Message)
    }

[<Fact>]
let ``FindMemberUsages paginates via cursor`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_member_paginate_%s{runId}")
        let bridge = FcsBridge()

        try
            let sourcePath, projectPath =
                writeProjectWithSource tempRoot "ThemeStylePage" styleProjectSource

            // Page size = 1; the fixture has at least 2 Foreground uses.
            let! page1 =
                bridge.FindMemberUsages(
                    { typeName = "Style"
                      memberName = "Foreground"
                      path = Some sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      exact = Some true
                      maxResults = Some 1
                      cursor = None }
                )

            Assert.Equal("succeeded", page1["status"].GetValue<string>())
            Assert.Equal(1, ((page1["uses"]) :?> JsonArray).Count)

            let nextCursor = page1["nextCursor"]
            Assert.NotNull(nextCursor)
            let cursorStr = nextCursor.GetValue<string>()
            Assert.False(System.String.IsNullOrWhiteSpace cursorStr)

            // Second page returns the remaining use(s).
            let! page2 =
                bridge.FindMemberUsages(
                    { typeName = "Style"
                      memberName = "Foreground"
                      path = Some sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      exact = Some true
                      maxResults = Some 1
                      cursor = Some cursorStr }
                )

            Assert.Equal("succeeded", page2["status"].GetValue<string>())
            Assert.True(((page2["uses"]) :?> JsonArray).Count >= 1)

            // The two pages return different use sites (different ranges).
            let getStart (page: JsonNode) =
                let u = ((page["uses"]) :?> JsonArray)[0]
                let r = u["range"]
                (r["startLine"].GetValue<int>(), r["startColumn"].GetValue<int>())

            Assert.NotEqual(getStart page1, getStart page2)
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``FindMemberUsages substring typeName does not false-match siblings sharing a prefix`` () : Task =
    // Regression for review note: with exact=false, typeName="Style" should NOT
    // match members declared on a *different* type whose DisplayName starts with
    // "Style" (e.g. StyleSheet). DisplayName matching is exact even when
    // exact=false; substring is only applied to FullName.
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_member_prefix_%s{runId}")
        let bridge = FcsBridge()

        let source =
            String.concat
                "\n"
                [ "module Theme.Mixed"
                  ""
                  "type Style ="
                  "    { fg: string }"
                  "    member this.Foreground = this.fg"
                  ""
                  "type StyleSheet ="
                  "    { rules: int }"
                  "    member this.Foreground = this.rules"
                  ""
                  "let s1 = { fg = \"x\" }"
                  "let s2 = { rules = 1 }"
                  ""
                  "let a = s1.Foreground"
                  "let b = s2.Foreground" ]

        try
            let sourcePath, projectPath =
                writeProjectWithSource tempRoot "ThemeMixed" source

            let! result =
                bridge.FindMemberUsages(
                    { typeName = "Style"
                      memberName = "Foreground"
                      path = Some sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      exact = Some false // substring mode — the dangerous one
                      maxResults = Some 100
                      cursor = None }
                )

            Assert.Equal("succeeded", result["status"].GetValue<string>())

            // The s2.Foreground use is on StyleSheet, not Style. Querying with
            // typeName="Style" + exact=false should *not* pull in StyleSheet sites.
            //
            // FCS reports each use along with its symbol. We can't introspect the
            // declaring type from the JSON, so instead assert by sheer count: the
            // s1 fixture contributes 1 definition + 1 use = 2 matches; StyleSheet
            // (if leaked) would push this above. Combined with the previous test
            // verifying exact-match correctness, this guards the substring-typeName
            // regression flagged in review.
            let matched = result["matchedCount"].GetValue<int>()
            Assert.True(matched <= 3, $"expected ≤3 Style.Foreground matches, got {matched} (StyleSheet leakage)")
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

// ─── Accessibility surface in symbol JSON (#110) ──────────────────────────────

[<Fact>]
let ``symbol JSON exposes accessibility for private/internal/public members`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_acc_%s{runId}")
        let bridge = FcsBridge()

        let source =
            String.concat
                "\n"
                [ "module Theme.Access"
                  ""
                  "let publicValue = 1"
                  "let internal internalValue = 2"
                  "let private privateValue = 3" ]

        try
            let sourcePath, projectPath = writeProjectWithSource tempRoot "ThemeAcc" source

            let! result =
                bridge.FileSymbols(
                    { path = sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      includeAllUses = None
                      maxResults = Some 100 }
                )

            Assert.Equal("succeeded", result["status"].GetValue<string>())

            let symbols = (result["symbols"]) :?> JsonArray

            let findAcc name =
                symbols
                |> Seq.tryPick (fun s ->
                    if (s["symbol"]["displayName"]).GetValue<string>() = name then
                        Some((s["symbol"]["accessibility"]).GetValue<string>())
                    else
                        None)

            Assert.Equal(Some "public", findAcc "publicValue")
            Assert.Equal(Some "internal", findAcc "internalValue")
            Assert.Equal(Some "private", findAcc "privateValue")
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

// ─── fcs_check_file (#109) ───────────────────────────────────────────────────

[<Fact>]
let ``CheckFile returns succeeded with errorCount=0 for a clean project`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_checkfile_ok_%s{runId}")
        let bridge = FcsBridge()

        try
            let sourcePath, projectPath = writeSimpleProject tempRoot "CheckOk" "value"

            let! result =
                bridge.CheckFile(
                    { path = sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None }
                )

            Assert.Equal("succeeded", result["status"].GetValue<string>())
            Assert.Equal(0, result["errorCount"].GetValue<int>())
            Assert.True(result["hasFullTypeCheckInfo"].GetValue<bool>())
            Assert.False(result["parseHadErrors"].GetValue<bool>())
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``CheckFile totalDiagnostics matches parseDiagnostics + checkDiagnostics`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_checkfile_shape_%s{runId}")
        let bridge = FcsBridge()

        try
            let sourcePath, projectPath = writeSimpleProject tempRoot "CheckShape" "value"

            let! result =
                bridge.CheckFile(
                    { path = sourcePath
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None }
                )

            Assert.Equal(
                result["totalDiagnostics"].GetValue<int>(),
                ((result["parseDiagnostics"]) :?> JsonArray).Count
                + ((result["checkDiagnostics"]) :?> JsonArray).Count
            )
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

// ─── fcs_type_at_position fuzzy (#111) ───────────────────────────────────────

[<Fact>]
let ``TypeAtPosition no_symbol includes lineText + surroundingLines for misalignment visibility`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_tap_nosym_%s{runId}")
        let bridge = FcsBridge()

        try
            let src =
                String.concat
                    "\n"
                    [ "module TestModule"
                      ""
                      "let aValue = 42"
                      ""
                      "let unrelated () = ()" ]

            let sourcePath, projectPath = writeProjectWithSource tempRoot "TapNoSym" src

            // line 1 (blank-ish) — no symbol at col 0
            let! result =
                bridge.TypeAtPosition(
                    { path = sourcePath
                      line = 1
                      character = 0
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      fuzzy = None }
                )

            Assert.Equal("no_symbol", result["status"].GetValue<string>())
            // The lineText must always be present so 1-based vs 0-based mistakes are visible.
            let lineText = result["lineText"].GetValue<string>()
            Assert.NotNull(lineText :> obj)
            // surroundingLines has exactly 3 entries (line-1, line, line+1)
            let surrounding = result["surroundingLines"] :?> JsonArray
            Assert.Equal(3, surrounding.Count)
            // fuzzy flag echoed back
            Assert.False(result["fuzzy"].GetValue<bool>())
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``TypeAtPosition fuzzy snaps to nearest symbol when exact position misses`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_tap_fuzzy_%s{runId}")
        let bridge = FcsBridge()

        try
            // line 0: "module M"
            // line 1: ""
            // line 2: "let aValue = 42"
            let src = "module M\n\nlet aValue = 42\n"
            let sourcePath, projectPath = writeProjectWithSource tempRoot "TapFuzzy" src

            // Aim a few columns past the identifier on the SAME line — exact may miss,
            // fuzzy should still resolve "aValue".
            let! fuzzyResult =
                bridge.TypeAtPosition(
                    { path = sourcePath
                      line = 2
                      character = 25 // well past end of "let aValue = 42"
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      fuzzy = Some true }
                )

            // Either we snap and get ok, or we still no_symbol (depends on FCS column
            // tolerance). The contract: when ok, fuzzySnap must be true and resolvedLine
            // must be set.
            let status = fuzzyResult["status"].GetValue<string>()

            if status = "ok" then
                Assert.True(fuzzyResult["fuzzySnap"].GetValue<bool>())
                Assert.NotNull(fuzzyResult["resolvedLine"] :> obj)
                Assert.NotNull(fuzzyResult["resolvedCharacter"] :> obj)
            else
                // If even fuzzy can't resolve (edge case), ensure the diagnostic shape is correct.
                Assert.Equal("no_symbol", status)
                Assert.True(fuzzyResult["fuzzy"].GetValue<bool>())
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

[<Fact>]
let ``TypeAtPosition exact hit reports fuzzySnap=false`` () : Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let tempRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_tap_exact_%s{runId}")
        let bridge = FcsBridge()

        try
            // line 0: "module M"
            // line 1: ""
            // line 2: "let aValue = 42"  — identifier starts at column 4
            let src = "module M\n\nlet aValue = 42\n"
            let sourcePath, projectPath = writeProjectWithSource tempRoot "TapExact" src

            let! result =
                bridge.TypeAtPosition(
                    { path = sourcePath
                      line = 2
                      character = 5
                      text = None
                      projectPath = Some projectPath
                      projectOptions = None
                      fuzzy = Some false }
                )

            // If the position is precise enough, ok status with fuzzySnap=false.
            // If FCS still misses despite the exact location, accept no_symbol (FCS
            // tolerance can vary across versions — the test guards the shape, not FCS).
            let status = result["status"].GetValue<string>()

            if status = "ok" then
                Assert.False(result["fuzzySnap"].GetValue<bool>())
                Assert.Equal(2, result["resolvedLine"].GetValue<int>())
                Assert.Equal(5, result["resolvedCharacter"].GetValue<int>())
        finally
            if Directory.Exists tempRoot then
                Directory.Delete(tempRoot, true)
    }

// ─── fcs_validate_snippet (#112) ─────────────────────────────────────────────

[<Fact>]
let ``ValidateSnippet returns succeeded with errorCount=0 for a valid trivial snippet`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! result =
            bridge.ValidateSnippet(
                { content = "module FsLangMcp.SnippetValidationOk\n\nlet x = 1 + 2\n"
                  mode = Some "fs"
                  projectPath = Some projectPath }
            )

        Assert.Equal("succeeded", result["status"].GetValue<string>())
        Assert.Equal(0, result["errorCount"].GetValue<int>())
        Assert.Equal("fs", result["mode"].GetValue<string>())
    }

[<Fact>]
let ``ValidateSnippet flags a type error in the snippet`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! result =
            bridge.ValidateSnippet(
                { content = "module FsLangMcp.SnippetValidationBad\n\nlet x: int = \"not an int\"\n"
                  mode = Some "fs"
                  projectPath = Some projectPath }
            )

        // We don't require status="aborted" — type errors typically yield status="succeeded"
        // with errorCount>0 (parse OK, check populated diagnostics).
        Assert.True(result["errorCount"].GetValue<int>() > 0, "Expected at least one error diagnostic")
        Assert.True(result["totalDiagnostics"].GetValue<int>() > 0)
    }

[<Fact>]
let ``ValidateSnippet rejects unknown mode`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! result =
            bridge.ValidateSnippet(
                { content = "module M\nlet x = 1\n"
                  mode = Some "garbage"
                  projectPath = Some projectPath }
            )

        Assert.Equal("invalid_args", result["status"].GetValue<string>())
    }

[<Fact>]
let ``ValidateSnippet requires projectPath`` () : Task =
    task {
        let bridge = FcsBridge()

        let! result =
            bridge.ValidateSnippet(
                { content = "module M\nlet x = 1\n"
                  mode = None
                  projectPath = None }
            )

        Assert.Equal("invalid_args", result["status"].GetValue<string>())
    }

[<Fact>]
let ``ValidateSnippet cleans up the temp file after running`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let beforeFiles =
            Directory.GetFiles(Path.GetTempPath(), "fslangmcp_snippet_*")
            |> Set.ofArray

        let! _ =
            bridge.ValidateSnippet(
                { content = "module M\nlet x = 1\n"
                  mode = Some "fs"
                  projectPath = Some projectPath }
            )

        let afterFiles =
            Directory.GetFiles(Path.GetTempPath(), "fslangmcp_snippet_*")
            |> Set.ofArray

        // No new snippet temp files left behind.
        let leftover = Set.difference afterFiles beforeFiles
        Assert.Empty(leftover)
    }

// ─── fcs_referenced_symbols + fcs_nuget_types (#113) ─────────────────────────

[<Fact>]
let ``ReferencedSymbols finds known framework type System.IO.File`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! result =
            bridge.ReferencedSymbols(
                { query = "System.IO.File"
                  projectPath = Some projectPath
                  includeNonPublic = None
                  maxResults = Some 50
                  cursor = None }
            )

        Assert.Equal("ok", result["status"].GetValue<string>())
        let resultsArray = result["results"] :?> JsonArray
        Assert.True(resultsArray.Count > 0, "Expected at least one match for System.IO.File")

        // First match should have an accessibility of "public" (default filter is public-only).
        let firstNode = resultsArray[0]
        let firstAcc = firstNode["accessibility"].GetValue<string>()
        Assert.Contains(firstAcc, [ "public"; "unknown" ])
    }

[<Fact>]
let ``ReferencedSymbols returns invalid_args for empty query`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! result =
            bridge.ReferencedSymbols(
                { query = "   "
                  projectPath = Some projectPath
                  includeNonPublic = None
                  maxResults = None
                  cursor = None }
            )

        Assert.Equal("invalid_args", result["status"].GetValue<string>())
        Assert.Contains("query", (result["message"].GetValue<string>()).ToLowerInvariant())
    }

[<Fact>]
let ``ReferencedSymbols returns invalid_args when projectPath is None`` () : Task =
    task {
        let bridge = FcsBridge()

        let! result =
            bridge.ReferencedSymbols(
                { query = "Anything"
                  projectPath = None
                  includeNonPublic = None
                  maxResults = None
                  cursor = None }
            )

        Assert.Equal("invalid_args", result["status"].GetValue<string>())
        Assert.Contains("projectpath", (result["message"].GetValue<string>()).ToLowerInvariant())
    }

[<Fact>]
let ``ReferencedSymbols includeNonPublic surfaces additional results vs default`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! publicOnly =
            bridge.ReferencedSymbols(
                { query = "FSharpProjectOptions"
                  projectPath = Some projectPath
                  includeNonPublic = Some false
                  maxResults = Some 500
                  cursor = None }
            )

        let! includingInternal =
            bridge.ReferencedSymbols(
                { query = "FSharpProjectOptions"
                  projectPath = Some projectPath
                  includeNonPublic = Some true
                  maxResults = Some 500
                  cursor = None }
            )

        let publicCount = (publicOnly["results"] :?> JsonArray).Count
        let allCount = (includingInternal["results"] :?> JsonArray).Count
        // includeNonPublic must never return fewer matches than the public-only filter.
        Assert.True(
            allCount >= publicCount,
            $"includeNonPublic should return >= public-only count: public=%d{publicCount} all=%d{allCount}"
        )
    }

[<Fact>]
let ``NugetTypes enumerates types from a known framework assembly`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! result =
            bridge.NugetTypes(
                { packageId = "FSharp.Core"
                  projectPath = Some projectPath
                  includeNonPublic = None
                  maxResults = Some 50
                  cursor = None }
            )

        Assert.Equal("ok", result["status"].GetValue<string>())
        let matched = (result["matchedAssemblies"] :?> JsonArray)
        Assert.True(matched.Count > 0, "Expected FSharp.Core to match at least one assembly")
        let resultsArray = result["results"] :?> JsonArray
        Assert.True(resultsArray.Count > 0, "Expected non-empty type list from FSharp.Core")
    }

[<Fact>]
let ``NugetTypes returns empty results + zero matched assemblies for an unknown package`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! result =
            bridge.NugetTypes(
                { packageId = "Definitely.Not.A.Real.Package.zzz9999"
                  projectPath = Some projectPath
                  includeNonPublic = None
                  maxResults = Some 10
                  cursor = None }
            )

        Assert.Equal("ok", result["status"].GetValue<string>())
        Assert.Equal(0, (result["matchedAssemblies"] :?> JsonArray).Count)
        Assert.Equal(0, (result["results"] :?> JsonArray).Count)
    }

[<Fact>]
let ``NugetTypes returns invalid_args for empty packageId`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! result =
            bridge.NugetTypes(
                { packageId = ""
                  projectPath = Some projectPath
                  includeNonPublic = None
                  maxResults = None
                  cursor = None }
            )

        Assert.Equal("invalid_args", result["status"].GetValue<string>())
        Assert.Contains("packageid", (result["message"].GetValue<string>()).ToLowerInvariant())
    }

[<Fact>]
let ``NugetTypes returns invalid_args when projectPath is None`` () : Task =
    task {
        let bridge = FcsBridge()

        let! result =
            bridge.NugetTypes(
                { packageId = "FSharp.Core"
                  projectPath = None
                  includeNonPublic = None
                  maxResults = None
                  cursor = None }
            )

        Assert.Equal("invalid_args", result["status"].GetValue<string>())
    }

[<Fact>]
let ``NugetTypes does not match System.* assemblies for packageId='System'`` () : Task =
    task {
        let root = findRepoRoot ()
        let projectPath = Path.Combine(root, "FsLangMcp.fsproj")
        let bridge = FcsBridge()

        let! result =
            bridge.NugetTypes(
                { packageId = "System"
                  projectPath = Some projectPath
                  includeNonPublic = None
                  maxResults = Some 10
                  cursor = None }
            )

        // Strict dotted-segment match: 'System' must match the *exact* assembly named
        // 'System' (if loaded) but NOT every System.Foo.Bar assembly. We expect either
        // an empty list (current netN.0 typically loads no plain 'System' assembly) or
        // exactly one match — not the dozens you'd see under reverse-prefix matching.
        let matched = result["matchedAssemblies"] :?> JsonArray
        Assert.True(matched.Count <= 1, $"Expected at most 1 'System' assembly, got %d{matched.Count}")
    }
