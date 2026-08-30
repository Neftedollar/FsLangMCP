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
          ""
          "let linkedTarget (value: int) = value * 2"
          ""
          "let boundaryTarget (value: int) = value + 1"
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
          "    let coveredHelper (value: int) = value + 1"
          ""
          "    [<Fact>]"
          "    let ``add returns the sum`` () ="
          "        let actual = add 2 3"
          "        let helperActual = coveredHelper 4"
          "        if actual <> 5 || helperActual <> 5 then failwith \"add broken\""
          ""
          "    [<Fact>]"
          "    let ``add is commutative`` () ="
          "        let left = add 1 2"
          "        let right = add 2 1"
          "        if left <> right then failwith \"add not commutative\""
          ""
          "    let topLevelFixture = add 40 2"
          ""
          "module First ="
          ""
          "    [<Fact>]"
          "    let ``boundary target works`` () ="
          "        let actual = boundaryTarget 1"
          "        if actual <> 2 then failwith \"boundary target broken\""
          "    do"
          "        boundaryTarget 5 |> ignore"
          "    do(*block boundary comment*)"
          "        boundaryTarget 6 |> ignore"
          "    do// line boundary comment"
          "        boundaryTarget 7 |> ignore"
          "    do(boundaryTarget 8 |> ignore)"
          ""
          "module Later ="
          "    module Nested ="
          "        let boundaryFixture = boundaryTarget 2"
          ""
          "type FirstBoundaryType() ="
          "    [<Fact>]"
          "    member _.``recursive type boundary target works`` () ="
          "        boundaryTarget 3 |> ignore"
          "and SecondBoundaryType() ="
          "    do boundaryTarget 4 |> ignore"
          "" ]

let private linkedTestsFs =
    String.concat
        "\n"
        [ "namespace Linked.Tests"
          ""
          "open System"
          "open Lib.Math"
          ""
          "type FactAttribute() ="
          "    inherit Attribute()"
          ""
          "module LinkedTests ="
          ""
          "    [<Fact>]"
          "    let ``linked target works`` () ="
          "        let actual = linkedTarget 21"
          "        if actual <> 42 then failwith \"linked target broken\""
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
          $"  <ItemGroup><Compile Include=\"%s{sourceFile}\" /></ItemGroup>"
          "</Project>" ]

let private testProject (sourceFile: string) (refRelative: string) =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup>"
          "    <TargetFramework>net10.0</TargetFramework>"
          "    <IsTestProject>true</IsTestProject>"
          "  </PropertyGroup>"
          $"  <ItemGroup><Compile Include=\"%s{sourceFile}\" /></ItemGroup>"
          $"  <ItemGroup><ProjectReference Include=\"%s{refRelative}\" /></ItemGroup>"
          "</Project>" ]

let private linkedTestProject =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup>"
          "    <TargetFramework>net10.0</TargetFramework>"
          "    <IsTestProject>true</IsTestProject>"
          "  </PropertyGroup>"
          "  <ItemGroup><Compile Include=\"../Shared/LinkedTests.fs\" Link=\"LinkedTests.fs\" /></ItemGroup>"
          "  <ItemGroup><ProjectReference Include=\"../Lib/Lib.fsproj\" /></ItemGroup>"
          "</Project>" ]

let private refProject (sourceFile: string) (refRelative: string) =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          $"  <ItemGroup><Compile Include=\"%s{sourceFile}\" /></ItemGroup>"
          $"  <ItemGroup><ProjectReference Include=\"%s{refRelative}\" /></ItemGroup>"
          "</Project>" ]

let private slnx =
    String.concat
        "\n"
        [ "<Solution>"
          "  <Project Path=\"Lib/Lib.fsproj\" />"
          "  <Project Path=\"Lib.Tests/Lib.Tests.fsproj\" />"
          "  <Project Path=\"App/App.fsproj\" />"
          "</Solution>" ]

let private linkedSlnx =
    String.concat
        "\n"
        [ "<Solution>"
          "  <Project Path=\"Lib/Lib.fsproj\" />"
          "  <Project Path=\"Linked.One/Linked.One.fsproj\" />"
          "  <Project Path=\"Linked.Two/Linked.Two.fsproj\" />"
          "</Solution>" ]

let private buildSlnx =
    String.concat
        "\n"
        [ "<Solution>"
          "  <Project Path=\"Lib/Lib.fsproj\" />"
          "  <Project Path=\"Lib.Tests/Lib.Tests.fsproj\" />"
          "  <Project Path=\"App/App.fsproj\" />"
          "  <Project Path=\"Linked.One/Linked.One.fsproj\" />"
          "  <Project Path=\"Linked.Two/Linked.Two.fsproj\" />"
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

    let libFsproj = write "Lib/Lib.fsproj" (leafProject "Lib.fs")
    do write "Lib/Lib.fs" libFs |> ignore
    let testFsproj = write "Lib.Tests/Lib.Tests.fsproj" (testProject "Tests.fs" "../Lib/Lib.fsproj")
    do write "Lib.Tests/Tests.fs" testsFs |> ignore
    do write "App/App.fsproj" (refProject "App.fs" "../Lib/Lib.fsproj") |> ignore
    do write "App/App.fs" appFs |> ignore
    do write "Shared/LinkedTests.fs" linkedTestsFs |> ignore
    do write "Linked.One/Linked.One.fsproj" linkedTestProject |> ignore
    do write "Linked.Two/Linked.Two.fsproj" linkedTestProject |> ignore
    let slnxPath = write "TestsForSymbol.slnx" slnx
    let linkedSlnxPath = write "LinkedTestsForSymbol.slnx" linkedSlnx
    let buildSlnxPath = write "BuildTestsForSymbol.slnx" buildSlnx

    let dotnetHostPath =
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue "dotnet"

    // dotnet build is ground truth and produces Lib.dll so the test project's FCS sweep
    // resolves the cross-project `add` reference. Isolation/retry flags mirror FindFixture
    // (parallel restore on a shared P2P races on *.nuget.g.props; in-process MSBuild from
    // sibling test collections can collide).
    let buildOnce () =
        let psi =
            ProcessStartInfo(
                dotnetHostPath,
                $"build \"%s{buildSlnxPath}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
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
    let bridge = FcsBridge()

    member _.Root = root
    member _.Slnx = slnxPath
    member _.LinkedSlnx = linkedSlnxPath
    member _.LibFsproj = libFsproj
    member _.TestFsproj = testFsproj
    member internal _.Bridge = bridge
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
let private gb (node: JsonNode) (key: string) = node[key].GetValue<bool>()

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
      maxResults = None
      timeoutMs = None
      cursor = None }

// ─────────────────────────────────────────────────────────────────────────────────

type TestsForSymbolTests(fx: TestsForSymbolFixture, output: ITestOutputHelper) =
    interface IClassFixture<TestsForSymbolFixture>

    [<Fact>]
    member _.``tests_for_symbol returns the test-file sites for a symbol covered by tests``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = fx.Bridge

            let! result =
                bridge.TestsForSymbol({ tfsArgs fx.Slnx "add" with timeoutMs = Some 30_000 })

            Assert.True(
                gs result "status" = "succeeded",
                $"Unexpected tests_for_symbol payload: %s{result.ToJsonString()}"
            )
            Assert.Equal("add", gs result "symbol")

            // Only Lib.Tests is a test project; Lib and App are not, so exactly one scanned.
            Assert.Equal(1, gi result "projectsScanned")

            // `add` is called three times across two [<Fact>] tests plus once in
            // top-level fixture code. testCount remains the legacy site count;
            // uniqueTestCount distinguishes the enclosing tests.
            let entries = testEntries result
            Assert.Equal(4, gi result "siteCount")
            Assert.Equal(4, gi result "testCount")
            Assert.Equal(2, gi result "uniqueTestCount")
            Assert.Equal(4, entries.Length)
            Assert.True(gb result["coverage"] "complete")

            // Every reported site lives in the test project, and the range object carries
            // coordinates only (no redundant `file` duplicated inside it).
            for entry in entries do
                Assert.Equal("Lib.Tests", gs entry "project")
                Assert.Contains("Tests.fs", gs entry "file")
                Assert.NotNull(entry["lineText"])
                Assert.Null(entry["range"]["file"])
                Assert.NotNull(entry["range"]["startLine"])

            let testCount = gi result "testCount"
            let siteCount = gi result "siteCount"
            let projectsScanned = gi result "projectsScanned"
            output.WriteLine($"tests_for_symbol add: testCount=%d{testCount}, siteCount=%d{siteCount}, projectsScanned=%d{projectsScanned}")
        }

    [<Fact>]
    member _.``tests_for_symbol returns empty for a symbol used only in non-test projects``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = fx.Bridge

            // `subtract` is referenced only by App (a non-test project), never by a test.
            let! result = bridge.TestsForSymbol(tfsArgs fx.Slnx "subtract")

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal(0, gi result "testCount")
            Assert.Equal(0, gi result "siteCount")
            Assert.Empty(testEntries result)
            // The test project was still scanned — it simply contains no use of `subtract`.
            Assert.Equal(1, gi result "projectsScanned")
        }

    [<Fact>]
    member _.``tests_for_symbol tags each site with its enclosing [<Fact>] test name``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = fx.Bridge

            let! result = bridge.TestsForSymbol(tfsArgs fx.Slnx "add")

            let names = enclosingTestNames result |> List.distinct |> List.sort

            // Both [<Fact>]-decorated test functions are recovered as the enclosing tests,
            // proving the upward attribute scan resolves the decorated binding name.
            Assert.Equal<string list>([ "add is commutative"; "add returns the sum" ], names)
        }

    [<Fact>]
    member _.``tests_for_symbol does not attribute top-level fixture code to the preceding test``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = fx.Bridge

            let! result = bridge.TestsForSymbol(tfsArgs fx.Slnx "add")

            let fixtureSite =
                testEntries result
                |> List.find (fun site -> gs site "lineText" |> fun line -> line.Contains("topLevelFixture"))

            Assert.Null(fixtureSite["enclosingTest"])
            Assert.Equal(4, gi result "testCount")
            Assert.Equal(2, gi result "uniqueTestCount")
            Assert.Equal(4, gi result "siteCount")
        }

    [<Fact>]
    member _.``tests_for_symbol stops enclosing-test attribution at a module boundary``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = fx.Bridge

            let! result = bridge.TestsForSymbol(tfsArgs fx.Slnx "boundaryTarget")

            let sites = testEntries result
            let testSite = sites |> List.find (fun site -> gs site "lineText" |> fun line -> line.Contains("let actual"))
            let fixtureSite = sites |> List.find (fun site -> gs site "lineText" |> fun line -> line.Contains("boundaryFixture"))
            let recursiveTestSite =
                sites |> List.find (fun site -> gs site "lineText" |> fun line -> line.Contains("boundaryTarget 3"))

            let recursiveFixtureSite =
                sites |> List.find (fun site -> gs site "lineText" |> fun line -> line.Contains("boundaryTarget 4"))

            let moduleInitializerSites =
                [ 5..8 ]
                |> List.map (fun value ->
                    sites
                    |> List.find (fun site ->
                        gs site "lineText"
                        |> fun line -> line.Contains($"boundaryTarget %d{value}")))

            Assert.Equal("boundary target works", gs testSite "enclosingTest")
            Assert.Null(fixtureSite["enclosingTest"])
            Assert.Equal("recursive type boundary target works", gs recursiveTestSite "enclosingTest")
            Assert.Null(recursiveFixtureSite["enclosingTest"])

            for moduleInitializerSite in moduleInitializerSites do
                Assert.Null(moduleInitializerSite["enclosingTest"])

            Assert.Equal(2, gi result "uniqueTestCount")
            Assert.Equal(8, gi result "siteCount")
        }

    [<Fact>]
    member _.``tests_for_symbol keeps linked-file sites and tests distinct per project``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = fx.Bridge

            let! result = bridge.TestsForSymbol(tfsArgs fx.LinkedSlnx "linkedTarget")

            let sites = testEntries result
            let projects = sites |> List.map (fun site -> gs site "project") |> Set.ofList
            let physicalFiles = sites |> List.map (fun site -> gs site "file") |> Set.ofList

            Assert.Equal(2, sites.Length)
            Assert.Single(physicalFiles) |> ignore
            Assert.Equal<Set<string>>(Set.ofList [ "Linked.One"; "Linked.Two" ], projects)
            Assert.All(sites, fun site -> Assert.Equal("linked target works", gs site "enclosingTest"))
            Assert.Equal(2, gi result "testCount")
            Assert.Equal(2, gi result "uniqueTestCount")
            Assert.Equal(2, gi result "siteCount")
        }

    [<Fact>]
    member _.``tests_for_symbol excludes the symbol definition from sites``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = fx.Bridge

            let! result = bridge.TestsForSymbol(tfsArgs fx.Slnx "coveredHelper")

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal(1, gi result "siteCount")
            Assert.Equal(1, gi result "testCount")
            Assert.Equal(1, gi result "uniqueTestCount")

            let onlySite = testEntries result |> List.exactlyOne
            Assert.Contains("coveredHelper 4", gs onlySite "lineText")
            Assert.Equal("add returns the sum", gs onlySite "enclosingTest")
        }

    [<Fact>]
    member _.``tests_for_symbol source fsproj without active solution returns a widening hint instead of confident zero``() : Task =
        task {
            let bridge = fx.Bridge

            let! result = bridge.TestsForSymbol(tfsArgs fx.LibFsproj "add")

            Assert.Equal("unknown", gs result "status")
            Assert.Equal("indeterminate", gs result "outcome")
            Assert.False(gb result["coverage"] "complete")
            Assert.Equal(0, gi result "projectsRequested")
            Assert.Equal(0, gi result "projectsScanned")
            Assert.Contains("No active .sln/.slnx", gs result "message")
            Assert.Contains(".sln/.slnx", gs result "message")
        }

    [<Fact>]
    member _.``tests_for_symbol source fsproj uses active solution to discover and scan relevant tests``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = fx.Bridge

            let! result =
                bridge.TestsForSymbol(
                    tfsArgs fx.LibFsproj "add",
                    activeProjectPath = fx.Slnx
                )

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal("matched", gs result "outcome")
            Assert.True(gb result "usedActiveSolutionContext")
            Assert.True(gb result["coverage"] "complete")
            Assert.Equal(Path.GetFullPath fx.LibFsproj, gs result "requestedProjectPath")
            Assert.Equal(Path.GetFullPath fx.Slnx, gs result "discoveryProjectPath")
            Assert.Equal(1, gi result "projectsRequested")
            Assert.Equal(1, gi result "projectsScanned")
            Assert.Equal(4, gi result "siteCount")
            Assert.Equal(4, gi result "testCount")
            Assert.Equal(2, gi result "uniqueTestCount")
        }

    [<Fact>]
    member _.``tests_for_symbol directly scoped to a test fsproj still scans that project``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = fx.Bridge

            let! result = bridge.TestsForSymbol(tfsArgs fx.TestFsproj "add")

            Assert.Equal("succeeded", gs result "status")
            Assert.False(gb result "usedActiveSolutionContext")
            Assert.True(gb result["coverage"] "complete")
            Assert.Equal(1, gi result "projectsRequested")
            Assert.Equal(1, gi result "projectsScanned")
            Assert.Equal(4, gi result "siteCount")
            Assert.Equal(4, gi result "testCount")
            Assert.Equal(2, gi result "uniqueTestCount")
        }

    [<Fact>]
    member _.``tests_for_symbol uses one timeout budget and reports an incomplete zero honestly``() : Task =
        task {
            let bridge = fx.Bridge

            let! result =
                bridge.TestsForSymbol({ tfsArgs fx.Slnx "add" with timeoutMs = Some 0 })

            Assert.Equal("unknown", gs result "status")
            Assert.Equal("indeterminate", gs result "outcome")
            Assert.False(gb result["coverage"] "complete")
            Assert.Equal(1, gi result "projectsRequested")
            Assert.Equal(0, gi result "projectsScanned")
            Assert.Equal(0, gi result "projectsFailed")
            Assert.Equal(1, gi result "projectsTimedOut")
            Assert.Equal(0, gi result "projectsBusy")
            Assert.Equal(0, gi result "siteCount")
            Assert.Equal(0, gi result "testCount")
            Assert.Equal(0, gi result "uniqueTestCount")
            Assert.Contains("incomplete", gs result "message")
        }

    [<Fact>]
    member _.``tests_for_symbol does not count a project scanned when site classification times out``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")

            let bridge =
                FcsBridge(
                    testsForSymbolSiteScanBeforeUseOverride =
                        (fun () -> raise (TimeoutException("controlled site scan timeout")))
                )

            let! result = bridge.TestsForSymbol(tfsArgs fx.TestFsproj "add")
            let coverage = result["coverage"]
            let perProject = result["perProject"] :?> JsonArray
            let project = perProject[0]

            Assert.Equal("unknown", gs result "status")
            Assert.Equal("indeterminate", gs result "outcome")
            Assert.False(gb coverage "complete")
            Assert.Equal(1, gi result "projectsRequested")
            Assert.Equal(0, gi result "projectsScanned")
            Assert.Equal(0, gi result "projectsFailed")
            Assert.Equal(1, gi result "projectsTimedOut")
            Assert.Equal(0, gi result "projectsBusy")
            Assert.Equal("timed_out", gs project "status")
            Assert.Equal("timeout", gs project "errorKind")

            let reconciledProjects =
                gi result "projectsScanned"
                + gi result "projectsFailed"
                + gi result "projectsTimedOut"
                + gi result "projectsBusy"

            Assert.Equal(gi result "projectsRequested", reconciledProjects)
        }

    [<Fact>]
    member _.``tests_for_symbol reports actual-worker admission rejection as retryable busy``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let firstStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseFirst = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable workerStarts = 0

            let controlledSweep
                (_projectPath: string)
                : Task<FSharp.Compiler.CodeAnalysis.FSharpSymbolUse array * FSharp.Compiler.Diagnostics.FSharpDiagnostic array> =
                task {
                    let ordinal = System.Threading.Interlocked.Increment(&workerStarts)

                    if ordinal = 1 then
                        firstStarted.TrySetResult(()) |> ignore
                        do! releaseFirst.Task

                    return [||], [||]
                }

            let bridge =
                FcsBridge(
                    freshProjectCheckConcurrencyOverride = 1,
                    projectSweepWorkerOverride = controlledSweep,
                    // Deterministically model the deadline crossing while a real
                    // admission-busy exception propagates. Busy must keep priority.
                    testsForSymbolFailureDeadlineExpiredOverride = (fun () -> true)
                )

            try
                let linkedOne = Path.Combine(fx.Root, "Linked.One", "Linked.One.fsproj")
                let linkedTwo = Path.Combine(fx.Root, "Linked.Two", "Linked.Two.fsproj")

                for projectPath in [ fx.TestFsproj; linkedOne; linkedTwo ] do
                    let! snapshot = bridge.GetEvaluatedProjectSnapshot(projectPath)
                    Assert.True(Result.isOk snapshot)

                let firstCaller =
                    bridge.TestsForSymbol({ tfsArgs fx.TestFsproj "add" with timeoutMs = Some 500 })

                do! firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5.0))
                let! firstResult = firstCaller.WaitAsync(TimeSpan.FromSeconds(5.0))
                Assert.Equal(1, gi firstResult "projectsTimedOut")
                Assert.Equal(0, gi firstResult "projectsScanned")

                let! busyResult =
                    bridge.TestsForSymbol({ tfsArgs fx.LinkedSlnx "linkedTarget" with timeoutMs = Some 5_000 })

                Assert.Equal("unknown", gs busyResult "status")
                Assert.Equal("indeterminate", gs busyResult "outcome")
                Assert.Equal(2, gi busyResult "projectsRequested")
                Assert.Equal(0, gi busyResult "projectsScanned")
                Assert.Equal(0, gi busyResult "projectsFailed")
                Assert.Equal(0, gi busyResult "projectsTimedOut")
                Assert.Equal(2, gi busyResult "projectsBusy")

                let entries = busyResult["perProject"] :?> JsonArray
                Assert.Equal(2, entries.Count)

                for entry in entries do
                    Assert.Equal("busy", gs entry "status")
                    Assert.Equal("fcs_worker_busy", gs entry "errorKind")
                    Assert.True(gb entry "retryable")

                Assert.Equal(1, System.Threading.Volatile.Read(&workerStarts))
                Assert.Equal(2L, bridge.ProjectUsesRejectedCount)
                releaseFirst.TrySetResult(()) |> ignore

                let settle = Stopwatch.StartNew()

                while
                    (bridge.ProjectUsesActiveCount <> 0 || bridge.ProjectUsesInFlightCount <> 0)
                    && settle.Elapsed < TimeSpan.FromSeconds(5.0)
                    do
                    do! Task.Delay(20)

                Assert.Equal(0, bridge.ProjectUsesActiveCount)
                Assert.Equal(0, bridge.ProjectUsesInFlightCount)
            finally
                releaseFirst.TrySetResult(()) |> ignore
        }

    [<Fact>]
    member _.``tests_for_symbol paginates sites while keeping full site and unique-test counts``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = fx.Bridge

            let! first =
                bridge.TestsForSymbol({ tfsArgs fx.Slnx "add" with maxResults = Some 2 })

            Assert.Equal(2, testEntries first |> List.length)
            Assert.Equal(4, gi first "siteCount")
            Assert.Equal(4, gi first "testCount")
            Assert.Equal(2, gi first "uniqueTestCount")
            Assert.True(gb first "truncated")
            Assert.Equal(4, gi first["totalEstimate"] "sites")

            let nextCursor = gs first "nextCursor"

            let! second =
                bridge.TestsForSymbol(
                    { tfsArgs fx.Slnx "add" with
                        maxResults = Some 100
                        cursor = Some nextCursor }
                )

            Assert.Equal(2, testEntries second |> List.length)
            Assert.Equal(4, gi second "siteCount")
            Assert.Equal(4, gi second "testCount")
            Assert.Equal(2, gi second "uniqueTestCount")
            Assert.False(gb second "truncated")
            Assert.Null(second["nextCursor"])
        }

    [<Fact>]
    member _.``tests_for_symbol returns invalid_args naming symbolQuery on a blank query``() : Task =
        task {
            let bridge = fx.Bridge
            let! result = bridge.TestsForSymbol(tfsArgs fx.Slnx "   ")

            Assert.Equal("invalid_args", gs result "status")
            Assert.Contains("symbolQuery", gs result "message")
        }
