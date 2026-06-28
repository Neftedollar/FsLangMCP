module FsLangMcp.Tests.CheckTests

// ─── #128 Stage 1: the consolidated `check` tool — one trustworthy verdict ───────
//
// The five check-cluster tools (workspace_diagnostics, fsharp_compile, fcs_check_file,
// fcs_parse_and_check_file, fcs_validate_snippet) leave an agent guessing: a `{}` /
// diagnosticsFileCount:0 from workspace_diagnostics is indistinguishable from "clean"
// vs "not analyzed yet", so agents fall back to `dotnet build` (#100). `check` collapses
// that into a single field — `verdict` ∈ { clean, errors, unknown } — backed by a FRESH
// in-process FCS re-check on the default speed="trusted".
//
// Fixture: one leaf project Probe with two source files (Helpers.fs consumed by Main.fs)
// built ONCE, so cross-file edits exercise the stale-`{}` property. Each test sets the
// on-disk source it needs first (xUnit serialises methods within a class), so the FCS
// re-check is what makes the verdict reflect the current revision.

open System
open System.IO
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge

// ── Fixture sources ────────────────────────────────────────────────────────────

let private cleanHelpers =
    String.concat "\n" [ "module Probe.Helpers"; ""; "let add (a: int) (b: int) : int = a + b"; "" ]

// Breaks the contract Main.fs depends on: `add` now returns string, so Main's
// `let result : int = add 1 2` becomes a CROSS-FILE type error.
let private brokenHelpers =
    String.concat "\n" [ "module Probe.Helpers"; ""; "let add (a: int) (b: int) : string = string (a + b)"; "" ]

let private cleanMain =
    String.concat "\n" [ "module Probe.Main"; ""; "open Probe.Helpers"; ""; "let result: int = add 1 2"; "" ]

// Self-contained single-file type error (string assigned to int).
let private errorMain =
    String.concat "\n" [ "module Probe.Main"; ""; "let x: int = \"oops\""; "" ]

let private probeProject =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          "  <ItemGroup>"
          "    <Compile Include=\"Helpers.fs\" />"
          "    <Compile Include=\"Main.fs\" />"
          "  </ItemGroup>"
          "</Project>" ]

// ── Class fixture: written + built ONCE, shared by every test in the class ───────

type CheckFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_check_{runId}")

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    let probeFsproj = write "Probe/Probe.fsproj" probeProject
    let helpersFs = write "Probe/Helpers.fs" cleanHelpers
    let mainFs = write "Probe/Main.fs" cleanMain

    // dotnet build once so Ionide.ProjInfo can resolve options (restore + design-time
    // build). After this, FCS re-checks read source files from disk — no rebuild needed
    // for the cross-file stale test. Isolation/retry flags mirror FindFixture to survive
    // the parallel-collection MSBuild contention the find author hit.
    let buildOnce () =
        let psi =
            ProcessStartInfo(
                "dotnet",
                $"build \"{probeFsproj}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
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

    /// Restore the fixture sources to their clean baseline. Called at the top of every
    /// test so method ordering cannot leak a previous test's on-disk edit.
    member _.ResetClean() =
        File.WriteAllText(helpersFs, cleanHelpers)
        File.WriteAllText(mainFs, cleanMain)

    member _.Root = root
    member _.ProbeFsproj = probeFsproj
    member _.HelpersFs = helpersFs
    member _.MainFs = mainFs
    member _.BuildExitCode = buildExit
    member _.BuildLog = buildLog

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                try
                    Directory.Delete(root, true)
                with _ ->
                    ()

// ── JSON helpers ─────────────────────────────────────────────────────────────────

let private gi (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private gb (node: JsonNode) (key: string) = node[key].GetValue<bool>()
let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()

// ── Arg builder ──────────────────────────────────────────────────────────────────

let private bareCheck: CheckArgs =
    { scope = None
      path = None
      snippet = None
      fileGlob = None
      mode = None
      speed = None
      severity = None
      projectPath = None
      timeoutMs = None }

// ─────────────────────────────────────────────────────────────────────────────────

type CheckTests(fx: CheckFixture) =
    interface IClassFixture<CheckFixture>

    [<Fact>]
    member _.``bare check() on a clean project returns verdict=clean with zero errors``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            // Bare call: the only thing set is the active-project fall-back Program.fs
            // injects — every other arg is None.
            let! result = bridge.Check({ bareCheck with projectPath = Some fx.ProbeFsproj })

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal("clean", gs result "verdict")
            Assert.True(gb result "analyzed", "a fresh trusted check must report analyzed=true")
            Assert.Equal(0, gi result "errorCount")
            Assert.Equal("project", gs result "scope")
            Assert.Equal("fcs", gs result "via")
            Assert.True(gb result "groundTruth", "a clean FCS verdict is ground truth")
        }

    [<Fact>]
    member _.``bare check() on a project whose file has a type error returns verdict=errors``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            File.WriteAllText(fx.MainFs, errorMain)
            let bridge = FcsBridge()

            let! result = bridge.Check({ bareCheck with projectPath = Some fx.ProbeFsproj })

            Assert.Equal("errors", gs result "verdict")
            Assert.True(gi result "errorCount" > 0, "the deliberate type error must be counted")

            // The error itself is surfaced (default severity floor = error).
            let diagnostics = result["diagnostics"] :?> JsonArray
            Assert.True(diagnostics.Count > 0, "the error diagnostic must be surfaced")
        }

    [<Fact>]
    member _.``check(path=...) on a file with a type error returns verdict=errors``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            File.WriteAllText(fx.MainFs, errorMain)
            let bridge = FcsBridge()

            let! result =
                bridge.Check(
                    { bareCheck with
                        path = Some fx.MainFs
                        projectPath = Some fx.ProbeFsproj }
                )

            Assert.Equal("file", gs result "scope")
            Assert.Equal("errors", gs result "verdict")
            Assert.True(gi result "errorCount" > 0)
        }

    [<Fact>]
    member _.``check(snippet=...) returns errors for a bad snippet and clean for a valid one``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            let! bad =
                bridge.Check(
                    { bareCheck with
                        snippet = Some "module Probe.SnippetBad\n\nlet x: int = \"oops\"\n"
                        projectPath = Some fx.ProbeFsproj }
                )

            Assert.Equal("snippet", gs bad "scope")
            Assert.Equal("errors", gs bad "verdict")
            Assert.True(gi bad "errorCount" > 0)

            let! good =
                bridge.Check(
                    { bareCheck with
                        snippet = Some "module Probe.SnippetOk\n\nlet x: int = 42\n"
                        projectPath = Some fx.ProbeFsproj }
                )

            Assert.Equal("clean", gs good "verdict")
            Assert.Equal(0, gi good "errorCount")
        }

    [<Fact>]
    member _.``STALE-GUARD: a fresh trusted check after an on-disk cross-file edit never reports a false-clean``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            // 1. Pristine revision → clean, freshly analyzed. This populates FCS caches
            //    with a CLEAN snapshot — exactly the state that would let a stale read
            //    report a false-clean on the next revision.
            let! before = bridge.Check({ bareCheck with projectPath = Some fx.ProbeFsproj })
            Assert.Equal("clean", gs before "verdict")
            Assert.True(gb before "analyzed", "the baseline check must be a genuine analysis, not a stale read")

            // 2. Break Helpers.fs so Main.fs (a DIFFERENT file) fails to type-check — the
            //    cross-file shape from #100. No rebuild; only the on-disk source changes.
            File.WriteAllText(fx.HelpersFs, brokenHelpers)

            // 3. The trusted re-check MUST reflect the new revision, not the cached clean.
            let! after = bridge.Check({ bareCheck with projectPath = Some fx.ProbeFsproj })

            Assert.Equal("errors", gs after "verdict")
            Assert.True(gi after "errorCount" > 0, "the cross-file error must be detected on re-check")
            Assert.True(gb after "analyzed", "the re-check is a fresh analysis")
            Assert.Equal(Some "fcs-reanalyze", (after["escalated"] |> Option.ofObj |> Option.map (fun n -> n.GetValue<string>())))
        }

    [<Fact>]
    member _.``speed=fast on a cold cache reports verdict=unknown, not a false-clean``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            // No FSAC snapshot is injected (the substrate is called directly), so the
            // cached snapshot is empty — the stale-`{}` ambiguity. fast must NOT call that
            // "clean"; it must honestly say "unknown" while the trusted default (other
            // tests) returns the real verdict.
            let! result =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some fx.ProbeFsproj
                        speed = Some "fast" }
                )

            Assert.Equal("fast", gs result "speed")
            Assert.Equal("unknown", gs result "verdict")
            Assert.False(gb result "analyzed", "a cold FSAC cache cannot be a confirmed analysis")
            Assert.Equal("fsac", gs result "via")
        }

    [<Fact>]
    member _.``speed=fast honors requested severity — a warning-only snapshot surfaces the warning when the floor allows it``
        ()
        : Task =
        task {
            let bridge = FcsBridge()

            // A ready, freshly-analyzed FSAC snapshot holding ONE warning and zero errors.
            // The fast path projects this through CheckFsacSnapshot, which now retains all
            // severities so the requested floor can surface them.
            let json =
                "{ \"lspState\": \"ready\", \"mostRecentAnalyzedAt\": \"2026-01-01T00:00:00Z\","
                + " \"diagnosticsFileCount\": 1, \"result\": { \"/probe/Warn.fs\": ["
                + " { \"severity\": 2, \"message\": \"unused value\", \"file\": \"/probe/Warn.fs\","
                + " \"range\": { \"startLine\": 1, \"startColumn\": 0, \"endLine\": 1, \"endColumn\": 5 } } ] } }"

            let snap = CheckFsacSnapshot.ofDiagnosticsResponse (JsonNode.Parse json)

            let fastCheck (severity: string option) =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some fx.ProbeFsproj
                        speed = Some "fast"
                        scope = Some "project"
                        severity = severity },
                    fsacSnapshot = (fun () -> Task.FromResult snap)
                )

            // DEFAULT floor = error: verdict stays clean (a warning is not an error) and the
            // warning is COUNTED, but it is below the floor so it is not in the list.
            let! atError = fastCheck None
            Assert.Equal("fast", gs atError "speed")
            Assert.Equal("clean", gs atError "verdict")
            Assert.Equal(0, gi atError "errorCount")
            Assert.Equal(1, gi atError "warningCount")
            Assert.Equal(0, (atError["diagnostics"] :?> JsonArray).Count)

            // severity=warning: the warning the caller asked for MUST now appear — this is
            // the regression the fix closes (the old fast path stored only errors).
            let! atWarning = fastCheck (Some "warning")
            Assert.Equal("clean", gs atWarning "verdict")
            Assert.Equal(1, gi atWarning "warningCount")
            let warnDiags = atWarning["diagnostics"] :?> JsonArray
            Assert.Equal(1, warnDiags.Count)
            let firstWarn = warnDiags[0]
            Assert.Equal(2, firstWarn["severity"].GetValue<int>())

            // severity=all surfaces it too.
            let! atAll = fastCheck (Some "all")
            Assert.Equal(1, (atAll["diagnostics"] :?> JsonArray).Count)
        }

    [<Fact>]
    member _.``invalid speed is rejected with invalid_args``() : Task =
        task {
            let bridge = FcsBridge()

            let! result =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some fx.ProbeFsproj
                        speed = Some "turbo" }
                )

            Assert.Equal("invalid_args", gs result "status")
            Assert.Contains("speed", gs result "message")
        }
