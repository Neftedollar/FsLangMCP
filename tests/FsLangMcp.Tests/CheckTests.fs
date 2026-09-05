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
// Fixture: one leaf project Probe where Helpers.fs is consumed by Main.fs, built ONCE,
// so cross-file edits exercise the stale-`{}` property. Each test sets the on-disk source
// it needs first (xUnit serialises methods within a class), so the FCS re-check is what
// makes the verdict reflect the current revision.

open System
open System.IO
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge
open FsLangMcp.LspBridge
open FsLangMcp.Dispatcher

// ── Fixture sources ────────────────────────────────────────────────────────────

let private cleanHelpers =
    String.concat "\n" [ "module Probe.Helpers"; ""; "let add (a: int) (b: int) : int = a + b"; "" ]

// Breaks the contract Main.fs depends on: `add` now returns string, so Main's
// `let result : int = add 1 2` becomes a CROSS-FILE type error.
let private brokenHelpers =
    String.concat "\n" [ "module Probe.Helpers"; ""; "let add (a: int) (b: int) : string = string (a + b)"; "" ]

let private cleanMain =
    String.concat "\n" [ "module Probe.Main"; ""; "open Probe.Helpers"; ""; "let result: int = add 1 2"; "" ]

let private standaloneSignature =
    String.concat "\n" [ "module Probe.Standalone"; ""; "val answer: int"; "" ]

let private standaloneImplementation =
    String.concat "\n" [ "module Probe.Standalone"; ""; "let answer = 42"; "" ]

// Self-contained single-file type error (string assigned to int).
let private errorMain =
    String.concat "\n" [ "module Probe.Main"; ""; "let x: int = \"oops\""; "" ]

let private probeProject =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          "  <ItemGroup>"
          "    <Compile Include=\"Standalone.fsi\" />"
          "    <Compile Include=\"Standalone.fs\" />"
          "    <Compile Include=\"Helpers.fs\" />"
          "    <Compile Include=\"Main.fs\" />"
          "  </ItemGroup>"
          "</Project>" ]

// #205: a second, independent leaf project alongside Probe so workspace-scope tests
// can exercise a genuine multi-project sweep. Beta carries a permanent, deliberate
// FS0025 incomplete-match warning (never reset by ResetClean) — a stable, real
// ground truth for the perProject infoCount reconciliation test.
let private betaWarningSource =
    String.concat
        "\n"
        [ "module Beta.Library"; ""; "let classify (x: int option) ="; "    match x with"; "    | Some v -> v"; "" ]

let private betaProject =
    String.concat
        "\n"
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          "  <ItemGroup>"
          "    <Compile Include=\"Library.fs\" />"
          "  </ItemGroup>"
          "</Project>" ]

// ── Class fixture: written + built ONCE, shared by every test in the class ───────

type CheckFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_check_{runId}")
    do TestRunTrace.fixture "fixture_initialize_start" "CheckFixture" root

    let write (rel: string) (content: string) =
        let full = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, content)
        full

    let probeFsproj = write "Probe/Probe.fsproj" probeProject
    let standaloneFsi = write "Probe/Standalone.fsi" standaloneSignature
    let _standaloneFs = write "Probe/Standalone.fs" standaloneImplementation
    let helpersFs = write "Probe/Helpers.fs" cleanHelpers
    let mainFs = write "Probe/Main.fs" cleanMain

    // #205: Beta lives beside Probe under the same root so workspace-scope tests get a
    // genuine second member project to sweep. Its warning is permanent — no ResetClean.
    let betaFsproj = write "Beta/Beta.fsproj" betaProject
    let _betaLibraryFs = write "Beta/Library.fs" betaWarningSource
    let probeSlnx =
        write
            "ProbeOnly.slnx"
            "<Solution>\n  <Project Path=\"Probe/Probe.fsproj\" />\n</Solution>\n"

    // dotnet build once per project so Ionide.ProjInfo can resolve options (restore +
    // design-time build). After this, FCS re-checks read source files from disk — no
    // rebuild needed for the cross-file stale test. Isolation/retry flags mirror
    // FindFixture to survive the parallel-collection MSBuild contention the find author
    // hit.
    let buildOnce (projectPath: string) =
        let psi =
            ProcessStartInfo(
                "dotnet",
                $"build \"{projectPath}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
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

    let rec buildWithRetry (projectPath: string) attempt =
        let code, log = buildOnce projectPath

        if code = 0 || attempt >= 3 then
            code, log
        else
            System.Threading.Thread.Sleep(1500)
            buildWithRetry projectPath (attempt + 1)

    let buildExit, buildLog = buildWithRetry probeFsproj 1
    let betaBuildExit, betaBuildLog = buildWithRetry betaFsproj 1
    do TestRunTrace.fixture "fixture_initialize_complete" "CheckFixture" root

    /// Restore the fixture sources to their clean baseline. Called at the top of every
    /// test so method ordering cannot leak a previous test's on-disk edit. Beta is
    /// intentionally excluded — its warning must stay in place for every test.
    member _.ResetClean() =
        File.WriteAllText(probeFsproj, probeProject)
        File.WriteAllText(helpersFs, cleanHelpers)
        File.WriteAllText(mainFs, cleanMain)

    member _.Root = root
    member _.ProbeFsproj = probeFsproj
    member _.ProbeSlnx = probeSlnx
    member _.StandaloneFsi = standaloneFsi
    member _.HelpersFs = helpersFs
    member _.MainFs = mainFs
    member _.BuildExitCode = buildExit
    member _.BuildLog = buildLog
    member _.BetaFsproj = betaFsproj
    member _.BetaBuildExitCode = betaBuildExit
    member _.BetaBuildLog = betaBuildLog

    interface IDisposable with
        member _.Dispose() = TestRunTrace.deleteOwnedDirectory "CheckFixture" root

// ── JSON helpers ─────────────────────────────────────────────────────────────────

let private gi (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private gb (node: JsonNode) (key: string) = node[key].GetValue<bool>()
let private gs (node: JsonNode) (key: string) = node[key].GetValue<string>()

let private bindCompleteSnapshot
    (expectation: CheckFsacExpectation)
    (snapshot: CheckFsacSnapshot)
    : CheckFsacSnapshot =
    { snapshot with
        Status = "ok"
        Ready = true
        ContextMatched = true
        Complete = true
        ExpectedFiles = Array.copy expectation.ExpectedFiles
        ReceivedFiles = Array.copy expectation.ExpectedFiles
        MissingFiles = [||]
        StaleFiles = [||]
        SessionGeneration = Some 7L
        FailureReason = None
        AnalyzedFileCount = expectation.ExpectedFiles.Length }

let private captureFastExpectation (bridge: FcsBridge) (args: CheckArgs) : Task<CheckFsacExpectation> =
    task {
        let mutable captured: CheckFsacExpectation option = None

        let capture (expectation: CheckFsacExpectation) =
            captured <- Some expectation
            Task.FromResult(CheckFsacSnapshot.unavailable expectation "test snapshot intentionally unavailable")

        let! _ = bridge.Check(args, fsacSnapshot = capture)

        return
            captured
            |> Option.defaultWith (fun () -> failwith "The fast-check expectation was not requested.")
    }

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

let private waitForDiscoveryState description predicate =
    task {
        let deadline = Stopwatch.StartNew()

        while not (predicate ()) && deadline.Elapsed < TimeSpan.FromSeconds(10.0) do
            do! Task.Delay(10)

        Assert.True(predicate (), description)
    }

let private settleDiscovery (completion: Task) =
    task {
        try
            do! completion.WaitAsync(TimeSpan.FromSeconds(10.0))
        with :? TimeoutException when completion.IsCompleted ->
            () // A zero-waiter worker terminates with the expected timeout.
    }

[<Fact>]
let ``check discovery keys length-frame POSIX path components`` () =
    if not (OperatingSystem.IsWindows()) then
        let root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fslangmcp-key-frame"))
        let targetA = Path.Combine(root, "a")
        let pathA = Path.Combine(root, "b") + "|" + Path.Combine(root, "c")
        let targetB = Path.Combine(root, "a") + "|" + Path.Combine(root, "b")
        let pathB = Path.Combine(root, "c")

        // This demonstrates the old delimiter collision independently of the
        // implementation, then verifies that the production key preserves the
        // three component boundaries.
        Assert.Equal($"project|{targetA}|{pathA}", $"project|{targetB}|{pathB}")
        let bridge = FcsBridge()
        let keyA = bridge.CheckDiscoveryKeyForTest("project", targetA, Some pathA)
        let keyB = bridge.CheckDiscoveryKeyForTest("project", targetB, Some pathB)

        Assert.False(String.Equals(keyA, keyB, StringComparison.Ordinal))

[<Fact>]
let ``project check rejects negative timeout and treats zero as immediate unknown`` () : Task =
    task {
        let projectPath = Path.Combine(Path.GetTempPath(), $"fslangmcp_timeout_args_{Guid.NewGuid():N}.fsproj")

        let mustNotStart (_: string) : Task =
            Task.FromException(InvalidOperationException("project evaluation must not start"))

        let bridge = FcsBridge(projectEvaluationBeforeLoadOverride = mustNotStart)

        let! negative =
            bridge.Check(
                { bareCheck with
                    scope = Some "project"
                    speed = Some "trusted"
                    projectPath = Some projectPath
                    timeoutMs = Some -1 }
            )

        Assert.Equal("invalid_args", gs negative "status")
        Assert.Contains("non-negative", gs negative "message")

        let! zero =
            bridge.Check(
                { bareCheck with
                    scope = Some "project"
                    speed = Some "trusted"
                    projectPath = Some projectPath
                    timeoutMs = Some 0 }
            )

        Assert.Equal("succeeded", gs zero "status")
        Assert.Equal("unknown", gs zero "verdict")
        Assert.False(gb zero "analyzed")
        Assert.Equal("timeout", gs (zero["blockingReason"]) "errorKind")
        Assert.Equal(0L, bridge.ProjectEvaluationStartedCount)
    }

[<Fact>]
let ``generic project evaluation failure stays distinct from sdk_not_found`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_generic_block_{Guid.NewGuid():N}")
        Directory.CreateDirectory(root) |> ignore
        let projectPath = Path.Combine(root, "Broken.fsproj")
        let sourcePath = Path.Combine(root, "Library.fs")

        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Compile Include=\"Library.fs\" /></ItemGroup></Project>"
        )

        File.WriteAllText(sourcePath, "module Broken\n\nlet value = 1\n")

        let failEvaluation (_: string) =
            Task.FromException(InvalidOperationException("simulated project evaluation failure"))

        let bridge = FcsBridge(projectEvaluationBeforeLoadOverride = failEvaluation)

        try
            for speed in [ "trusted"; "fast" ] do
                let! result =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some speed
                            projectPath = Some projectPath
                            timeoutMs = Some 5_000 }
                    )

                Assert.Equal("unknown", gs result "verdict")
                Assert.Equal("project_failure", gs (result["blockingReason"]) "errorKind")
        finally
            Directory.Delete(root, true)
    }

let private runAutoScopeDiscoveryDeadlineTest () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_scope_deadline_{Guid.NewGuid():N}")
        let discoveryStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseDiscovery = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        Directory.CreateDirectory(root) |> ignore

        let blockDiscovery () =
            discoveryStarted.TrySetResult(()) |> ignore
            releaseDiscovery.Task.GetAwaiter().GetResult()

        let bridge =
            FcsBridge(checkTargetDiscoveryBeforeComputeOverride = blockDiscovery)

        try
            let checkTask =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some root
                        speed = Some "trusted"
                        timeoutMs = Some 300 }
                )

            // Coverlet instrumentation can add substantial scheduler overhead on CI.
            // These outer waits are deadlock guards; the product deadlines asserted
            // below remain 300/150/1000 ms and are intentionally unchanged.
            do! discoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5.0))
            let! result = checkTask.WaitAsync(TimeSpan.FromSeconds(5.0))

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal("unknown", gs result "verdict")
            Assert.False(gb result "analyzed")
            Assert.Contains("timed out", (gs result "reason").ToLowerInvariant())
            Assert.False(
                releaseDiscovery.Task.IsCompleted,
                "The caller must return while pure target discovery is still blocked."
            )
            Assert.Equal(0L, bridge.ProjectEvaluationStartedCount)
            Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)
            Assert.Equal(1L, bridge.CheckTargetDiscoveryStartedCount)
            Assert.Equal(1, bridge.CheckTargetDiscoveryActiveCount)
            Assert.Equal(1, bridge.CheckTargetDiscoveryInFlightCount)

            let discoveryCompletion =
                bridge.TryGetCheckTargetDiscoveryCompletionForTest("auto-scope", root, None)
                |> Option.defaultWith (fun () -> failwith "The auto-scope discovery worker was not observable.")

            Assert.False(
                discoveryCompletion.IsCompleted,
                "The caller deadline must not masquerade as discovery-worker completion."
            )

            // An exact-key retry must share the still-running scan rather than start
            // another abandoned directory walk.
            let! sameTarget =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some root
                        speed = Some "trusted"
                        timeoutMs = Some 150 }
                )
                |> fun work -> work.WaitAsync(TimeSpan.FromSeconds(5.0))

            Assert.Equal("unknown", gs sameTarget "verdict")
            Assert.Equal(1L, bridge.CheckTargetDiscoveryStartedCount)
            Assert.Equal(1, bridge.CheckTargetDiscoveryInFlightCount)

            // Distinct keys have no wait queue. Each one is rejected promptly while
            // the first real scan owns the sole admission slot, and its dictionary
            // entry is removed immediately.
            for index in 1..6 do
                let otherRoot = Path.Combine(Path.GetTempPath(), $"fslangmcp_scope_busy_{index}_{Guid.NewGuid():N}")

                let! busy =
                    bridge.Check(
                        { bareCheck with
                            projectPath = Some otherRoot
                            speed = Some "trusted"
                            timeoutMs = Some 1000 }
                    )
                    |> fun work -> work.WaitAsync(TimeSpan.FromSeconds(5.0))

                Assert.Equal("unknown", gs busy "verdict")
                Assert.Contains("discovery busy", (gs busy "reason").ToLowerInvariant())

            Assert.Equal(1L, bridge.CheckTargetDiscoveryStartedCount)
            Assert.Equal(6L, bridge.CheckTargetDiscoveryRejectedCount)
            Assert.Equal(1, bridge.CheckTargetDiscoveryInFlightCount)

            releaseDiscovery.TrySetResult(()) |> ignore

            try
                do! discoveryCompletion.WaitAsync(TimeSpan.FromSeconds(5.0))
            with :? TimeoutException when discoveryCompletion.IsCompleted ->
                ()

            Assert.Equal(0, bridge.CheckTargetDiscoveryActiveCount)
            Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount)

            // Completion releases admission: a new explicit workspace scan starts
            // normally (the empty directory then fails validation, as expected).
            let! retry =
                bridge.Check(
                    { bareCheck with
                        scope = Some "workspace"
                        projectPath = Some root
                        speed = Some "trusted"
                        // This call proves admission was released, not another deadline.
                        // Leave enough headroom for a CPU-saturated full-suite run.
                        timeoutMs = Some 5000 }
                )

            Assert.Equal("invalid_args", gs retry "status")
            Assert.Equal(2L, bridge.CheckTargetDiscoveryStartedCount)
        finally
            releaseDiscovery.TrySetResult(()) |> ignore

            if Directory.Exists root then
                Directory.Delete(root, true)
    }

// Release blocked producers and drain public requests plus exact retained workers
// even when an assertion fails. No cleanup timeout: fixture files must outlive them.
type private BlockedCheckRequests(release: unit -> unit, reset: unit -> unit) =
    let requests = ResizeArray<Task>()
    let workers = ResizeArray<Task>()
    let mutable verifyDrained = ignore

    member _.Track(request: Task<'T>) =
        requests.Add(request :> Task)
        request

    member _.TrackWorker(completion: Task) =
        workers.Add(completion)
        completion

    member _.TrackWorkerExpectingTimeout(completion: Task) =
        workers.Add(
            task {
                try
                    do! completion
                with :? TimeoutException ->
                    ()
            }
            :> Task
        )

        completion

    member _.VerifyBeforeReset(verify: unit -> unit) =
        verifyDrained <- verify

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                release ()

                let pending =
                    Array.append (requests.ToArray()) (workers.ToArray())

                try
                    do! Task.WhenAll(pending)
                finally
                    verifyDrained ()
                    reset ()
            })

[<Fact>]
let ``fast project check returns unknown when expectation evaluation exhausts the overall timeout`` () : Task =
    task {
        let projectPath = Path.Combine(Path.GetTempPath(), $"fslangmcp_fast_timeout_{Guid.NewGuid():N}.fsproj")
        let loadStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseLoad = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable snapshotCalled = false

        let blockedLoad (_: string) : Task =
            (task {
                loadStarted.TrySetResult(()) |> ignore
                do! releaseLoad.Task
                return raise (InvalidOperationException("controlled fast evaluation completion"))
             }
             :> Task)

        let bridge = FcsBridge(projectEvaluationBeforeLoadOverride = blockedLoad)

        use requests = new BlockedCheckRequests((fun () -> releaseLoad.TrySetResult(()) |> ignore), ignore)

        // Admit the exact options producer without spending the Check caller's
        // 300ms budget on scheduler admission. This request also owns its cleanup.
        let seed = bridge.GetEvaluatedProjectSnapshot(projectPath) |> requests.Track
        do! loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
        Assert.Equal(1, bridge.ProjectEvaluationActiveCount)
        Assert.Equal(1, bridge.ProjectOptionsInFlightCount)

        let checkTask =
            bridge.Check(
                { bareCheck with
                    scope = Some "project"
                    speed = Some "fast"
                    projectPath = Some projectPath
                    timeoutMs = Some 300 },
                fsacSnapshot = (fun expectation ->
                    snapshotCalled <- true
                    Task.FromResult(CheckFsacSnapshot.unavailable expectation "must not be reached"))
            )
            |> requests.Track

        let! result = checkTask.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))

        Assert.Equal("succeeded", gs result "status")
        Assert.Equal("unknown", gs result "verdict")
        Assert.False(gb result "analyzed")
        Assert.False(gb result "expectationComplete")
        Assert.Contains("timed out", (gs result "reason").ToLowerInvariant())
        Assert.False(snapshotCalled, "No FSAC snapshot work may start after expectation exhausted the budget.")
        Assert.Equal(1, bridge.ProjectEvaluationActiveCount)
        Assert.Equal(1L, bridge.ProjectEvaluationStartedCount)
        Assert.Equal(0L, bridge.ProjectEvaluationRejectedCount)
        Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)

        releaseLoad.TrySetResult(()) |> ignore
        let! seededResult = seed.WaitAsync(TimeSpan.FromSeconds(10.0))
        Assert.Equal(Error "controlled fast evaluation completion", seededResult)
        Assert.Equal(0, bridge.ProjectEvaluationActiveCount)
        Assert.Equal(0, bridge.ProjectOptionsInFlightCount)
        Assert.False(snapshotCalled, "Draining the producer must not start an expired caller's snapshot.")
    }

[<Fact>]
let ``fast incomplete timeout expectation cannot turn a cached error into a conclusive verdict`` () : Task =
    task {
        let projectPath = Path.Combine(Path.GetTempPath(), $"fslangmcp_fast_incomplete_{Guid.NewGuid():N}.fsproj")
        let mutable snapshotCalled = false

        let timedOutLoad (_: string) : Task =
            Task.FromException(TimeoutException("simulated project evaluation timeout"))

        let bridge = FcsBridge(projectEvaluationBeforeLoadOverride = timedOutLoad)

        let maliciousSnapshot (expectation: CheckFsacExpectation) =
            snapshotCalled <- true

            let diagnostics =
                JsonArray(
                    jobj
                        [ "severity", jint 1
                          "message", jstr "cached error from an unbound expectation"
                          "file", jstr "/stale.fs"
                          "range", JsonObject() :> JsonNode ]
                    :> JsonNode
                )
                :> JsonNode

            { CheckFsacSnapshot.empty with
                Status = "ok"
                Ready = true
                ContextMatched = true
                Complete = true
                ExpectedFiles = Array.copy expectation.ExpectedFiles
                ReceivedFiles = Array.copy expectation.ExpectedFiles
                MissingFiles = [||]
                StaleFiles = [||]
                SessionGeneration = Some 17L
                FailureReason = None
                AnalyzedFileCount = expectation.ExpectedFiles.Length
                ErrorCount = 1
                MostRecentAnalyzedAt = Some "2026-01-01T00:00:00Z"
                Diagnostics = diagnostics }
            |> Task.FromResult

        let! result =
            bridge.Check(
                { bareCheck with
                    scope = Some "project"
                    speed = Some "fast"
                    projectPath = Some projectPath
                    timeoutMs = Some 1000 },
                fsacSnapshot = maliciousSnapshot
            )

        Assert.False(snapshotCalled, "A typed expectation blocker must skip the cached snapshot entirely.")
        Assert.Equal("unknown", gs result "verdict")
        Assert.False(gb result "analyzed")
        Assert.False(gb result "expectationComplete")
        Assert.Equal(0, gi result "errorCount")
        Assert.Contains("timed out", (gs result "reason").ToLowerInvariant())
        Assert.Equal("timeout", gs (result["blockingReason"]) "errorKind")
    }

[<Fact>]
let ``trusted workspace overall timeout skips later project loaders`` () : Task =
    task {
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_trusted_deadline_{Guid.NewGuid():N}")
        let projectA = Path.Combine(root, "A.fsproj")
        let projectB = Path.Combine(root, "B.fsproj")
        let aStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let bStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseA = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(projectA, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
        File.WriteAllText(projectB, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")

        let controlledLoad (path: string) : Task =
            (task {
                if String.Equals(Path.GetFullPath(path), Path.GetFullPath(projectA), StringComparison.Ordinal) then
                    aStarted.TrySetResult(()) |> ignore
                    do! releaseA.Task
                    return raise (InvalidOperationException("controlled A completion"))
                else
                    bStarted.TrySetResult(()) |> ignore
                    return raise (InvalidOperationException("B must not start after deadline"))
             }
             :> Task)

        let bridge = FcsBridge(projectEvaluationBeforeLoadOverride = controlledLoad)

        try
            let checkTask =
                bridge.Check(
                    { bareCheck with
                        scope = Some "workspace"
                        speed = Some "trusted"
                        projectPath = Some root
                        timeoutMs = Some 300 }
                )

            do! aStarted.Task.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(1.0)))
            let! result = checkTask.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))

            Assert.Equal("unknown", gs result "verdict")
            Assert.False(gb result "analyzed")
            Assert.Equal(2, gi result "timedOutCount")
            Assert.False(bStarted.Task.IsCompleted, "The next workspace project must be skipped after deadline expiry.")
            Assert.Equal(1L, bridge.ProjectEvaluationStartedCount)
            Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)
            Assert.Contains("overall", (gs result "reason").ToLowerInvariant())
        finally
            releaseA.TrySetResult(()) |> ignore

            if Directory.Exists root then
                Directory.Delete(root, true)
    }

// ─────────────────────────────────────────────────────────────────────────────────

[<CollectionDefinition("FsLangMcp check worker isolation", DisableParallelization = true)>]
type CheckWorkerIsolationCollection() = class end

[<Collection("FsLangMcp check worker isolation")>]
type CheckAutoScopeDiscoveryTests() =
    [<Fact>]
    member _.``auto scope discovery obeys the overall timeout before options start``() : Task =
        runAutoScopeDiscoveryDeadlineTest ()

// Several tests deliberately hold and release the process-wide FCS admission
// gates. Running unrelated FCS fixtures beside them turns scheduler pressure
// into false 5-12s test timeouts and can leave a failed run waiting on the
// deliberately retained worker. Keep this class serial with the rest of the
// assembly; methods within the class were already serialized by xUnit.
[<Collection("FsLangMcp check worker isolation")>]
type CheckTests(fx: CheckFixture) =
    interface IClassFixture<CheckFixture>

    [<Fact>]
    member _.``trusted project timeout during snapshot hashing cannot commit or start a late FCS check``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let hashStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseHash = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let blockSnapshotHash () =
                hashStarted.TrySetResult(()) |> ignore
                releaseHash.Task.GetAwaiter().GetResult()

            let bridge =
                FcsBridge(analysisSnapshotKeyBeforeComputeOverride = blockSnapshotHash)

            // Warm options first so the regression exercises the hot-cache sync-start
            // path that previously ran hashing before the caller could attach WaitAsync.
            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")
            let computeBefore = bridge.AnalysisSnapshotComputeCount
            let commitBefore = bridge.AnalysisSnapshotCommitCount

            try
                let checkTask =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 1000 }
                    )

                do! hashStarted.Task.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(1.0)))
                let! result = checkTask.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))

                Assert.Equal("unknown", gs result "verdict")
                Assert.False(gb result "analyzed")
                Assert.Contains("timed out", (gs result "reason").ToLowerInvariant())
                Assert.False(releaseHash.Task.IsCompleted, "The caller must return before the blocked pure hash is released.")
                Assert.Equal(commitBefore, bridge.AnalysisSnapshotCommitCount)
                Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)
                Assert.Equal(1L, bridge.SnapshotComputationStartedCount)
                Assert.Equal(1, bridge.SnapshotComputationActiveCount)
                Assert.Equal(1, bridge.SnapshotComputationInFlightCount)

                let! sameSnapshot =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 150 }
                    )
                    |> fun work -> work.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(1.0)))

                Assert.Equal("unknown", gs sameSnapshot "verdict")
                Assert.Equal(1L, bridge.SnapshotComputationStartedCount)
                Assert.Equal(0L, bridge.SnapshotComputationRejectedCount)
                Assert.Equal(1, bridge.SnapshotComputationInFlightCount)
            finally
                releaseHash.TrySetResult(()) |> ignore

            // Release the pre-compute hook after the caller deadline. The admitted
            // worker must re-check that deadline and settle without performing the
            // expensive hash, committing identity, or launching FCS work.
            let resumed = Stopwatch.StartNew()

            let snapshotStillRunning () =
                bridge.SnapshotComputationActiveCount <> 0
                || bridge.SnapshotComputationInFlightCount <> 0

            while snapshotStillRunning () && resumed.Elapsed < TestTiming.watchdog (TimeSpan.FromSeconds(2.0)) do
                do! Task.Delay(10)

            Assert.Equal(computeBefore, bridge.AnalysisSnapshotComputeCount)
            Assert.Equal(commitBefore, bridge.AnalysisSnapshotCommitCount)
            Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)
            Assert.Equal(0, bridge.SnapshotComputationActiveCount)
            Assert.Equal(0, bridge.SnapshotComputationInFlightCount)
        }

    [<Fact>]
    member _.``trusted deadline expiring before worker admission cannot commit or start FCS late``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let admissionReached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseAdmission = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let blockBeforeAdmission () : Task =
                (task {
                    admissionReached.TrySetResult(()) |> ignore
                    do! releaseAdmission.Task
                 }
                 :> Task)

            let mustNotTypeCheck _ =
                Task.FromException<FSharp.Compiler.Diagnostics.FSharpDiagnostic array>(
                    InvalidOperationException("late type-check must not start")
                )

            let bridge =
                FcsBridge(
                    freshProjectCheckBeforeAdmissionOverride = blockBeforeAdmission,
                    freshProjectCheckWorkerOverride = mustNotTypeCheck,
                    freshProjectCheckConcurrencyOverride = 1
                )

            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")
            let commitBefore = bridge.AnalysisSnapshotCommitCount
            let invalidationsBefore = bridge.FreshProjectCheckInvalidationCount

            try
                let checkTask =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 1000 }
                    )

                do! admissionReached.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
                let! result = checkTask.WaitAsync(TimeSpan.FromSeconds(10.0))

                Assert.Equal("unknown", gs result "verdict")
                Assert.False(gb result "analyzed")
                Assert.False(releaseAdmission.Task.IsCompleted)
                Assert.Equal(commitBefore, bridge.AnalysisSnapshotCommitCount)
                Assert.Equal(invalidationsBefore, bridge.FreshProjectCheckInvalidationCount)
                Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)
            finally
                releaseAdmission.TrySetResult(()) |> ignore

            let settle = Stopwatch.StartNew()

            while bridge.FreshProjectCheckInFlightCount <> 0
                  && settle.Elapsed < TimeSpan.FromSeconds(10.0) do
                do! Task.Delay(10)

            Assert.Equal(0, bridge.FreshProjectCheckInFlightCount)
            Assert.Equal(commitBefore, bridge.AnalysisSnapshotCommitCount)
            Assert.Equal(invalidationsBefore, bridge.FreshProjectCheckInvalidationCount)
            Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)
        }

    [<Fact>]
    member _.``trusted deadline expiring after invalidation cannot start FCS late``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let preFcsReached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releasePreFcs = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let blockAfterInvalidation () : Task =
                (task {
                    preFcsReached.TrySetResult(()) |> ignore
                    do! releasePreFcs.Task
                 }
                 :> Task)

            let mustNotTypeCheck _ =
                Task.FromException<FSharp.Compiler.Diagnostics.FSharpDiagnostic array>(
                    InvalidOperationException("post-deadline type-check must not start")
                )

            let bridge =
                FcsBridge(
                    freshProjectCheckBeforeFcsStartOverride = blockAfterInvalidation,
                    freshProjectCheckWorkerOverride = mustNotTypeCheck,
                    freshProjectCheckConcurrencyOverride = 1
                )

            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")
            let commitBefore = bridge.AnalysisSnapshotCommitCount
            let invalidationsBefore = bridge.FreshProjectCheckInvalidationCount

            try
                let checkTask =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 1000 }
                    )

                do! preFcsReached.Task.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))
                let! result = checkTask.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))

                Assert.Equal("unknown", gs result "verdict")
                Assert.False(gb result "analyzed")
                Assert.False(releasePreFcs.Task.IsCompleted)
                Assert.True(bridge.AnalysisSnapshotCommitCount > commitBefore)
                Assert.True(bridge.FreshProjectCheckInvalidationCount > invalidationsBefore)
                Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)
            finally
                releasePreFcs.TrySetResult(()) |> ignore

            let settle = Stopwatch.StartNew()

            while bridge.FreshProjectCheckInFlightCount <> 0
                  && settle.Elapsed < TestTiming.watchdog (TimeSpan.FromSeconds(2.0)) do
                do! Task.Delay(10)

            Assert.Equal(0, bridge.FreshProjectCheckInFlightCount)
            Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)
        }

    [<Fact>]
    member _.``trusted project timeouts share the real worker and reject distinct snapshots without queueing``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let firstStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseFirst = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let secondStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseSecond = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable workerCalls = 0

            let controlledWorker _ =
                task {
                    let invocation = System.Threading.Interlocked.Increment(&workerCalls)

                    if invocation = 1 then
                        firstStarted.TrySetResult(()) |> ignore
                        do! releaseFirst.Task
                    else
                        secondStarted.TrySetResult(()) |> ignore
                        do! releaseSecond.Task

                    return [||]
                }

            let bridge =
                FcsBridge(
                    freshProjectCheckWorkerOverride = controlledWorker,
                    freshProjectCheckConcurrencyOverride = 1
                )

            // Keep the regression focused on the uncancellable FCS worker rather
            // than project evaluation latency.
            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")

            use requests =
                new BlockedCheckRequests(
                    (fun () ->
                        releaseFirst.TrySetResult(()) |> ignore
                        releaseSecond.TrySetResult(()) |> ignore),
                    fx.ResetClean
                )

            // Admission belongs to a long-lived seed, not to the measured 300ms
            // caller. Keep it alive until release so awaiting it drains worker A.
            let seed =
                bridge.Check(
                    { bareCheck with
                        scope = Some "project"
                        speed = Some "trusted"
                        projectPath = Some fx.ProbeFsproj
                        timeoutMs = Some Int32.MaxValue }
                )
                |> requests.Track

            do! firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10.0))

            let firstCheck =
                bridge.Check(
                    { bareCheck with
                        scope = Some "project"
                        speed = Some "trusted"
                        projectPath = Some fx.ProbeFsproj
                        timeoutMs = Some 300 }
                )
                |> requests.Track

            let! firstResult = firstCheck.WaitAsync(TimeSpan.FromSeconds(10.0))

            Assert.Equal("unknown", gs firstResult "verdict")
            Assert.False(gb firstResult "analyzed")
            Assert.Equal(1, workerCalls)
            Assert.Equal(1L, bridge.FreshProjectCheckStartedCount)
            Assert.Equal(1L, bridge.ProjectTypeCheckStartCount)
            Assert.Equal(1, bridge.FreshProjectCheckActiveCount)
            Assert.Equal(1, bridge.FreshProjectCheckInFlightCount)

            // The same snapshot must neither reject nor start a second worker. This
            // short caller may expire upstream; these counters alone do not prove
            // that it reached the join before its deadline.
            let! sameSnapshot =
                bridge.Check(
                    { bareCheck with
                        scope = Some "project"
                        speed = Some "trusted"
                        projectPath = Some fx.ProbeFsproj
                        timeoutMs = Some 150 }
                )
                |> requests.Track
                |> fun work -> work.WaitAsync(TimeSpan.FromSeconds(10.0))

            Assert.Equal("unknown", gs sameSnapshot "verdict")
            Assert.Equal(1, workerCalls)
            Assert.Equal(1L, bridge.FreshProjectCheckStartedCount)
            Assert.Equal(0L, bridge.FreshProjectCheckRejectedCount)
            Assert.Equal(1, bridge.FreshProjectCheckInFlightCount)

            // Each content revision is a distinct type-check key. While A owns
            // admission all are rejected promptly, without a cross-key waiter or
            // retained dictionary entry.
            for index in 1..6 do
                File.WriteAllText(fx.MainFs, cleanMain + $"\n// distinct snapshot {index}\n")

                let! busy =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 1000 }
                    )
                    |> requests.Track
                    |> fun work -> work.WaitAsync(TimeSpan.FromSeconds(10.0))

                Assert.Equal("unknown", gs busy "verdict")
                Assert.False(gb busy "analyzed")
                Assert.Contains("type-check busy", (gs busy "reason").ToLowerInvariant())
                Assert.Equal("fcs_worker_busy", gs (busy["blockingReason"]) "errorKind")

            Assert.Equal(1, workerCalls)
            Assert.Equal(1L, bridge.FreshProjectCheckStartedCount)
            Assert.Equal(6L, bridge.FreshProjectCheckRejectedCount)
            Assert.Equal(1, bridge.FreshProjectCheckInFlightCount)
            Assert.Equal(1, bridge.FreshProjectCheckMaxObservedConcurrency)

            // A completed against an obsolete snapshot. Its late empty result is
            // discarded, admission is released, and the current snapshot can be
            // retried as a new worker B.
            releaseFirst.TrySetResult(()) |> ignore
            let! staleResult = seed.WaitAsync(TimeSpan.FromSeconds(10.0))

            Assert.Equal("unknown", gs staleResult "verdict")
            Assert.False(gb staleResult "analyzed")
            Assert.Equal(0, bridge.FreshProjectCheckActiveCount)
            Assert.Equal(0, bridge.FreshProjectCheckInFlightCount)

            let retry =
                bridge.Check(
                    { bareCheck with
                        scope = Some "project"
                        speed = Some "trusted"
                        projectPath = Some fx.ProbeFsproj
                        timeoutMs = Some 5000 }
                )
                |> requests.Track

            do! secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
            Assert.Equal(2, workerCalls)
            Assert.Equal(2L, bridge.FreshProjectCheckStartedCount)
            Assert.Equal(2L, bridge.ProjectTypeCheckStartCount)
            releaseSecond.TrySetResult(()) |> ignore
            let! retryResult = retry.WaitAsync(TimeSpan.FromSeconds(10.0))

            Assert.Equal("clean", gs retryResult "verdict")
            Assert.True(gb retryResult "analyzed")
            Assert.Equal(0, gi retryResult "errorCount")
            Assert.Equal(0, bridge.FreshProjectCheckActiveCount)
            Assert.Equal(0, bridge.FreshProjectCheckInFlightCount)
        }

    [<Fact>]
    member _.``reference probe timeout shares exact options and rejects distinct work without queueing``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let probeStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseProbe = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable probeCalls = 0

            let blockingProbe (otherOptions: string array) =
                System.Threading.Interlocked.Increment(&probeCalls) |> ignore
                probeStarted.TrySetResult(()) |> ignore
                releaseProbe.Task.GetAwaiter().GetResult()
                otherOptions.Length, otherOptions.Length

            let bridge =
                FcsBridge(
                    referenceResolutionProbeOverride = blockingProbe,
                    freshProjectCheckWorkerOverride = (fun _ -> Task.FromResult([||])),
                    freshProjectCheckConcurrencyOverride = 1
                )

            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")

            try
                let firstCheck =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 300 }
                    )

                do! probeStarted.Task.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))
                let! firstResult = firstCheck.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))

                Assert.Equal("unknown", gs firstResult "verdict")
                Assert.False(gb firstResult "analyzed")
                Assert.Equal(1, probeCalls)
                Assert.Equal(1L, bridge.ReferenceResolutionProbeStartedCount)
                Assert.Equal(1, bridge.ReferenceResolutionProbeActiveCount)
                Assert.Equal(1, bridge.ReferenceResolutionProbeInFlightCount)
                Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)

                let! sameOptions =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 150 }
                    )
                    |> fun work -> work.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(1.0)))

                Assert.Equal("unknown", gs sameOptions "verdict")
                Assert.Equal(1, probeCalls)
                Assert.Equal(1L, bridge.ReferenceResolutionProbeStartedCount)
                Assert.Equal(1, bridge.ReferenceResolutionProbeInFlightCount)

                for index in 1..6 do
                    let! rejected =
                        bridge.ProbeReferencesForTest(
                            $"distinct-reference-probe-{index}",
                            [| $"-r:/definitely/missing/{index}.dll" |]
                        )

                    match rejected with
                    | Error(CheckBusy reason) -> Assert.Contains("probe busy", reason.ToLowerInvariant())
                    | Error failure -> failwith $"Expected busy reference probe, got {failure}"
                    | Ok value -> failwith $"Expected busy reference probe, got {value}"

                Assert.Equal(1, probeCalls)
                Assert.Equal(6L, bridge.ReferenceResolutionProbeRejectedCount)
                Assert.Equal(1, bridge.ReferenceResolutionProbeInFlightCount)
                releaseProbe.TrySetResult(()) |> ignore

                let settle = Stopwatch.StartNew()
                let referenceProbeStillRunning () =
                    bridge.ReferenceResolutionProbeActiveCount <> 0
                    || bridge.ReferenceResolutionProbeInFlightCount <> 0

                while referenceProbeStillRunning ()
                      && settle.Elapsed < TestTiming.watchdog (TimeSpan.FromSeconds(2.0)) do
                    do! Task.Delay(10)

                Assert.Equal(0, bridge.ReferenceResolutionProbeActiveCount)
                Assert.Equal(0, bridge.ReferenceResolutionProbeInFlightCount)

                let! retry =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 5000 }
                    )

                Assert.Equal("clean", gs retry "verdict")
                Assert.True(gb retry "analyzed")
                Assert.Equal(2, probeCalls)
                Assert.Equal(2L, bridge.ReferenceResolutionProbeStartedCount)
                Assert.Equal(1L, bridge.ProjectTypeCheckStartCount)
            finally
                releaseProbe.TrySetResult(()) |> ignore
                fx.ResetClean()
        }

    [<Fact>]
    member _.``cached project validation is single flight and skips hashing after caller deadline``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let validationStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseValidation = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable validationHooks = 0

            let blockValidation (_: string) =
                System.Threading.Interlocked.Increment(&validationHooks) |> ignore
                validationStarted.TrySetResult(()) |> ignore
                releaseValidation.Task.GetAwaiter().GetResult()

            let bridge =
                FcsBridge(
                    projectOptionsCacheValidationBeforeComputeOverride = blockValidation,
                    freshProjectCheckWorkerOverride = (fun _ -> Task.FromResult([||])),
                    freshProjectCheckConcurrencyOverride = 1
                )

            // The first miss loads normally; the hook applies only to validation of
            // this now-cached exact fsproj entry.
            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")
            Assert.Equal(1L, bridge.ProjectOptionsLoadCount)
            Assert.Equal(0L, bridge.ProjectOptionsCacheValidationCount)

            try
                let firstCheck =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 300 }
                    )

                do! validationStarted.Task.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))
                let! firstResult = firstCheck.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))

                Assert.Equal("unknown", gs firstResult "verdict")
                Assert.Equal(1, validationHooks)
                Assert.Equal(0L, bridge.ProjectOptionsCacheValidationCount)
                Assert.Equal(1, bridge.ProjectEvaluationActiveCount)
                Assert.Equal(1, bridge.ProjectOptionsInFlightCount)

                let! sameProject =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 150 }
                    )
                    |> fun work -> work.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(1.0)))

                Assert.Equal("unknown", gs sameProject "verdict")
                Assert.Equal(1, validationHooks)
                Assert.Equal(2L, bridge.ProjectEvaluationStartedCount)
                Assert.Equal(1, bridge.ProjectOptionsInFlightCount)
                releaseValidation.TrySetResult(()) |> ignore

                let settle = Stopwatch.StartNew()
                let validationStillRunning () =
                    bridge.ProjectEvaluationActiveCount <> 0
                    || bridge.ProjectOptionsInFlightCount <> 0

                while validationStillRunning ()
                      && settle.Elapsed < TestTiming.watchdog (TimeSpan.FromSeconds(2.0)) do
                    do! Task.Delay(10)

                Assert.Equal(0, bridge.ProjectEvaluationActiveCount)
                Assert.Equal(0, bridge.ProjectOptionsInFlightCount)
                Assert.Equal(0L, bridge.ProjectOptionsCacheValidationCount)
                Assert.Equal(1L, bridge.ProjectOptionsLoadCount)
                Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)

                let! retry =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj
                            timeoutMs = Some 5000 }
                    )

                Assert.Equal("clean", gs retry "verdict")
                Assert.True(gb retry "analyzed")
                Assert.True(bridge.ProjectOptionsCacheValidationCount >= 2L)
                Assert.Equal(1L, bridge.ProjectOptionsLoadCount)
                Assert.Equal(1L, bridge.ProjectTypeCheckStartCount)
            finally
                releaseValidation.TrySetResult(()) |> ignore
                fx.ResetClean()
        }

    [<Fact>]
    member _.``nearest project discovery is bounded before synchronous directory enumeration``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let nearestStarted = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseNearest = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable discoveryHooks = 0

            let blockDiscovery () =
                System.Threading.Interlocked.Increment(&discoveryHooks) |> ignore
                nearestStarted.TrySetResult(()) |> ignore
                releaseNearest.Task.GetAwaiter().GetResult()

            let bridge =
                FcsBridge(
                    checkTargetDiscoveryBeforeComputeOverride = blockDiscovery,
                    freshProjectCheckWorkerOverride = (fun _ -> Task.FromResult([||])),
                    freshProjectCheckConcurrencyOverride = 1
                )

            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")
            let evaluationsBefore = bridge.ProjectEvaluationStartedCount

            try
                let firstCheck =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            path = Some fx.MainFs
                            speed = Some "trusted"
                            timeoutMs = Some 300 }
                    )

                do! nearestStarted.Task.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))
                let! firstResult = firstCheck.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))

                Assert.Equal("unknown", gs firstResult "verdict")
                Assert.Equal(1, discoveryHooks)
                Assert.Equal(1L, bridge.CheckTargetDiscoveryStartedCount)
                Assert.Equal(1, bridge.CheckTargetDiscoveryInFlightCount)
                Assert.Equal(evaluationsBefore, bridge.ProjectEvaluationStartedCount)
                Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)

                let discoveryCompletion =
                    bridge.TryGetCheckTargetDiscoveryCompletionForTest("nearest-project", fx.MainFs, None)
                    |> Option.defaultWith (fun () -> failwith "The nearest-project discovery worker was not observable.")

                Assert.False(
                    discoveryCompletion.IsCompleted,
                    "The caller deadline must not masquerade as discovery-worker completion."
                )

                let! samePath =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            path = Some fx.MainFs
                            speed = Some "trusted"
                            timeoutMs = Some 150 }
                    )
                    |> fun work -> work.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(1.0)))

                Assert.Equal("unknown", gs samePath "verdict")
                Assert.Equal(1, discoveryHooks)
                Assert.Equal(1L, bridge.CheckTargetDiscoveryStartedCount)
                releaseNearest.TrySetResult(()) |> ignore

                try
                    do! discoveryCompletion.WaitAsync(TimeSpan.FromSeconds(5.0))
                with :? TimeoutException when discoveryCompletion.IsCompleted ->
                    ()

                Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount)
                Assert.Equal(evaluationsBefore, bridge.ProjectEvaluationStartedCount)
                Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)

                let! retry =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            path = Some fx.MainFs
                            speed = Some "trusted"
                            timeoutMs = Some 5000 }
                    )

                Assert.Equal("clean", gs retry "verdict")
                Assert.True(gb retry "analyzed")
                Assert.True(bridge.CheckTargetDiscoveryStartedCount >= 3L)
                Assert.Equal(1L, bridge.ProjectTypeCheckStartCount)
            finally
                releaseNearest.TrySetResult(()) |> ignore
                fx.ResetClean()
        }

    [<Theory>]
    [<InlineData("trusted")>]
    [<InlineData("fast")>]
    member _.``project discovery cannot start fallback scan after nearest lookup exhausts deadline``
        (speed: string)
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let fallbackReached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseFallback = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let blockBeforeFallback () =
                fallbackReached.TrySetResult(()) |> ignore
                releaseFallback.Task.GetAwaiter().GetResult()

            let bridge =
                FcsBridge(
                    checkProjectDiscoveryBeforeFallbackOverride = blockBeforeFallback,
                    freshProjectCheckWorkerOverride = (fun _ -> Task.FromResult([||])),
                    freshProjectCheckConcurrencyOverride = 1
                )

            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")
            let projectDirectory = Path.GetDirectoryName(fx.ProbeFsproj)
            let evaluationsBefore = bridge.ProjectEvaluationStartedCount

            let completeSnapshot expectation =
                bindCompleteSnapshot expectation CheckFsacSnapshot.empty |> Task.FromResult

            try
                let checkTask =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "project"
                            projectPath = Some projectDirectory
                            speed = Some speed
                            timeoutMs = Some 300 },
                        fsacSnapshot = completeSnapshot
                    )

                do! fallbackReached.Task.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))
                let! result = checkTask.WaitAsync(TestTiming.watchdog (TimeSpan.FromSeconds(2.0)))

                let discoveryCompletion =
                    bridge.TryGetCheckTargetDiscoveryCompletionForTest("project", projectDirectory, None)
                    |> Option.defaultWith (fun () -> failwith "The project discovery worker was not observable.")

                Assert.Equal("unknown", gs result "verdict")
                Assert.False(gb result "analyzed")
                Assert.False(releaseFallback.Task.IsCompleted)
                Assert.False(
                    discoveryCompletion.IsCompleted,
                    "The caller deadline must not masquerade as discovery-worker completion."
                )
                Assert.Equal(0L, bridge.CheckProjectDiscoveryFallbackCount)
                Assert.Equal(evaluationsBefore, bridge.ProjectEvaluationStartedCount)
                Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)

                releaseFallback.TrySetResult(()) |> ignore

                try
                    do! discoveryCompletion.WaitAsync(TimeSpan.FromSeconds(5.0))
                with :? TimeoutException when discoveryCompletion.IsCompleted ->
                    ()
            finally
                releaseFallback.TrySetResult(()) |> ignore

            Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount)
            Assert.Equal(0L, bridge.CheckProjectDiscoveryFallbackCount)
            Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)

            let! retry =
                bridge.Check(
                    { bareCheck with
                        scope = Some "project"
                        projectPath = Some projectDirectory
                        speed = Some speed
                        timeoutMs = Some 5000 },
                    fsacSnapshot = completeSnapshot
                )

            Assert.True(
                String.Equals("clean", gs retry "verdict", StringComparison.Ordinal),
                retry.ToJsonString()
            )
            Assert.True(gb retry "analyzed")
            Assert.Equal(1L, bridge.CheckProjectDiscoveryFallbackCount)
            Assert.Equal((if speed = "trusted" then 1L else 0L), bridge.ProjectTypeCheckStartCount)
        }

    [<Theory>]
    [<InlineData("trusted", true)>]
    [<InlineData("fast", true)>]
    [<InlineData("trusted", false)>]
    [<InlineData("fast", false)>]
    member _.``check discovery all waiters expired never starts fallback``(speed: string, beforeCompute: bool) : Task =
        task {
            fx.ResetClean()
            let reached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            use leaderDeadline = new System.Threading.CancellationTokenSource()
            use followerDeadline = new System.Threading.CancellationTokenSource()
            let mutable callers = 0

            let nextDeadline () =
                match System.Threading.Interlocked.Increment(&callers) with
                | 1 -> leaderDeadline.Token
                | 2 -> followerDeadline.Token
                | _ -> System.Threading.CancellationToken.None // Subsequent retry is live.

            // The synchronous hook models an uncancellable filesystem operation.
            let block () =
                reached.TrySetResult(()) |> ignore
                release.Task.GetAwaiter().GetResult()

            let bridge =
                FcsBridge(
                    checkTargetDiscoveryBeforeComputeOverride = (fun () -> if beforeCompute then block ()),
                    checkProjectDiscoveryBeforeFallbackOverride = (fun () -> if not beforeCompute then block ()),
                    checkTargetDiscoveryDeadlineTokenOverride = nextDeadline,
                    freshProjectCheckWorkerOverride = (fun _ -> Task.FromResult([||]))
                )

            use requests =
                new BlockedCheckRequests((fun () -> release.TrySetResult(()) |> ignore), fx.ResetClean)

            requests.VerifyBeforeReset(fun () ->
                Assert.Equal(0, bridge.CheckTargetDiscoveryActiveCount)
                Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount))

            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")
            let target = Path.GetDirectoryName(fx.ProbeFsproj)
            let args = { bareCheck with scope = Some "project"; projectPath = Some target; speed = Some speed }
            let snapshot expectation = bindCompleteSnapshot expectation CheckFsacSnapshot.empty |> Task.FromResult

            let leader = bridge.Check(args, fsacSnapshot = snapshot) |> requests.Track
            do! reached.Task.WaitAsync(TimeSpan.FromSeconds(10.0))

            let completion =
                bridge.TryGetCheckTargetDiscoveryCompletionForTest("project", target, None)
                |> Option.defaultWith (fun () -> failwith "Missing discovery worker.")
                |> requests.TrackWorkerExpectingTimeout

            let follower = bridge.Check(args, fsacSnapshot = snapshot) |> requests.Track
            do! waitForDiscoveryState "Both callers must join the retained worker." (fun () -> bridge.CheckTargetDiscoveryWaiterCount = 2)

            leaderDeadline.Cancel()
            followerDeadline.Cancel()
            let! leaderResult = leader.WaitAsync(TimeSpan.FromSeconds(10.0))
            let! followerResult = follower.WaitAsync(TimeSpan.FromSeconds(10.0))
            Assert.Equal("unknown", gs leaderResult "verdict")
            Assert.Equal("unknown", gs followerResult "verdict")
            do! waitForDiscoveryState "Expired callers must release only their waiters." (fun () -> bridge.CheckTargetDiscoveryWaiterCount = 0)
            Assert.False(completion.IsCompleted)
            Assert.Equal(1, bridge.CheckTargetDiscoveryActiveCount)
            Assert.Equal(1, bridge.CheckTargetDiscoveryInFlightCount)
            Assert.Equal(1L, bridge.CheckTargetDiscoveryStartedCount)

            release.TrySetResult(()) |> ignore
            do! settleDiscovery completion
            Assert.Equal(0L, bridge.CheckProjectDiscoveryFallbackCount)
            Assert.Equal(0, bridge.CheckTargetDiscoveryActiveCount)
            Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount)
            Assert.Equal(0L, bridge.ProjectTypeCheckStartCount)

            let! retry = bridge.Check(args, fsacSnapshot = snapshot) |> requests.Track
            Assert.Equal("clean", gs retry "verdict")
            Assert.Equal(1L, bridge.CheckProjectDiscoveryFallbackCount)
            Assert.Equal(0, bridge.CheckTargetDiscoveryActiveCount)
            Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount)
        }

    [<Theory>]
    [<InlineData("trusted", true, true)>]
    [<InlineData("fast", true, true)>]
    [<InlineData("trusted", false, true)>]
    [<InlineData("fast", false, true)>]
    [<InlineData("trusted", true, false)>]
    [<InlineData("fast", true, false)>]
    [<InlineData("trusted", false, false)>]
    [<InlineData("fast", false, false)>]
    member _.``check discovery expiring one same key waiter preserves the other``
        (speed: string, expireLeader: bool, beforeCompute: bool) : Task =
        task {
            fx.ResetClean()
            let reached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            use leaderDeadline = new System.Threading.CancellationTokenSource()
            use followerDeadline = new System.Threading.CancellationTokenSource()
            let mutable callers = 0

            let nextDeadline () =
                if System.Threading.Interlocked.Increment(&callers) = 1 then leaderDeadline.Token
                else followerDeadline.Token

            let block () =
                reached.TrySetResult(()) |> ignore
                release.Task.GetAwaiter().GetResult()

            let bridge =
                FcsBridge(
                    checkTargetDiscoveryBeforeComputeOverride = (fun () -> if beforeCompute then block ()),
                    checkProjectDiscoveryBeforeFallbackOverride = (fun () -> if not beforeCompute then block ()),
                    checkTargetDiscoveryDeadlineTokenOverride = nextDeadline,
                    freshProjectCheckWorkerOverride = (fun _ -> Task.FromResult([||]))
                )

            use requests =
                new BlockedCheckRequests((fun () -> release.TrySetResult(()) |> ignore), fx.ResetClean)

            requests.VerifyBeforeReset(fun () ->
                Assert.Equal(0, bridge.CheckTargetDiscoveryActiveCount)
                Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount))

            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")
            let target = Path.GetDirectoryName(fx.ProbeFsproj)
            let args = { bareCheck with scope = Some "project"; projectPath = Some target; speed = Some speed }
            let snapshot expectation = bindCompleteSnapshot expectation CheckFsacSnapshot.empty |> Task.FromResult

            let leader = bridge.Check(args, fsacSnapshot = snapshot) |> requests.Track
            do! reached.Task.WaitAsync(TimeSpan.FromSeconds(10.0))

            let completion =
                bridge.TryGetCheckTargetDiscoveryCompletionForTest("project", target, None)
                |> Option.defaultWith (fun () -> failwith "Missing discovery worker.")
                |> requests.TrackWorker

            let follower = bridge.Check(args, fsacSnapshot = snapshot) |> requests.Track
            do! waitForDiscoveryState "Follower must join before either deadline expires." (fun () -> bridge.CheckTargetDiscoveryWaiterCount = 2)

            let expiring, surviving = if expireLeader then leader, follower else follower, leader
            (if expireLeader then leaderDeadline else followerDeadline).Cancel()
            let! expired = expiring.WaitAsync(TimeSpan.FromSeconds(10.0))
            Assert.Equal("unknown", gs expired "verdict")
            do! waitForDiscoveryState "The surviving caller must keep its waiter." (fun () -> bridge.CheckTargetDiscoveryWaiterCount = 1)
            Assert.False(surviving.IsCompleted)
            Assert.Equal(1, bridge.CheckTargetDiscoveryActiveCount)
            Assert.Equal(1L, bridge.CheckTargetDiscoveryStartedCount)
            Assert.Equal(0L, bridge.CheckTargetDiscoveryRejectedCount)

            release.TrySetResult(()) |> ignore
            let! result = surviving.WaitAsync(TimeSpan.FromSeconds(10.0))
            do! completion
            Assert.True(gs result "verdict" = "clean", result.ToJsonString())
            Assert.True(gb result "analyzed")
            Assert.Equal(1L, bridge.CheckProjectDiscoveryFallbackCount)
            Assert.Equal(1L, bridge.CheckTargetDiscoveryStartedCount)
            Assert.Equal(0, bridge.CheckTargetDiscoveryActiveCount)
            Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount)
        }

    [<Theory>]
    [<InlineData("trusted")>]
    [<InlineData("fast")>]
    member _.``check discovery retains busy admission and exact worker cleanup across retries``(speed: string) : Task =
        task {
            fx.ResetClean()
            let firstReached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseFirst = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let replacementReached = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseReplacement = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            use firstDeadline = new System.Threading.CancellationTokenSource()
            let mutable callers = 0
            let mutable workers = 0

            let block () =
                match System.Threading.Interlocked.Increment(&workers) with
                | 1 ->
                    firstReached.TrySetResult(()) |> ignore
                    releaseFirst.Task.GetAwaiter().GetResult()
                | 2 ->
                    replacementReached.TrySetResult(()) |> ignore
                    releaseReplacement.Task.GetAwaiter().GetResult()
                | _ -> () // The distinct-key retry completes normally.

            let bridge =
                FcsBridge(
                    checkTargetDiscoveryBeforeComputeOverride = block,
                    checkTargetDiscoveryDeadlineTokenOverride = (fun () ->
                        if System.Threading.Interlocked.Increment(&callers) = 1 then firstDeadline.Token
                        else System.Threading.CancellationToken.None),
                    freshProjectCheckWorkerOverride = (fun _ -> Task.FromResult([||]))
                )

            use requests =
                new BlockedCheckRequests(
                    (fun () ->
                        releaseFirst.TrySetResult(()) |> ignore
                        releaseReplacement.TrySetResult(()) |> ignore),
                    fx.ResetClean
                )

            requests.VerifyBeforeReset(fun () ->
                Assert.Equal(0, bridge.CheckTargetDiscoveryActiveCount)
                Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount))

            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")
            let target = Path.GetDirectoryName(fx.ProbeFsproj)
            let args = { bareCheck with scope = Some "project"; projectPath = Some target; speed = Some speed }
            // Same actual project, distinct discovery key (directory vs explicit fsproj).
            let distinctArgs = { args with projectPath = Some fx.ProbeFsproj }
            let snapshot expectation = bindCompleteSnapshot expectation CheckFsacSnapshot.empty |> Task.FromResult

            let first = bridge.Check(args, fsacSnapshot = snapshot) |> requests.Track
            do! firstReached.Task.WaitAsync(TimeSpan.FromSeconds(10.0))

            let completion =
                bridge.TryGetCheckTargetDiscoveryCompletionForTest("project", target, None)
                |> Option.defaultWith (fun () -> failwith "Missing original discovery worker.")
                |> requests.TrackWorkerExpectingTimeout

            let staleCleanup =
                bridge.TryGetCheckTargetDiscoveryCleanupForTest("project", target, None)
                |> Option.defaultWith (fun () -> failwith "Missing exact-worker cleanup.")

            firstDeadline.Cancel()
            let! expired = first.WaitAsync(TimeSpan.FromSeconds(10.0))
            Assert.Equal("unknown", gs expired "verdict")

            let! busy =
                bridge.Check(distinctArgs, fsacSnapshot = snapshot)
                |> requests.Track
                |> fun work -> work.WaitAsync(TimeSpan.FromSeconds(10.0))

            Assert.Equal("unknown", gs busy "verdict")
            Assert.Equal("fcs_worker_busy", gs busy["blockingReason"] "errorKind")
            Assert.Equal(1L, bridge.CheckTargetDiscoveryRejectedCount)
            Assert.Equal(1L, bridge.CheckTargetDiscoveryStartedCount)
            Assert.Equal(1, bridge.CheckTargetDiscoveryActiveCount)
            Assert.Equal(1, bridge.CheckTargetDiscoveryInFlightCount)
            Assert.False(completion.IsCompleted)

            releaseFirst.TrySetResult(()) |> ignore
            do! settleDiscovery completion
            Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount)
            Assert.Equal(0L, bridge.CheckProjectDiscoveryFallbackCount)

            let replacement = bridge.Check(args, fsacSnapshot = snapshot) |> requests.Track
            do! replacementReached.Task.WaitAsync(TimeSpan.FromSeconds(10.0))

            let replacementCompletion =
                bridge.TryGetCheckTargetDiscoveryCompletionForTest("project", target, None)
                |> Option.defaultWith (fun () -> failwith "Missing replacement discovery worker.")
                |> requests.TrackWorker

            Assert.NotSame(completion, replacementCompletion)

            staleCleanup ()
            staleCleanup ()
            Assert.Equal(1, bridge.CheckTargetDiscoveryInFlightCount)
            let follower = bridge.Check(args, fsacSnapshot = snapshot) |> requests.Track
            do! waitForDiscoveryState "Stale cleanup must leave the replacement joinable." (fun () -> bridge.CheckTargetDiscoveryWaiterCount = 2)
            Assert.Equal(2L, bridge.CheckTargetDiscoveryStartedCount)
            Assert.Equal(1L, bridge.CheckTargetDiscoveryRejectedCount)

            releaseReplacement.TrySetResult(()) |> ignore
            let! replacementResult = replacement.WaitAsync(TimeSpan.FromSeconds(10.0))
            let! followerResult = follower.WaitAsync(TimeSpan.FromSeconds(10.0))
            do! replacementCompletion
            Assert.Equal("clean", gs replacementResult "verdict")
            Assert.Equal("clean", gs followerResult "verdict")
            Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount)

            let! retry = bridge.Check(distinctArgs, fsacSnapshot = snapshot) |> requests.Track
            Assert.Equal("clean", gs retry "verdict")
            Assert.Equal(3L, bridge.CheckTargetDiscoveryStartedCount)
            Assert.Equal(0, bridge.CheckTargetDiscoveryActiveCount)
            Assert.Equal(0, bridge.CheckTargetDiscoveryInFlightCount)
        }

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

    [<Theory>]
    [<InlineData(".fsproj")>]
    [<InlineData(".sln")>]
    [<InlineData(".slnx")>]
    member _.``check path rejects project and solution files with structured InvalidArgument``(extension: string) : Task =
        task {
            let nonSourcePath = Path.Combine(fx.Root, $"NotSource{extension}")
            File.WriteAllText(nonSourcePath, "")
            let bridge = FcsBridge()

            let! result =
                bridge.Check(
                    { bareCheck with
                        path = Some nonSourcePath
                        projectPath = Some fx.ProbeFsproj }
                )

            Assert.Equal("error", gs result "status")
            Assert.Equal("InvalidArgument", gs result "errorKind")
            Assert.Contains(".fs/.fsi", gs result "message")
        }

    [<Fact>]
    member _.``check path accepts an F# signature file``() : Task =
        task {
            let bridge = FcsBridge()

            let! result =
                bridge.Check(
                    { bareCheck with
                        path = Some fx.StandaloneFsi
                        projectPath = Some fx.ProbeFsproj }
                )

            Assert.Equal("succeeded", gs result "status")
            Assert.Equal("file", gs result "scope")
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
    member _.``check(snippet) surfaces the caller's error without wrapper noise duplicates or temp paths (#187)`` () : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            // Bare snippet — the shape agents actually paste: no module header,
            // exactly one genuine type error.
            let! result =
                bridge.Check(
                    { bareCheck with
                        snippet = Some "let x: int = \"nope\"\n"
                        projectPath = Some fx.ProbeFsproj }
                )

            Assert.Equal("errors", gs result "verdict")
            let diags = result["diagnostics"] :?> JsonArray

            let codes =
                [ for d in diags -> d["errorNumberText"].GetValue<string>() ]

            // FS0222 (must begin with namespace/module) and FS0225 (source-file
            // bookkeeping) describe the synthetic temp-file wrapper, never the
            // snippet's content.
            Assert.DoesNotContain("FS0222", codes)
            Assert.DoesNotContain("FS0225", codes)
            Assert.Contains("FS0001", codes)

            // No byte-identical duplicates (FS0222 used to arrive twice).
            let rendered = [ for d in diags -> d.ToJsonString() ]
            Assert.Equal<string list>(List.distinct rendered, rendered)

            // The caller never had a file; the harness temp path must not leak.
            for d in diags do
                Assert.Equal("snippet", d["file"].GetValue<string>())
        }

    [<Fact>]
    member _.``check(snippet) bare expression code without a module header is clean (#187)`` () : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            let! result =
                bridge.Check(
                    { bareCheck with
                        snippet = Some "let answer = 42\n"
                        projectPath = Some fx.ProbeFsproj }
                )

            // The missing-module FS0222 is our wrapper's artifact; a valid bare
            // snippet must not come back as verdict=errors because of it.
            Assert.Equal("clean", gs result "verdict")
            Assert.Equal(0, gi result "errorCount")
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
    member _.``trusted file aborted answer carries a typed project failure``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()

            let bridge =
                FcsBridge(
                    trustedFileCheckAnswerOverride =
                        (fun _ -> FSharp.Compiler.CodeAnalysis.FSharpCheckFileAnswer.Aborted)
                )

            let! result =
                bridge.Check(
                    { bareCheck with
                        scope = Some "file"
                        path = Some fx.MainFs
                        speed = Some "trusted"
                        projectPath = Some fx.ProbeFsproj }
                )

            Assert.Equal("unknown", gs result "verdict")
            Assert.False(gb result "analyzed")
            Assert.False(gb result "groundTruth")
            Assert.Equal("project_failure", gs (result["blockingReason"]) "errorKind")
            Assert.False(gb (result["blockingReason"]) "retryable")
        }

    [<Fact>]
    member _.``trusted file exceptions retain timeout cancellation busy and generic kinds``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()

            let cases: (string * bool * (unit -> exn)) array =
                [| "timeout", true, (fun () -> TimeoutException("simulated trusted file timeout"))
                   "cancelled", true, (fun () -> OperationCanceledException("simulated trusted file cancellation"))
                   "fcs_worker_busy", true, (fun () -> ProjectEvaluationBusyException(fx.ProbeFsproj))
                   "fcs_worker_busy",
                   true,
                   (fun () -> BoundedCheckWorkBusyException("file type-check", fx.MainFs))
                   "project_failure", false, (fun () -> InvalidOperationException("simulated trusted file failure")) |]

            for expectedErrorKind, expectedRetryable, createFailure in cases do
                let bridge =
                    FcsBridge(
                        trustedFileCheckAnswerOverride =
                            (fun _ -> raise (createFailure ()))
                    )

                let! result =
                    bridge.Check(
                        { bareCheck with
                            scope = Some "file"
                            path = Some fx.MainFs
                            speed = Some "trusted"
                            projectPath = Some fx.ProbeFsproj }
                    )

                Assert.Equal("unknown", gs result "verdict")
                Assert.False(gb result "analyzed")
                Assert.False(gb result "groundTruth")
                Assert.Equal(expectedErrorKind, gs (result["blockingReason"]) "errorKind")
                Assert.Equal(expectedRetryable, gb (result["blockingReason"]) "retryable")
        }

    [<Fact>]
    member _.``fast snapshot success observed after the deadline is never conclusive``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let mutable deadlineProbeCalled = false

            let bridge =
                FcsBridge(
                    checkFastSnapshotDeadlineExpiredOverride = (fun () ->
                        deadlineProbeCalled <- true
                        true)
                )

            let! warmed = bridge.ProbeProjectOptions(fx.ProbeFsproj)
            Assert.True(Result.isOk warmed, $"Could not warm project options: {warmed}")

            let conclusiveError (expectation: CheckFsacExpectation) =
                { bindCompleteSnapshot expectation CheckFsacSnapshot.empty with
                    ErrorCount = 1
                    MostRecentAnalyzedAt = Some "2026-01-01T00:00:00Z" }
                |> Task.FromResult

            let! result =
                bridge.Check(
                    { bareCheck with
                        scope = Some "project"
                        speed = Some "fast"
                        projectPath = Some fx.ProbeFsproj
                        timeoutMs = Some 5000 },
                    fsacSnapshot = conclusiveError
                )

            Assert.True(deadlineProbeCalled, "The regression must cross the post-success deadline check.")
            Assert.Equal("unknown", gs result "verdict")
            Assert.False(gb result "analyzed")
            Assert.Contains("timed out", (gs result "reason").ToLowerInvariant())
            Assert.Equal("timeout", gs (result["blockingReason"]) "errorKind")
            Assert.True(gb (result["blockingReason"]) "retryable")
        }

    [<Fact>]
    member _.``fast file snapshot timeout remains a retryable timeout``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            let timedOutSnapshot (_: CheckFsacExpectation) =
                Task.FromException<CheckFsacSnapshot>(TimeoutException("simulated file snapshot timeout"))

            let! result =
                bridge.Check(
                    { bareCheck with
                        scope = Some "file"
                        path = Some fx.MainFs
                        speed = Some "fast"
                        projectPath = Some fx.ProbeFsproj
                        timeoutMs = Some 5_000 },
                    fsacSnapshot = timedOutSnapshot
                )

            Assert.Equal("unknown", gs result "verdict")
            Assert.False(gb result "analyzed")
            Assert.Equal("timeout", gs (result["blockingReason"]) "errorKind")
            Assert.True(gb (result["blockingReason"]) "retryable")
        }

    [<Fact>]
    member _.``dispatcher preserves typed LSP timeout cancellation and generic failures``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()

            let args =
                { bareCheck with
                    scope = Some "file"
                    path = Some fx.MainFs
                    speed = Some "fast"
                    projectPath = Some fx.ProbeFsproj
                    timeoutMs = Some 5_000 }

            let runThroughDispatcher injectFailure =
                task {
                    use lspBridge =
                        new FsAutoCompleteBridge(diagnosticsSnapshotBeforeReadOverride = injectFailure)

                    let fcsBridge = FcsBridge()
                    return! CheckDispatch.run fcsBridge lspBridge (CheckRequest.Check args)
                }

            let cases: (string * (unit -> unit)) array =
                [| "timeout", (fun () -> raise (TimeoutException("simulated LSP timeout")))
                   "cancelled", (fun () -> raise (OperationCanceledException("simulated request cancellation")))
                   "fsac_unavailable", (fun () -> raise (InvalidOperationException("simulated LSP failure"))) |]

            for expectedErrorKind, injectFailure in cases do
                let! result = runThroughDispatcher injectFailure
                Assert.Equal("unknown", gs result "verdict")
                Assert.False(gb result "analyzed")
                Assert.Equal(expectedErrorKind, gs (result["blockingReason"]) "errorKind")
                Assert.True(gb (result["blockingReason"]) "retryable")
        }

    [<Fact>]
    member _.``legacy LSP gate-busy reason remains a retryable worker-busy blocker``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            let legacyBusySnapshot (expectation: CheckFsacExpectation) =
                let filesNode files =
                    JsonArray(files |> Array.map jstr) :> JsonNode

                jobj
                    [ "status", jstr "not_ready"
                      "ready", jbool false
                      "reason", jstr "lsp_lifecycle_gate_busy"
                      "message", jstr "legacy gate-busy envelope"
                      "lspState", jstr "warming"
                      "contextMatched", jbool false
                      "complete", jbool false
                      "diagnosticsFileCount", jint 0
                      "expectedFiles", filesNode expectation.ExpectedFiles
                      "receivedFiles", filesNode [||]
                      "missingFiles", filesNode expectation.ExpectedFiles
                      "staleFiles", filesNode [||]
                      "blockingReasons", JsonArray() :> JsonNode
                      "result", JsonObject() :> JsonNode ]
                :> JsonNode
                |> CheckFsacSnapshot.ofDiagnosticsResponse
                |> Task.FromResult

            let! result =
                bridge.Check(
                    { bareCheck with
                        scope = Some "file"
                        path = Some fx.MainFs
                        speed = Some "fast"
                        projectPath = Some fx.ProbeFsproj
                        timeoutMs = Some 5_000 },
                    fsacSnapshot = legacyBusySnapshot
                )

            Assert.Equal("unknown", gs result "verdict")
            Assert.False(gb result "analyzed")
            Assert.Equal("fcs_worker_busy", gs (result["blockingReason"]) "errorKind")
            Assert.True(gb (result["blockingReason"]) "retryable")
        }

    [<Fact>]
    member _.``trusted cancellation remains distinct from timeout and project failure``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()

            let cancelledWorker
                (_: FSharp.Compiler.CodeAnalysis.FSharpProjectOptions)
                : Task<FSharp.Compiler.Diagnostics.FSharpDiagnostic array> =
                Task.FromException<FSharp.Compiler.Diagnostics.FSharpDiagnostic array>(
                    OperationCanceledException("simulated cancellation")
                )

            let bridge = FcsBridge(freshProjectCheckWorkerOverride = cancelledWorker)

            let! result =
                bridge.Check(
                    { bareCheck with
                        scope = Some "project"
                        speed = Some "trusted"
                        projectPath = Some fx.ProbeFsproj
                        timeoutMs = Some 5_000 }
                )

            Assert.Equal("unknown", gs result "verdict")
            Assert.False(gb result "analyzed")
            Assert.Equal("cancelled", gs (result["blockingReason"]) "errorKind")
            Assert.True(gb (result["blockingReason"]) "retryable")
        }

    [<Fact>]
    member _.``P1-02 fast check requires a current publication for every evaluated SourceFile``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            let partialSnapshot (withError: bool) (expectation: CheckFsacExpectation) =
                Assert.Equal("project", expectation.Scope)
                Assert.True(expectation.Complete)
                Assert.True(expectation.ContextFingerprint.IsSome)
                Assert.Equal(64, expectation.ContextFingerprint.Value.Length)
                Assert.True(
                    expectation.ExpectedFiles.Length > 1,
                    "the evaluated project must contain multiple SourceFiles"
                )

                let received = expectation.ExpectedFiles |> Array.take 1
                let missing = expectation.ExpectedFiles |> Array.skip 1

                let diagnostics =
                    if withError then
                        JsonArray(
                            jobj
                                [ "severity", jint 1
                                  "message", jstr "current error"
                                  "file", jstr received[0]
                                  "range", JsonObject() :> JsonNode ]
                            :> JsonNode
                        )
                        :> JsonNode
                    else
                        JsonArray() :> JsonNode

                { CheckFsacSnapshot.empty with
                    Status = "ok"
                    Ready = true
                    ContextMatched = true
                    Complete = false
                    ExpectedFiles = Array.copy expectation.ExpectedFiles
                    ReceivedFiles = received
                    MissingFiles = missing
                    StaleFiles = [||]
                    SessionGeneration = Some 11L
                    FailureReason = None
                    AnalyzedFileCount = received.Length
                    ErrorCount = if withError then 1 else 0
                    MostRecentAnalyzedAt = Some "2026-01-01T00:00:00Z"
                    Diagnostics = diagnostics }

            let run withError =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some fx.ProbeFsproj
                        scope = Some "project"
                        speed = Some "fast" },
                    fsacSnapshot = (partialSnapshot withError >> Task.FromResult)
                )

            // One explicit empty publication plus three absent files is incomplete,
            // not a workspace-wide clean result.
            let! missingOnly = run false
            Assert.Equal("unknown", gs missingOnly "verdict")
            Assert.False(gb missingOnly "complete")
            Assert.False(gb missingOnly "analyzed")
            Assert.True(gi missingOnly "expectedFileCount" > 1)
            Assert.Equal(1, gi missingOnly "receivedFileCount")
            Assert.Equal(gi missingOnly "expectedFileCount" - 1, gi missingOnly "missingFileCount")
            Assert.Equal(0, gi missingOnly "staleFileCount")

            // A current positive error remains actionable even though the rest of the
            // requested scope is incomplete; the response still advertises complete=false.
            let! partialError = run true
            Assert.Equal("errors", gs partialError "verdict")
            Assert.False(gb partialError "complete")
            Assert.Equal(1, gi partialError "errorCount")
            Assert.Contains("incomplete", gs partialError "reason")
        }

    [<Fact>]
    member _.``fast file check fingerprint covers same-mtime edits in another source of the owning project``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            let args =
                { bareCheck with
                    path = Some fx.MainFs
                    projectPath = Some fx.ProbeFsproj
                    scope = Some "file"
                    speed = Some "fast" }

            let! before = captureFastExpectation bridge args
            let! unchanged = captureFastExpectation bridge args
            Assert.Equal(before.ContextFingerprint, unchanged.ContextFingerprint)

            let fixedStamp = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            let changedHelpers = cleanHelpers.Replace("a + b", "a - b", StringComparison.Ordinal)
            Assert.Equal(cleanHelpers.Length, changedHelpers.Length)
            File.WriteAllText(fx.HelpersFs, changedHelpers)
            File.SetLastWriteTimeUtc(fx.HelpersFs, fixedStamp)

            let! after = captureFastExpectation bridge args
            Assert.True(before.ContextFingerprint.IsSome)
            Assert.True(after.ContextFingerprint.IsSome)
            Assert.NotEqual(before.ContextFingerprint, after.ContextFingerprint)
        }

    [<Fact>]
    member _.``fast project check fingerprint covers same-mtime MSBuild option edits``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()
            let fixedStamp = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

            let withDefine value =
                probeProject.Replace(
                    "</PropertyGroup>",
                    $"<DefineConstants>{value}</DefineConstants></PropertyGroup>",
                    StringComparison.Ordinal
                )

            let firstProject = withDefine "FIRST"
            let otherProject = withDefine "OTHER"
            Assert.Equal(firstProject.Length, otherProject.Length)
            File.WriteAllText(fx.ProbeFsproj, firstProject)
            File.SetLastWriteTimeUtc(fx.ProbeFsproj, fixedStamp)

            let args =
                { bareCheck with
                    projectPath = Some fx.ProbeFsproj
                    scope = Some "project"
                    speed = Some "fast" }

            let! before = captureFastExpectation bridge args
            let! unchanged = captureFastExpectation bridge args
            Assert.Equal(before.ContextFingerprint, unchanged.ContextFingerprint)

            File.WriteAllText(fx.ProbeFsproj, otherProject)
            File.SetLastWriteTimeUtc(fx.ProbeFsproj, fixedStamp)

            let! after = captureFastExpectation bridge args
            Assert.True(before.ContextFingerprint.IsSome)
            Assert.True(after.ContextFingerprint.IsSome)
            Assert.NotEqual(before.ContextFingerprint, after.ContextFingerprint)
        }

    [<Fact>]
    member _.``P1-02 fast check treats stale expected files as unknown in the absence of current errors``() : Task =
        task {
            let bridge = FcsBridge()

            let staleSnapshot (expectation: CheckFsacExpectation) =
                { CheckFsacSnapshot.empty with
                    Status = "ok"
                    Ready = true
                    ContextMatched = true
                    Complete = false
                    ExpectedFiles = Array.copy expectation.ExpectedFiles
                    ReceivedFiles = Array.copy expectation.ExpectedFiles
                    MissingFiles = [||]
                    StaleFiles = expectation.ExpectedFiles |> Array.take 1
                    SessionGeneration = Some 12L
                    FailureReason = None
                    AnalyzedFileCount = expectation.ExpectedFiles.Length }

            let! result =
                bridge.Check(
                    { bareCheck with
                        path = Some fx.MainFs
                        projectPath = Some fx.ProbeFsproj
                        scope = Some "file"
                        speed = Some "fast" },
                    fsacSnapshot = (staleSnapshot >> Task.FromResult)
                )

            Assert.Equal("unknown", gs result "verdict")
            Assert.False(gb result "complete")
            Assert.Equal(0, gi result "missingFileCount")
            Assert.Equal(1, gi result "staleFileCount")
            Assert.Contains("stale", (gs result "reason").ToLowerInvariant())
        }

    [<Fact>]
    member _.``fast check cannot call an empty fileGlob selection clean``() : Task =
        task {
            let bridge = FcsBridge()

            let emptySelection (_: CheckFsacExpectation) =
                { CheckFsacSnapshot.empty with
                    Status = "ok"
                    Ready = true
                    ContextMatched = true
                    Complete = true
                    ExpectedFiles = [||]
                    ReceivedFiles = [||]
                    MissingFiles = [||]
                    StaleFiles = [||]
                    SessionGeneration = Some 13L
                    FailureReason =
                        Some "fileGlob matched no evaluated source files in the requested workspace."
                    MostRecentAnalyzedAt = Some "2026-01-01T00:00:00Z" }

            let! result =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some fx.ProbeSlnx
                        scope = Some "workspace"
                        fileGlob = Some "src/DoesNotExist/*.fs"
                        speed = Some "fast" },
                    fsacSnapshot = (emptySelection >> Task.FromResult)
                )

            Assert.Equal("unknown", gs result "verdict")
            Assert.False(gb result "complete")
            Assert.False(gb result "analyzed")
            Assert.Equal(0, gi result "expectedFileCount")
            Assert.Contains("fileGlob", gs result "reason")
        }

    [<Fact>]
    member _.``P1-02 diagnostics projection preserves typed coverage metadata``() =
        let response =
            JsonNode.Parse(
                """{
                  "status":"ok",
                  "lspState":"ready",
                  "contextMatched":true,
                  "complete":false,
                  "sessionGeneration":42,
                  "diagnosticsFileCount":1,
                  "expectedFiles":["/scope/A.fs","/scope/B.fs"],
                  "receivedFiles":["/scope/A.fs"],
                  "missingFiles":["/scope/B.fs"],
                  "staleFiles":[],
                  "result":{"/scope/A.fs":[{"severity":1,"message":"broken"}]}
                }"""
            )

        let snapshot = CheckFsacSnapshot.ofDiagnosticsResponse response
        Assert.Equal("ok", snapshot.Status)
        Assert.True(snapshot.Ready)
        Assert.True(snapshot.ContextMatched)
        Assert.False(snapshot.Complete)
        Assert.Equal(Some 42L, snapshot.SessionGeneration)
        Assert.Equal(2, snapshot.ExpectedFiles.Length)
        Assert.Single(snapshot.ReceivedFiles) |> ignore
        Assert.Single(snapshot.MissingFiles) |> ignore
        Assert.Empty(snapshot.StaleFiles)
        Assert.Equal(1, snapshot.ErrorCount)

    [<Fact>]
    member _.``P1-02 diagnostics context accepts a loaded solution member and rejects an unrelated project``() : Task =
        task {
            let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_check_context_{Guid.NewGuid():N}")
            let projectA = Path.Combine(root, "A", "A.fsproj")
            let projectB = Path.Combine(root, "B", "B.fsproj")
            let projectC = Path.Combine(root, "C", "C.fsproj")
            let fileB = Path.Combine(root, "B", "B.fs")
            let fileC = Path.Combine(root, "C", "C.fs")
            let solution = Path.Combine(root, "AB.slnx")

            try
                Directory.CreateDirectory(Path.GetDirectoryName(projectA)) |> ignore
                Directory.CreateDirectory(Path.GetDirectoryName(projectB)) |> ignore
                Directory.CreateDirectory(Path.GetDirectoryName(projectC)) |> ignore
                File.WriteAllText(projectA, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
                File.WriteAllText(projectB, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
                File.WriteAllText(projectC, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
                File.WriteAllText(fileB, "module B")
                File.WriteAllText(fileC, "module C")

                File.WriteAllText(
                    solution,
                    "<Solution><Project Path=\"A/A.fsproj\" /><Project Path=\"B/B.fsproj\" /></Solution>"
                )

                use lsp =
                    new FsAutoCompleteBridge(fsacCommandOverride = "this-command-must-not-be-started")

                let! selected =
                    lsp.SetProject(
                        { projectPath = solution
                          workspacePath = None
                          restartLsp = Some false }
                    )

                Assert.Equal("ok", gs selected "status")

                // C is unrelated: reject it before EnsureStarted can touch the fake command.
                let! unrelated = lsp.DiagnosticsForContext(Some projectC, [| fileC |], None, None)
                Assert.Equal("context_mismatch", gs unrelated "status")
                Assert.False(gb unrelated "contextMatched")
                Assert.True(lsp.FsacProcess.IsNone)

                // B is a loaded member of active solution A+B, so it passes context
                // binding. The deliberately missing executable then fails at startup,
                // proving this was not rejected as a mismatch.
                let! memberContext = lsp.DiagnosticsForContext(Some projectB, [| fileB |], None, None)
                Assert.Equal("infrastructure_error", gs memberContext "status")
                Assert.True(gb memberContext "contextMatched")
                Assert.True(lsp.FsacProcess.IsNone)
            finally
                if Directory.Exists root then
                    Directory.Delete(root, true)
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
                    fsacSnapshot = (fun expectation ->
                        Task.FromResult(bindCompleteSnapshot expectation snap))
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
    member _.``speed=fast totalDiagnostics counts all severities so it agrees with the surfaced list (#133)``
        ()
        : Task =
        task {
            let bridge = FcsBridge()

            // A ready snapshot holding ONLY an info-severity (LSP code 3) diagnostic and
            // ZERO errors/warnings. Before #133 the fast path set
            // totalDiagnostics = ErrorCount + WarningCount = 0, yet severity="all"/"information"
            // surfaced the info node — a non-empty list with a zero count.
            let json =
                "{ \"lspState\": \"ready\", \"mostRecentAnalyzedAt\": \"2026-01-01T00:00:00Z\","
                + " \"diagnosticsFileCount\": 1, \"result\": { \"/probe/Info.fs\": ["
                + " { \"severity\": 3, \"message\": \"naming hint\", \"file\": \"/probe/Info.fs\","
                + " \"range\": { \"startLine\": 1, \"startColumn\": 0, \"endLine\": 1, \"endColumn\": 5 } } ] } }"

            let snap = CheckFsacSnapshot.ofDiagnosticsResponse (JsonNode.Parse json)

            let fastCheck (severity: string option) =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some fx.ProbeFsproj
                        speed = Some "fast"
                        scope = Some "project"
                        severity = severity },
                    fsacSnapshot = (fun expectation ->
                        Task.FromResult(bindCompleteSnapshot expectation snap))
                )

            // severity=all: the info node is surfaced AND counted — list length == totalDiagnostics.
            let! atAll = fastCheck (Some "all")
            let allDiags = atAll["diagnostics"] :?> JsonArray
            Assert.Equal(1, allDiags.Count)
            Assert.Equal(allDiags.Count, gi atAll "totalDiagnostics")
            // Verdict stays error-based and the full-set error/warning tallies are unaffected.
            Assert.Equal("clean", gs atAll "verdict")
            Assert.Equal(0, gi atAll "errorCount")
            Assert.Equal(0, gi atAll "warningCount")

            // severity=information: same node, same agreement.
            let! atInfo = fastCheck (Some "information")
            let infoDiags = atInfo["diagnostics"] :?> JsonArray
            Assert.Equal(1, infoDiags.Count)
            Assert.Equal(infoDiags.Count, gi atInfo "totalDiagnostics")

            // Default floor = error: the info node is below the floor (empty list), but the
            // full-set total still counts it — list ⊆ total, never list > total.
            let! atError = fastCheck None
            Assert.Equal(0, (atError["diagnostics"] :?> JsonArray).Count)
            Assert.Equal(1, gi atError "totalDiagnostics")
        }

    // ── #190: totalDiagnostics disagreed with errorCount+warningCount whenever the full
    // set held Info/Hidden diagnostics, and nothing in the payload said why — agents read
    // the gap as "hidden findings" and burned time hunting for them (field report #100).
    // infoCount and belowSeverityFloorCount must close that gap on every path.
    [<Fact>]
    member _.``speed=fast: infoCount + belowSeverityFloorCount explain a zero errorCount/warningCount but nonzero totalDiagnostics (#190)``
        ()
        : Task =
        task {
            let bridge = FcsBridge()

            // The exact field-evidence shape from #190: zero errors/warnings, nonzero
            // totalDiagnostics, because the full set holds one info-severity (LSP code 3)
            // diagnostic that the default severity floor excludes from the list.
            let json =
                "{ \"lspState\": \"ready\", \"mostRecentAnalyzedAt\": \"2026-01-01T00:00:00Z\","
                + " \"diagnosticsFileCount\": 1, \"result\": { \"/probe/Info.fs\": ["
                + " { \"severity\": 3, \"message\": \"naming hint\", \"file\": \"/probe/Info.fs\","
                + " \"range\": { \"startLine\": 1, \"startColumn\": 0, \"endLine\": 1, \"endColumn\": 5 } } ] } }"

            let snap = CheckFsacSnapshot.ofDiagnosticsResponse (JsonNode.Parse json)

            let fastCheck (severity: string option) =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some fx.ProbeFsproj
                        speed = Some "fast"
                        scope = Some "project"
                        severity = severity },
                    fsacSnapshot = (fun expectation ->
                        Task.FromResult(bindCompleteSnapshot expectation snap))
                )

            let! atError = fastCheck None
            Assert.Equal(0, gi atError "errorCount")
            Assert.Equal(0, gi atError "warningCount")
            Assert.Equal(1, gi atError "totalDiagnostics")
            Assert.Equal(1, gi atError "infoCount")

            Assert.Equal(
                gi atError "totalDiagnostics",
                gi atError "errorCount" + gi atError "warningCount" + gi atError "infoCount"
            )

            Assert.Equal(1, gi atError "belowSeverityFloorCount")
            Assert.Contains("severity=\"all\"", gs atError "diagnosticsNote")

            // severity=all: the floor no longer excludes anything, so the gap closes and
            // the note disappears — the 50-item cap (diagnosticsTruncated) must never
            // leak into belowSeverityFloorCount either.
            let! atAll = fastCheck (Some "all")
            Assert.Equal(0, gi atAll "belowSeverityFloorCount")
            Assert.Null(atAll["diagnosticsNote"])
        }

    [<Fact>]
    member _.``speed=trusted: totalDiagnostics = errorCount + warningCount + infoCount on a real in-process check (#190)``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            // A genuine FS0025 incomplete-match warning, zero errors — no mocked FSAC
            // snapshot; this exercises the identity through a real, fresh FCS check.
            File.WriteAllText(
                fx.MainFs,
                String.concat
                    "\n"
                    [ "module Probe.Main"
                      ""
                      "let classify (x: int option) ="
                      "    match x with"
                      "    | Some v -> v"
                      "" ]
            )

            let! result =
                bridge.Check(
                    { bareCheck with
                        path = Some fx.MainFs
                        projectPath = Some fx.ProbeFsproj
                        scope = Some "file"
                        speed = Some "trusted" }
                )

            Assert.Equal(0, gi result "errorCount")
            Assert.True(gi result "warningCount" > 0, "Expected the incomplete-match warning to be counted")

            // Exact values, not just the identity: the fixture guarantees no Info/Hidden
            // diagnostics (infoCount = 0, a real tally via countInfoDiagnostics — not a
            // total-error-warning remainder that would make this assertion tautological),
            // and the default `error` floor excludes exactly the warning(s) from the list,
            // so belowSeverityFloorCount must equal warningCount precisely, not merely be
            // nonzero.
            Assert.Equal(0, gi result "infoCount")
            Assert.Equal(gi result "warningCount", gi result "belowSeverityFloorCount")

            Assert.Equal(
                gi result "totalDiagnostics",
                gi result "errorCount" + gi result "warningCount" + gi result "infoCount"
            )

            // Default floor (error) excludes the warning from the list, so the gap must
            // be explained.
            Assert.True(gi result "belowSeverityFloorCount" > 0)
            Assert.Contains("severity=\"all\"", gs result "diagnosticsNote")

            // severity=all surfaces it and the gap closes.
            let! atAll =
                bridge.Check(
                    { bareCheck with
                        path = Some fx.MainFs
                        projectPath = Some fx.ProbeFsproj
                        scope = Some "file"
                        speed = Some "trusted"
                        severity = Some "all" }
                )

            Assert.Equal(0, gi atAll "belowSeverityFloorCount")
            Assert.Null(atAll["diagnosticsNote"])
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

    // ── #138: diagnostics array is always capped so a large error wall can't overflow ──
    [<Fact>]
    member _.``OVERFLOW-GUARD: a project with 60+ errors caps diagnostics at 50 but keeps counts accurate``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()

            // 60 independent bool-to-int bindings → 60 distinct type errors.
            let manyErrors =
                String.concat "\n" ([ "module Probe.Main"; "" ] @ [ for i in 1..60 -> $"let v{i}: int = true" ])

            File.WriteAllText(fx.MainFs, manyErrors)
            let bridge = FcsBridge()

            let! result = bridge.Check({ bareCheck with projectPath = Some fx.ProbeFsproj })

            Assert.Equal("errors", gs result "verdict")
            // Counts stay FULL-set accurate — the cap is presentation-only.
            let errorCount = gi result "errorCount"
            Assert.True(errorCount > 50, $"errorCount should reflect all 60 errors, got {errorCount}")
            Assert.True(gi result "totalDiagnostics" > 50, "totalDiagnostics must count the full set")

            // The surfaced array is capped to 50 and flagged truncated.
            let diagnostics = result["diagnostics"] :?> JsonArray
            Assert.Equal(50, diagnostics.Count)
            Assert.True(gb result "diagnosticsTruncated", "a 60-error wall must report diagnosticsTruncated=true")
        }

    // ── #138: the restore-aware early-return must NOT fire on a restored project ──
    [<Fact>]
    member _.``RESTORED-GUARD: a restored project takes the normal clean path, never the unrestored verdict``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            let! result = bridge.Check({ bareCheck with projectPath = Some fx.ProbeFsproj })

            // The fixture is restored/built, so the probe sees resolved references and the
            // verdict is the genuine ground-truth — NOT the unrestored short-circuit.
            Assert.Equal("clean", gs result "verdict")
            Assert.True(gb result "analyzed", "a restored project must be genuinely analyzed")
            Assert.Null(result["restoreStatus"]) // the unrestored extra must be absent
            Assert.False(gb result "diagnosticsTruncated", "a clean project has nothing to truncate")
        }

    [<Fact>]
    member _.``project scope makes its downstream coverage boundary explicit``() : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")
            fx.ResetClean()
            let bridge = FcsBridge()

            let! projectOnly =
                bridge.Check(
                    { bareCheck with
                        scope = Some "project"
                        projectPath = Some fx.ProbeFsproj }
                )

            Assert.Equal("clean", gs projectOnly "verdict")
            Assert.False(gb projectOnly "downstreamProjectsChecked")
            Assert.Equal("workspace", gs projectOnly "recommendedScope")

            let note = gs projectOnly "coverageNote"
            Assert.Contains("not checked", note)
            Assert.Contains("scope=\"workspace\"", note)

            let! ambiguousWorkspace =
                bridge.Check(
                    { bareCheck with
                        scope = Some "workspace"
                        projectPath = Some fx.ProbeFsproj }
                )

            Assert.Equal("invalid_args", gs ambiguousWorkspace "status")
            Assert.Contains("solution", gs ambiguousWorkspace "message")
            Assert.Contains("downstream consumers", gs ambiguousWorkspace "message")

            let! workspace =
                bridge.Check(
                    { bareCheck with
                        scope = Some "workspace"
                        projectPath = Some fx.Root }
                )

            Assert.Null(workspace["downstreamProjectsChecked"])
            Assert.Null(workspace["coverageNote"])
            Assert.Null(workspace["recommendedScope"])
        }

    // ── #205: perProject entries carried only errorCount/warningCount, so the
    // per-project breakdown couldn't be reconciled against a project's own full-set
    // count the way the top level already can (#190/#202). infoCount is now a genuine
    // tally (countInfoDiagnostics) on each perProject entry too — this proves a
    // workspace sweep's per-project errorCount/warningCount/infoCount for a project
    // matches EXACTLY what a direct scope="project" check reports for that same
    // project, and that the workspace totals are the sum of the members' own
    // breakdowns rather than an independently-computed remainder that merely agrees.
    [<Fact>]
    member _.``workspace scope's perProject infoCount reconciles with that member's own scope="project" check (#205)``
        ()
        : Task =
        task {
            Assert.True((fx.BuildExitCode = 0), $"Probe fixture build failed (exit {fx.BuildExitCode}):\n{fx.BuildLog}")

            Assert.True(
                (fx.BetaBuildExitCode = 0),
                $"Beta fixture build failed (exit {fx.BetaBuildExitCode}):\n{fx.BetaBuildLog}"
            )

            fx.ResetClean()
            let bridge = FcsBridge()

            // Ground truth: Beta's own standalone project-scope check. Beta.fs carries a
            // permanent FS0025 incomplete-match warning — zero errors, one real warning,
            // zero Info/Hidden diagnostics.
            let! betaAlone =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some fx.BetaFsproj
                        scope = Some "project" }
                )

            Assert.Equal(0, gi betaAlone "errorCount")
            Assert.True(gi betaAlone "warningCount" > 0, "Expected the incomplete-match warning to be counted")
            Assert.Equal(0, gi betaAlone "infoCount")

            Assert.Equal(
                gi betaAlone "totalDiagnostics",
                gi betaAlone "errorCount" + gi betaAlone "warningCount" + gi betaAlone "infoCount"
            )

            // A workspace sweep over the fixture root covers Probe (clean) AND Beta.
            let! workspace =
                bridge.Check(
                    { bareCheck with
                        projectPath = Some fx.Root
                        scope = Some "workspace" }
                )

            Assert.Equal("clean", gs workspace "verdict")
            let perProject = (workspace["perProject"] :?> JsonArray) |> Seq.cast<JsonNode>

            let findProject (name: string) =
                perProject
                |> Seq.tryFind (fun p -> gs p "project" = name)
                |> Option.defaultWith (fun () ->
                    let seen = perProject |> Seq.map (fun p -> gs p "project") |> String.concat ", "
                    failwith $"Expected a perProject entry named '{name}'; observed: [{seen}]")

            let betaEntry = findProject "Beta"
            let probeEntry = findProject "Probe"

            // The reconciliation: Beta's perProject breakdown inside the workspace sweep
            // matches its own standalone scope="project" identity exactly, field for
            // field — not just a nonzero remainder that happens to agree.
            //
            // infoCount is exercised only at zero here (betaAlone's own infoCount is 0,
            // asserted above) — no construct reachable through a real trusted FCS check
            // was found to produce Info/Hidden severity (probed: FS0025, a
            // #nowarn-suppressed FS0025, [<Obsolete>] usage, shadowing, an unused open).
            // The nonzero path is exercised only on the mocked FSAC fast path at :2008,
            // which cannot reach perProject (speed="fast" never builds it — see the class
            // comment above this test). So this reconciles wiring/scoping — that Beta's
            // own countInfoDiagnostics tally survives being embedded in a workspace sweep
            // instead of leaking or getting overwritten — not that the tally is capable of
            // being nonzero; a hardcoded `jint 0` at the emitter would also pass this.
            Assert.Equal(gi betaAlone "errorCount", gi betaEntry "errorCount")
            Assert.Equal(gi betaAlone "warningCount", gi betaEntry "warningCount")
            Assert.Equal(gi betaAlone "infoCount", gi betaEntry "infoCount")

            // Probe is genuinely clean — its own breakdown is all zeros, not a leak of
            // Beta's counts or the workspace aggregate. This only catches a leak from
            // scoping the tally over the accumulating allDiags instead of Probe's own
            // diags because Beta sorts before Probe in the sweep (listProjects sorts
            // full paths, and "Beta" < "Probe"): Beta's diags land in allDiags first, so
            // if Probe's entry were built off allDiags instead of its own diags, it would
            // show Beta's nonzero warningCount here, not agree with it by coincidence.
            Assert.Equal(0, gi probeEntry "errorCount")
            Assert.Equal(0, gi probeEntry "warningCount")
            Assert.Equal(0, gi probeEntry "infoCount")

            // Workspace-level identity still holds (#190), and is exactly the sum of the
            // two members' own perProject breakdowns.
            Assert.Equal(gi betaEntry "errorCount" + gi probeEntry "errorCount", gi workspace "errorCount")
            Assert.Equal(gi betaEntry "warningCount" + gi probeEntry "warningCount", gi workspace "warningCount")
            Assert.Equal(gi betaEntry "infoCount" + gi probeEntry "infoCount", gi workspace "infoCount")

            Assert.Equal(
                gi workspace "totalDiagnostics",
                gi workspace "errorCount" + gi workspace "warningCount" + gi workspace "infoCount"
            )
        }
