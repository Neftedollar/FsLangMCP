module FsLangMcp.Tests.DeadCodeTests

// ─── #70: fcs_dead_code — conservative dead-code candidate analysis ────────────────
//
// Fixture: a single project whose source declares four module-level bindings:
//   • usedHelper  — `let private`, referenced by `run` → LIVE, never a candidate;
//   • orphan      — `let private`, referenced nowhere → DEAD, a candidate by default;
//   • publicOrphan— `let` (public), referenced nowhere → candidate ONLY with includePublic;
//   • run         — `let` (public), references usedHelper, itself unreferenced.
// So the default pass flags exactly `orphan`; includePublic=true additionally surfaces
// the unused public bindings, but `usedHelper` stays off the list either way.
// Built once per class (IClassFixture) so FSharp.Core resolves for the FCS sweep.

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
        [ "module DeadLib"
          ""
          "// private + referenced by `run` below → LIVE"
          "let private usedHelper (x: int) = x + 1"
          ""
          "// private + referenced nowhere → DEAD (candidate by default)"
          "let private orphan (x: int) = x * 2"
          ""
          "// public + referenced nowhere → candidate only with includePublic"
          "let publicOrphan (x: int) = x + 100"
          ""
          "// public entry that uses usedHelper, keeping it live"
          "let run (x: int) = usedHelper x"
          "" ]

let private leafProject (sourceFile: string) =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          $"  <ItemGroup><Compile Include=\"{sourceFile}\" /></ItemGroup>"
          "</Project>" ]

// ── Class fixture: written + built ONCE, shared by every test in the class ─────────

type DeadCodeFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_dc_{runId}")

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    do write "DeadLib.fs" libFs |> ignore
    let fsprojPath = write "DeadLib.fsproj" (leafProject "DeadLib.fs")

    // dotnet build is ground truth and produces FSharp.Core resolution so the FCS sweep
    // type-checks. Isolation/retry flags mirror the TestsForSymbol fixture (parallel
    // restore races on *.nuget.g.props; in-process MSBuild from sibling collections can
    // collide).
    let buildOnce () =
        let psi =
            ProcessStartInfo(
                "dotnet",
                $"build \"{fsprojPath}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
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
    member _.Fsproj = fsprojPath
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

let private candidateList (result: JsonNode) =
    match result["candidates"] with
    | :? JsonArray as arr -> arr |> Seq.toList
    | _ -> []

let private candidateNames (result: JsonNode) =
    candidateList result |> List.map (fun c -> c["name"].GetValue<string>())

let private caveatList (result: JsonNode) =
    match result["caveats"] with
    | :? JsonArray as arr -> arr |> Seq.toList
    | _ -> []

// ── Arg builder ────────────────────────────────────────────────────────────────────

let private dcArgs (projectPath: string) (includePublic: bool option) : FcsDeadCodeArgs =
    { projectPath = Some projectPath
      includePublic = includePublic
      maxResults = None }

// ─────────────────────────────────────────────────────────────────────────────────

type DeadCodeTests(fx: DeadCodeFixture, output: ITestOutputHelper) =
    interface IClassFixture<DeadCodeFixture>

    [<Fact>]
    member _.``dead_code flags an unused private binding and skips a used private and an unused public``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! result = bridge.DeadCode(dcArgs fx.Fsproj None)

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal(1, gi result "projectsScanned")

            let names = candidateNames result
            let nameStr = String.concat ", " names
            output.WriteLine($"default candidates: {nameStr}")

            // The unused private binding is the lone candidate.
            Assert.Contains("orphan", names)
            // A used private binding is LIVE (referenced by `run`).
            Assert.DoesNotContain("usedHelper", names)
            // Public bindings are excluded by default.
            Assert.DoesNotContain("publicOrphan", names)
            Assert.DoesNotContain("run", names)

            // candidateCount mirrors the returned set when nothing is truncated.
            Assert.Equal(candidateList result |> List.length, gi result "candidateCount")
            Assert.False(gb result "truncated")
        }

    [<Fact>]
    member _.``dead_code candidate carries name, accessibility, declaredIn and a find note``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! result = bridge.DeadCode(dcArgs fx.Fsproj None)

            let orphan =
                candidateList result
                |> List.find (fun c -> c["name"].GetValue<string>() = "orphan")

            Assert.Equal("private", gs orphan "accessibility")
            // declaredIn locates the binding: a real file path + a range with a start line.
            let declaredIn = orphan["declaredIn"]
            let startLine = declaredIn["range"]["startLine"]
            Assert.Contains("DeadLib.fs", gs declaredIn "file")
            Assert.NotNull(startLine)
            // The note steers the agent to verify with `find` before deleting.
            Assert.Contains("find", gs orphan "note")
        }

    [<Fact>]
    member _.``dead_code with includePublic surfaces the unused public binding``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! result = bridge.DeadCode(dcArgs fx.Fsproj (Some true))

            let names = candidateNames result
            let nameStr = String.concat ", " names
            output.WriteLine($"includePublic candidates: {nameStr}")

            // The unused private binding is still flagged.
            Assert.Contains("orphan", names)
            // The unused public binding now appears, because includePublic widens the scope.
            Assert.Contains("publicOrphan", names)
            // The used private binding stays LIVE regardless of includePublic.
            Assert.DoesNotContain("usedHelper", names)
        }

    [<Fact>]
    member _.``dead_code always emits a non-empty caveats list``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            let bridge = FcsBridge()

            let! result = bridge.DeadCode(dcArgs fx.Fsproj None)

            // Caveats are always present — the tool's conservatism is communicated, not implied.
            Assert.NotEmpty(caveatList result)
            Assert.NotNull(result["candidates"])
            Assert.NotNull(result["candidateCount"])
            Assert.NotNull(result["truncated"])
        }

    [<Fact>]
    member _.``dead_code returns invalid_args without a project context``() : Task =
        task {
            let bridge = FcsBridge()

            let! result =
                bridge.DeadCode
                    { projectPath = None
                      includePublic = None
                      maxResults = None }

            Assert.Equal("invalid_args", gs result "status")
            Assert.Contains("project context", gs result "message")
        }
