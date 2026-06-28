module FsLangMcp.Tests.CompileOrderTests

// ─── #58: fcs_check_compile_order — F#'s order-of-compilation gotcha ──────────────
//
// A symbol used in a file that is DEFINED in a file appearing LATER in the project's
// <Compile> order is "not defined" purely because of file ordering (FS0039), not a
// missing `open`. FcsBridge.CheckCompileOrder correlates each FS0039 error with the
// project's resolved definitions and reports the ones whose definition compiles AFTER
// the use site.
//
// Fixture: ONE source pair (Uses.fs references Defs.answer; Defs.fs defines it) compiled
// in two different orders. The WRONG project lists Uses.fs before Defs.fs → 1 problem;
// the RIGHT project lists Defs.fs first → 0 problems. The Wrong project intentionally
// fails to compile, so the fixture only RESTORES (always green; Ionide.ProjInfo needs
// obj/ assets to resolve options, not a successful build).

open System
open System.IO
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ── Shared source pair (identical content in both projects) ──────────────────────

let private usesFs =
    String.concat "\n" [ "module Uses"; ""; "let consume () ="; "    Defs.answer + 1"; "" ]

let private defsFs =
    String.concat "\n" [ "module Defs"; ""; "let answer = 41"; "" ]

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

// ── Class fixture: written + restored ONCE, shared across the class ───────────────

type CompileOrderFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_order_{runId}")

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    // WRONG: Uses.fs compiles BEFORE Defs.fs → the Defs.answer forward reference fails.
    let wrongFsproj = write "Wrong/Wrong.fsproj" (projectWithOrder "Uses.fs" "Defs.fs")
    do write "Wrong/Uses.fs" usesFs |> ignore
    do write "Wrong/Defs.fs" defsFs |> ignore

    // RIGHT: Defs.fs first, Uses.fs second → compiles clean.
    let rightFsproj = write "Right/Right.fsproj" (projectWithOrder "Defs.fs" "Uses.fs")
    do write "Right/Uses.fs" usesFs |> ignore
    do write "Right/Defs.fs" defsFs |> ignore

    // Restore (not build): Ionide.ProjInfo's WorkspaceLoader needs obj/ assets to resolve
    // OtherOptions, but does NOT need a successful compile — which is the whole point of
    // the Wrong project. Restore always exits 0. Retry to absorb transient NuGet hiccups
    // while the rest of the suite runs collections in parallel.
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
    let rightExit, rightLog = restoreWithRetry rightFsproj 1

    member _.Root = root
    member _.WrongFsproj = wrongFsproj
    member _.RightFsproj = rightFsproj
    member _.RestoreExit = wrongExit + rightExit
    member _.RestoreLog = wrongLog + "\n" + rightLog

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

// ── JSON helpers ─────────────────────────────────────────────────────────────────

let private gi (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()

let private args (projectPath: string) : FcsCheckCompileOrderArgs =
    { projectPath = Some projectPath; symbol = None }

// ─────────────────────────────────────────────────────────────────────────────────

type CompileOrderTests(fx: CompileOrderFixture, output: ITestOutputHelper) =
    interface IClassFixture<CompileOrderFixture>

    [<Fact>]
    member _.``wrong <Compile> order: a forward reference flags exactly one compile-order problem``() : Task =
        task {
            Assert.True((fx.RestoreExit = 0), $"Fixture restore failed (exit {fx.RestoreExit}):\n{fx.RestoreLog}")
            let bridge = FcsBridge()

            let! result = bridge.CheckCompileOrder(args fx.WrongFsproj)

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal(1, gi result "projectsScanned")
            Assert.Equal(1, gi result "problemCount")

            let problems = result["compileOrderProblems"] :?> JsonArray
            Assert.Equal(1, problems.Count)

            let p = problems[0]
            let definedIn = p["definedIn"]
            let usedIn = p["usedIn"]
            let defIdx = gi definedIn "compileIndex"
            let useIdx = gi usedIn "compileIndex"

            // The unresolved name is the leftmost qualifier FCS reports in FS0039 — the
            // module `Defs`, whose definition (Defs.fs) compiles AFTER its use (Uses.fs).
            Assert.Equal("Defs", gs p "symbol")
            Assert.EndsWith("Defs.fs", gs definedIn "file")
            Assert.EndsWith("Uses.fs", gs usedIn "file")

            output.WriteLine($"definedIn.compileIndex={defIdx}, usedIn.compileIndex={useIdx}")

            // Definition compiles strictly AFTER the use → that is the whole gotcha.
            Assert.True((defIdx > useIdx), "definition must compile after the use site")

            // The offending source line and an actionable fix are surfaced verbatim.
            Assert.Contains("Defs.answer", gs usedIn "lineText")
            Assert.Contains("Defs.fs before Uses.fs", gs p "fix")
            Assert.Contains("<Compile>", gs p "fix")
        }

    [<Fact>]
    member _.``correct <Compile> order: zero compile-order problems``() : Task =
        task {
            Assert.True((fx.RestoreExit = 0), $"Fixture restore failed (exit {fx.RestoreExit}):\n{fx.RestoreLog}")
            let bridge = FcsBridge()

            let! result = bridge.CheckCompileOrder(args fx.RightFsproj)

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal(1, gi result "projectsScanned")
            Assert.Equal(0, gi result "problemCount")
            Assert.Empty(result["compileOrderProblems"] :?> JsonArray)
        }

    [<Fact>]
    member _.``symbol filter narrows to the unresolved name reported by FS0039``() : Task =
        task {
            Assert.True((fx.RestoreExit = 0), $"Fixture restore failed (exit {fx.RestoreExit}):\n{fx.RestoreLog}")
            let bridge = FcsBridge()

            // The FS0039 name is the module head `Defs`, so symbol="Defs" keeps the hit.
            let! hit = bridge.CheckCompileOrder({ args fx.WrongFsproj with symbol = Some "Defs" })
            Assert.Equal(1, gi hit "problemCount")

            // A different name (the *member* `answer`, which never appears as the
            // unresolved head) is filtered out — proving the filter keys on the FS0039
            // name, not on every identifier on the line.
            let! miss = bridge.CheckCompileOrder({ args fx.WrongFsproj with symbol = Some "answer" })
            Assert.Equal(0, gi miss "problemCount")
        }

    [<Fact>]
    member _.``no project context returns invalid_args``() : Task =
        task {
            let bridge = FcsBridge()
            let! result = bridge.CheckCompileOrder({ projectPath = None; symbol = None })

            Assert.Equal("invalid_args", gs result "status")
            Assert.Contains("project", gs result "message")
        }
