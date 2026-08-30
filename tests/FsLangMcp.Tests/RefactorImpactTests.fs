module FsLangMcp.Tests.RefactorImpactTests

// ─── #71: fcs_refactor_impact — read-only blast-radius + verification preview ─────
//
// The tool ORCHESTRATES existing backends (find / tests-for-symbol / check-compile-order
// / public-api) into one synthesis; these tests prove the synthesis, not the backends
// (those are covered by FindTests / TestsForSymbolTests / CompileOrderTests / PublicApiTests).
//
// Two fixtures, mirroring the proven patterns of the backend tests:
//   • ImpactFixture — a 3-project solution (Lib defines `add`/`subtract`; Lib.Tests is a
//     TEST project that calls `add` inside two [<Fact>] tests; App is a non-test project
//     that calls `subtract`). BUILT once so the cross-project `add` reference resolves.
//     Drives the cross-project-impact + covering-tests case and the public-member case.
//   • MoveFixture — the CompileOrderTests Wrong/Right pair (Uses.fs before Defs.fs →
//     FS0039 forward reference). RESTORED only (the Wrong project intentionally fails to
//     compile). Drives the kind=move compile-order case.

open System
open System.IO
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ── Cross-project fixture sources (Lib / Lib.Tests / App) ─────────────────────────

let private libFs =
    String.concat
        "\n"
        [ "module Lib.Math"
          ""
          "let add (a: int) (b: int) = a + b"
          ""
          "let subtract (a: int) (b: int) = a - b"
          ""
          "let paged (value: int) = value + 1"
          "" ]

let private pagedTestLines =
    [ for index in 1..105 -> $"        paged %d{index} |> ignore" ]

let private testsFs =
    String.concat
        "\n"
        ([ "namespace Lib.Tests"
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
           ""
           "    [<Fact>]"
           "    let ``paged symbol spans more than one result page`` () =" ]
         @ pagedTestLines
         @ [ "" ])

let private pagedAppLines =
    [ for index in 1..1005 -> $"let pagedUse%d{index} = paged %d{index}" ]

let private appFs =
    String.concat
        "\n"
        ([ "module App.Run"; ""; "open Lib.Math"; ""; "let go () = subtract 10 4"; "" ]
         @ pagedAppLines
         @ [ "" ])

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

// ── ImpactFixture: written + BUILT once, shared by the class ──────────────────────

type ImpactFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_impact_{runId}")

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
    let slnxPath = write "Impact.slnx" slnx
    let libFsproj = Path.Combine(root, "Lib", "Lib.fsproj")

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
            System.Threading.Thread.Sleep 1500
            buildWithRetry (attempt + 1)

    let buildExit, buildLog = buildWithRetry 1

    member _.Root = root
    member _.Slnx = slnxPath
    member _.LibFsproj = libFsproj
    member _.BuildExitCode = buildExit
    member _.BuildLog = buildLog

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

// ── MoveFixture: the compile-order Wrong/Right pair, RESTORED only ────────────────

let private usesFs =
    String.concat "\n" [ "module Uses"; ""; "let consume () ="; "    Defs.answer + 1"; "" ]

let private defsFs = String.concat "\n" [ "module Defs"; ""; "let answer = 41"; "" ]

let private projectWithOrder (firstFile: string) (secondFile: string) =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          "  <ItemGroup>"
          $"    <Compile Include=\"{firstFile}\" />"
          $"    <Compile Include=\"{secondFile}\" />"
          "  </ItemGroup>"
          "</Project>" ]

type MoveFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_impactmove_{runId}")

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    // WRONG: Uses.fs compiles BEFORE Defs.fs → the Defs.answer forward reference fails.
    let wrongFsproj = write "Wrong/Wrong.fsproj" (projectWithOrder "Uses.fs" "Defs.fs")
    do write "Wrong/Uses.fs" usesFs |> ignore
    do write "Wrong/Defs.fs" defsFs |> ignore

    let restoreOnce (fsproj: string) =
        let psi = ProcessStartInfo("dotnet", $"restore \"{fsproj}\" -nologo")
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

    let rec restoreWithRetry fsproj attempt =
        let code, log = restoreOnce fsproj

        if code = 0 || attempt >= 3 then
            code, log
        else
            System.Threading.Thread.Sleep 1500
            restoreWithRetry fsproj (attempt + 1)

    let wrongExit, wrongLog = restoreWithRetry wrongFsproj 1

    member _.Root = root
    member _.WrongFsproj = wrongFsproj
    member _.RestoreExit = wrongExit
    member _.RestoreLog = wrongLog

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

// ── JSON helpers ──────────────────────────────────────────────────────────────────

let private gi (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()
let private gb (node: JsonNode) (key: string) = node[key].GetValue<bool>()

let private arr (node: JsonNode) (key: string) =
    match node[key] with
    | :? JsonArray as a -> a |> Seq.toList
    | _ -> []

let private verifyLines (result: JsonNode) =
    arr result "verify" |> List.map (fun n -> n.GetValue<string>())

// ── Arg builder ─────────────────────────────────────────────────────────────────────

let private impactArgs (projectPath: string) (symbol: string) (kind: string option) : FcsRefactorImpactArgs =
    { symbol = Some symbol
      path = None
      line = None
      character = None
      newName = None
      kind = kind
      projectPath = Some projectPath }

let private refactorImpactWithActiveSolution
    (bridge: FcsBridge)
    (args: FcsRefactorImpactArgs)
    (activeSolution: string)
    : Task<JsonNode> =
    let methodInfo =
        typeof<FcsBridge>.GetMethods(
            System.Reflection.BindingFlags.Instance
            ||| System.Reflection.BindingFlags.Public
            ||| System.Reflection.BindingFlags.NonPublic
        )
        |> Array.filter (fun candidate -> candidate.Name = "RefactorImpact")
        |> Array.maxBy (fun candidate -> candidate.GetParameters().Length)

    let parameters = methodInfo.GetParameters()

    Assert.Contains(
        parameters,
        fun parameter -> parameter.Name = "activeProjectPath"
    )

    let invocationArgs =
        parameters
        |> Array.map (fun parameter ->
            match parameter.Name with
            | "args" -> box args
            | "activeProjectPath" -> box (Some activeSolution)
            | "renamePreview" -> null
            | name -> failwith $"Unexpected RefactorImpact parameter: %s{name}")

    methodInfo.Invoke(bridge, invocationArgs) :?> Task<JsonNode>

// ─── Cross-project impact + covering tests ────────────────────────────────────────

type ImpactCrossProjectTests(fx: ImpactFixture, output: ITestOutputHelper) =
    interface IClassFixture<ImpactFixture>

    [<Fact>]
    member _.``refactor_impact source fsproj uses the active solution for test discovery``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = FcsBridge()

            let! result =
                refactorImpactWithActiveSolution
                    bridge
                    (impactArgs fx.LibFsproj "add" None)
                    fx.Slnx

            Assert.Equal("succeeded", gs result "status")

            let impact = result["impact"]
            Assert.True(gb impact "complete")
            Assert.True(gb impact "crossProject")
            Assert.True((gi impact "projectCount" >= 2), "active solution must widen the main find blast radius")

            let tests = result["tests"]
            Assert.Equal("succeeded", gs tests "status")
            Assert.Equal("matched", gs tests "outcome")
            Assert.True(gb tests["coverage"] "complete")
            Assert.Equal(3, gi tests "count")
            Assert.Equal(2, gi tests "uniqueTestCount")
            Assert.Equal(3, gi tests "siteCount")
        }

    [<Fact>]
    member _.``refactor_impact preserves an indeterminate tests_for_symbol outcome``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = FcsBridge()

            // A source .fsproj cannot discover projects that reference it without a
            // separate active solution. The impact report must not turn that discovery
            // gap into a confident successful zero-test result.
            let! result = bridge.RefactorImpact(impactArgs fx.LibFsproj "add" None)

            Assert.NotEqual<string>("succeeded", gs result "status")

            let tests = result["tests"]
            Assert.Equal("unknown", gs tests "status")
            Assert.Equal("indeterminate", gs tests "outcome")
            Assert.False(gb tests["coverage"] "complete")
            Assert.Equal(0, gi tests "count")
            Assert.Equal(0, gi tests "siteCount")
            Assert.Contains(".sln/.slnx", gs tests "message")
        }

    [<Fact>]
    member _.``refactor_impact marks paginated find and test slices as incomplete``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = FcsBridge()

            // RefactorImpact currently asks Find for at most 1000 sites and
            // TestsForSymbol for at most 100. This fixture deliberately exceeds both.
            let! result = bridge.RefactorImpact(impactArgs fx.Slnx "paged" None)

            let impact = result["impact"]
            let tests = result["tests"]

            Assert.True((gi impact "totalSites" > 1000), "find fixture must exceed its internal page size")
            Assert.True((gi tests "siteCount" > 100), "test fixture must exceed its internal page size")
            Assert.Equal(100, (arr tests "tests").Length)

            Assert.NotEqual<string>("succeeded", gs result "status")
            Assert.False(gb result "complete")
            Assert.True(gb impact "truncated")
            Assert.False(gb impact "complete")
            Assert.True(gb impact "fileCountIsLowerBound")
            Assert.True(gb impact "projectCountIsLowerBound")
            Assert.False(gb impact "crossProjectKnown")
            Assert.Null(impact["crossProject"])
            Assert.True(gb tests "truncated")
            Assert.True(gb tests["coverage"] "complete")
            Assert.False(gb tests "complete")
        }

    [<Fact>]
    member _.``refactor_impact on a cross-project symbol reports the projects, files, and covering tests``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = FcsBridge()

            // `add` is defined in Lib and used inside Lib.Tests' two [<Fact>] tests →
            // the blast radius spans 2 projects, 2 unique tests, and 3 test sites.
            let! result = bridge.RefactorImpact(impactArgs fx.Slnx "add" None)

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal("auto", gs result "kind")

            let target = result["target"]
            Assert.Equal("add", gs target "symbol")
            Assert.Equal("symbol", gs target "resolvedVia")

            // Impact: cross-project (Lib defines + Lib.Tests uses), at least 2 projects.
            let impact = result["impact"]
            Assert.True((gi impact "totalSites" > 0), "expected at least one use site")
            Assert.True(gb impact "crossProject", "add is used across Lib and Lib.Tests")
            Assert.True((gi impact "projectCount" >= 2), "blast radius must span >= 2 projects")
            Assert.True((gi impact "fileCount" >= 2), "blast radius must span >= 2 files")
            Assert.NotEmpty(arr impact "sitesByProject")
            Assert.NotEmpty(arr impact "affectedFiles")

            // Tests: the two [<Fact>] tests reference `add` three times.
            let tests = result["tests"]
            Assert.Equal(3, gi tests "count")
            Assert.Equal(2, gi tests "uniqueTestCount")
            Assert.Equal(3, gi tests "siteCount")
            Assert.Equal(3, (arr tests "tests").Length)

            // Generic blast-radius preview: no move/signature sections requested.
            Assert.Null(result["compileOrder"])
            Assert.Null(result["apiSurface"])

            // verify checklist mentions the cross-project rebuild and the covering tests.
            let lines = verifyLines result
            Assert.NotEmpty(lines)
            Assert.Contains(lines, fun l -> l.Contains "cross-project")
            Assert.Contains(lines, fun l -> l.Contains "test reference")

            output.WriteLine(String.concat "\n" lines)
        }

    [<Fact>]
    member _.``refactor_impact kind=signature on a public member flags a breaking change``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit %d{fx.BuildExitCode}):\n%s{fx.BuildLog}")
            let bridge = FcsBridge()

            // `add` is a public let in module Lib.Math → a signature change is breaking.
            let! result = bridge.RefactorImpact(impactArgs fx.Slnx "add" (Some "signature"))

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal("signature", gs result "kind")

            let api = result["apiSurface"]
            Assert.NotNull(api)
            Assert.True(gb api "isPublic", "add is on the public API surface")

            let members = arr api "affectedPublicMembers"
            Assert.NotEmpty(members)
            Assert.Contains(members, fun m -> m["member"].GetValue<string>() = "add")

            // The breaking-change line drives the human checklist.
            let lines = verifyLines result
            Assert.Contains(lines, fun l -> l.Contains "BREAKING")

            output.WriteLine(String.concat "\n" lines)
        }

    [<Fact>]
    member _.``refactor_impact returns invalid_args when no target is given``() : Task =
        task {
            let bridge = FcsBridge()

            let! result =
                bridge.RefactorImpact
                    { symbol = None
                      path = None
                      line = None
                      character = None
                      newName = None
                      kind = None
                      projectPath = Some fx.Slnx }

            Assert.Equal("invalid_args", gs result "status")
            Assert.Contains("symbol", gs result "message")
        }

// ─── kind=move surfaces compile-order risk ────────────────────────────────────────

type ImpactMoveTests(fx: MoveFixture, output: ITestOutputHelper) =
    interface IClassFixture<MoveFixture>

    [<Fact>]
    member _.``refactor_impact kind=move surfaces a compile-order forward-reference problem``() : Task =
        task {
            Assert.True((fx.RestoreExit = 0), $"Fixture restore failed (exit %d{fx.RestoreExit}):\n%s{fx.RestoreLog}")
            let bridge = FcsBridge()

            // Wrong project: Uses.fs (referencing Defs.answer) compiles BEFORE Defs.fs, so
            // the module head `Defs` reads as FS0039 — a pure <Compile>-ORDER problem a
            // move would have to respect.
            let! result = bridge.RefactorImpact(impactArgs fx.WrongFsproj "Defs" (Some "move"))

            Assert.Equal("partial", gs result "status")
            Assert.False(gb result "complete")
            Assert.Equal("move", gs result "kind")

            let compileOrder = result["compileOrder"]
            Assert.NotNull(compileOrder)
            Assert.Equal(1, gi compileOrder "problemCount")
            Assert.NotEmpty(arr compileOrder "problems")

            // The move case never runs the public-API check.
            Assert.Null(result["apiSurface"])

            let lines = verifyLines result
            Assert.Contains(lines, fun l -> l.Contains "compile-order")

            output.WriteLine(String.concat "\n" lines)
        }
