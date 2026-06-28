module FsLangMcp.Tests.TestsForSymbolTests

// ─── #60: fcs_tests_for_symbol — the test-coverage slice of `find` ───────────────
//
// Fixture: a 3-project solution.
//   Lib        — defines `add` and `subtract`.
//   Lib.Tests  — a TEST project (<IsTestProject>true</IsTestProject>) that calls `add`
//                inside two [<Fact>] tests. A self-contained FactAttribute keeps the
//                project compiling without a real xunit reference.
//   App        — a NON-test project that calls `subtract`.
// So tests_for_symbol "add" recovers the two [<Fact>] sites with their enclosing test
// names; tests_for_symbol "subtract" is empty (used only in the non-test App project).
// Built once per class (IClassFixture) so cross-project references resolve.

open System
open System.IO
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ── Fixture sources ──────────────────────────────────────────────────────────────

let private libFs =
    String.concat
        "\n"
        [ "module Lib.Math"
          ""
          "let add (a: int) (b: int) = a + b"
          ""
          "let subtract (a: int) (b: int) = a - b"
          "" ]

// A self-contained FactAttribute so the test project compiles WITHOUT a real xunit
// reference; <IsTestProject>true</IsTestProject> is what marks it a test project, and
// the [<Fact>] text is what the enclosing-test scan keys on.
let private testsFs =
    String.concat
        "\n"
        [ "namespace Lib.Tests"
          ""
          "open System"
          "open Lib.Math"
          ""
          "type FactAttribute() ="
          "    inherit Attribute()"
          ""
          "module AddTests ="
          ""
          "    [<Fact>]"
          "    let ``add returns the sum`` () ="
          "        let actual = add 2 3"
          "        if actual <> 5 then failwith \"add broken\""
          ""
          "    [<Fact>]"
          "    let ``add is commutative`` () ="
          "        let left = add 1 2"
          "        let right = add 2 1"
          "        if left <> right then failwith \"add not commutative\""
          "" ]

let private appFs =
    String.concat
        "\n"
        [ "module App.Run"
          ""
          "open Lib.Math"
          ""
          "let go () = subtract 10 4"
          "" ]

let private leafProject (sourceFile: string) =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          $"  <ItemGroup><Compile Include=\"{sourceFile}\" /></ItemGroup>"
          "</Project>" ]

let private testProject (sourceFile: string) (refRelative: string) =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup>"
          "    <TargetFramework>net10.0</TargetFramework>"
          "    <IsTestProject>true</IsTestProject>"
          "  </PropertyGroup>"
          $"  <ItemGroup><Compile Include=\"{sourceFile}\" /></ItemGroup>"
          $"  <ItemGroup><ProjectReference Include=\"{refRelative}\" /></ItemGroup>"
          "</Project>" ]

let private refProject (sourceFile: string) (refRelative: string) =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          $"  <ItemGroup><Compile Include=\"{sourceFile}\" /></ItemGroup>"
          $"  <ItemGroup><ProjectReference Include=\"{refRelative}\" /></ItemGroup>"
          "</Project>" ]

let private slnx =
    String.concat
        "\n"
        [ "<Solution>"
          "  <Project Path=\"Lib/Lib.fsproj\" />"
          "  <Project Path=\"Lib.Tests/Lib.Tests.fsproj\" />"
          "  <Project Path=\"App/App.fsproj\" />"
          "</Solution>" ]

// ── Class fixture: written + built ONCE, shared by every test in the class ─────────

type TestsForSymbolFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_tfs_{runId}")

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    do write "Lib/Lib.fsproj" (leafProject "Lib.fs") |> ignore
    do write "Lib/Lib.fs" libFs |> ignore
    do write "Lib.Tests/Lib.Tests.fsproj" (testProject "Tests.fs" "../Lib/Lib.fsproj") |> ignore
    do write "Lib.Tests/Tests.fs" testsFs |> ignore
    do write "App/App.fsproj" (refProject "App.fs" "../Lib/Lib.fsproj") |> ignore
    do write "App/App.fs" appFs |> ignore
    let slnxPath = write "TestsForSymbol.slnx" slnx

    // dotnet build is ground truth and produces Lib.dll so the test project's FCS sweep
    // resolves the cross-project `add` reference. Isolation/retry flags mirror FindFixture
    // (parallel restore on a shared P2P races on *.nuget.g.props; in-process MSBuild from
    // sibling test collections can collide).
    let buildOnce () =
        let psi =
            ProcessStartInfo(
                "dotnet",
                $"build \"{slnxPath}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
            )

        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
        psi.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] <- "1"
        use p = Process.Start(psi)
        let stdout = p.StandardOutput.ReadToEnd()
        let stderr = p.StandardError.ReadToEnd()
        p.WaitForExit()
        p.ExitCode, stdout + stderr

    let rec buildWithRetry attempt =
        let code, log = buildOnce ()

        if code = 0 || attempt >= 3 then
            code, log
        else
            System.Threading.Thread.Sleep(1500)
            buildWithRetry (attempt + 1)

    let buildExit, buildLog = buildWithRetry 1

    member _.Root = root
    member _.Slnx = slnxPath
    member _.BuildExitCode = buildExit
    member _.BuildLog = buildLog

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

// ── JSON helpers ───────────────────────────────────────────────────────────────────

let private gi (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()

let private testEntries (result: JsonNode) =
    match result["tests"] with
    | :? JsonArray as arr -> arr |> Seq.toList
    | _ -> []

let private enclosingTestNames (result: JsonNode) =
    testEntries result
    |> List.choose (fun t ->
        match t["enclosingTest"] with
        | :? JsonValue as v -> Some(v.GetValue<string>())
        | _ -> None)

// ── Arg builder ────────────────────────────────────────────────────────────────────

let private tfsArgs (projectPath: string) (query: string) : FcsTestsForSymbolArgs =
    { symbolQuery = query
      exact = None
      path = None
      text = None
      projectPath = Some projectPath
      maxResults = None }

// ─────────────────────────────────────────────────────────────────────────────────

type TestsForSymbolTests(fx: TestsForSymbolFixture, output: ITestOutputHelper) =
    interface IClassFixture<TestsForSymbolFixture>

    [<Fact>]
    member _.``tests_for_symbol returns the test-file sites for a symbol covered by tests``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! result = bridge.TestsForSymbol(tfsArgs fx.Slnx "add")

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal("add", gs result "symbol")

            // Only Lib.Tests is a test project; Lib and App are not, so exactly one scanned.
            Assert.Equal(1, gi result "projectsScanned")

            // `add` is called three times across the two [<Fact>] tests.
            let entries = testEntries result
            Assert.Equal(3, gi result "testCount")
            Assert.Equal(3, entries.Length)

            // Every reported site lives in the test project, and the range object carries
            // coordinates only (no redundant `file` duplicated inside it).
            for entry in entries do
                Assert.Equal("Lib.Tests", gs entry "project")
                Assert.Contains("Tests.fs", gs entry "file")
                Assert.NotNull(entry["lineText"])
                Assert.Null(entry["range"]["file"])
                Assert.NotNull(entry["range"]["startLine"])

            let testCount = gi result "testCount"
            let projectsScanned = gi result "projectsScanned"
            output.WriteLine($"tests_for_symbol add: testCount={testCount}, projectsScanned={projectsScanned}")
        }

    [<Fact>]
    member _.``tests_for_symbol returns empty for a symbol used only in non-test projects``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            // `subtract` is referenced only by App (a non-test project), never by a test.
            let! result = bridge.TestsForSymbol(tfsArgs fx.Slnx "subtract")

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal(0, gi result "testCount")
            Assert.Empty(testEntries result)
            // The test project was still scanned — it simply contains no use of `subtract`.
            Assert.Equal(1, gi result "projectsScanned")
        }

    [<Fact>]
    member _.``tests_for_symbol tags each site with its enclosing [<Fact>] test name``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! result = bridge.TestsForSymbol(tfsArgs fx.Slnx "add")

            let names = enclosingTestNames result |> List.distinct |> List.sort

            // Both [<Fact>]-decorated test functions are recovered as the enclosing tests,
            // proving the upward attribute scan resolves the decorated binding name.
            Assert.Equal<string list>([ "add is commutative"; "add returns the sum" ], names)
        }

    [<Fact>]
    member _.``tests_for_symbol returns invalid_args naming symbolQuery on a blank query``() : Task =
        task {
            let bridge = FcsBridge()
            let! result = bridge.TestsForSymbol(tfsArgs fx.Slnx "   ")

            Assert.Equal("invalid_args", gs result "status")
            Assert.Contains("symbolQuery", gs result "message")
        }
