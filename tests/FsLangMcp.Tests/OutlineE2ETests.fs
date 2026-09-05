module FsLangMcp.Tests.OutlineE2ETests

/// End-to-end integration tests for ProjectOutline (issue #78 fixes).
///
/// These tests call FcsBridge.ProjectOutline against a real temp fsproj to
/// exercise the production filter/pagination code path — not a replicated helper.
///
/// Coverage:
///   F. No filter → all files returned
///   G. Filter regex → only matching entries pass through
///   H. Evil pattern (a+)+$ — completes well under 1 s (DoS regression guard)
///   I. Overlong filter (>1024 chars) → InvalidArgException
///   J. Cursor pagination round-trip: page-1 has nextCursor + truncated=true;
///      page-2 with that cursor returns the rest, no overlap
///   K. memberCounts replaces _summary sentinel (issue #82): emitted in both
///      summaryOnly modes, no synthetic kinds, exact values, filter-aware,
///      and not shrunk by maxResultsPerFile truncation.

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge
open FsLangMcp.Cursor
open FsLangMcp.Program

// ─── Helpers ──────────────────────────────────────────────────────────────────

/// Create a temp directory containing a project with two .fs source files.
/// File1: module Alpha with a type Timer and a let getValue.
/// File2: module Beta with a let processData and a type Channel.
/// Returns (projectPath, tempRoot).
let private createFixtureProject () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_outline_e2e_{runId}")
    Directory.CreateDirectory(root) |> ignore

    // File1.fs — contains "Timer"
    let file1 = Path.Combine(root, "File1.fs")
    File.WriteAllText(
        file1,
        """module Alpha

type Timer = { interval: int }

let getValue () = 42
"""
    )

    // File2.fs — contains "Channel"
    let file2 = Path.Combine(root, "File2.fs")
    File.WriteAllText(
        file2,
        """module Beta

type Channel = { name: string }

let processData (x: int) = x * 2
"""
    )

    let projectPath = Path.Combine(root, "Fixture.fsproj")
    File.WriteAllText(
        projectPath,
        String.concat
            Environment.NewLine
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup>"
              "    <TargetFramework>net10.0</TargetFramework>"
              "  </PropertyGroup>"
              "  <ItemGroup>"
              "    <Compile Include=\"File1.fs\" />"
              "    <Compile Include=\"File2.fs\" />"
              "  </ItemGroup>"
              "</Project>" ]
    )

    projectPath, root

/// Three files in deterministic path order: the first has no "Needle" entry,
/// while the second and third do. Used to prove entry filtering precedes file paging.
let private createFilteredPaginationProject () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_outline_filtered_%s{runId}")
    Directory.CreateDirectory(root) |> ignore

    File.WriteAllText(Path.Combine(root, "A.fs"), "module A\n\nlet unrelated = 1\n")
    File.WriteAllText(Path.Combine(root, "B.fs"), "module B\n\nlet NeedleOne = 1\n")
    File.WriteAllText(Path.Combine(root, "C.fs"), "module C\n\nlet NeedleTwo = 2\n")

    let projectPath = Path.Combine(root, "FilteredFixture.fsproj")

    File.WriteAllText(
        projectPath,
        String.concat
            Environment.NewLine
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
              "  <ItemGroup>"
              "    <Compile Include=\"A.fs\" />"
              "    <Compile Include=\"B.fs\" />"
              "    <Compile Include=\"C.fs\" />"
              "  </ItemGroup>"
              "</Project>" ]
    )

    projectPath, root

/// One file with the sole matching declaration after FileOutline's default
/// 200-definition response cap. ProjectOutline must not call this exhaustive.
let private createTailMatchProject () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_outline_tail_%s{runId}")
    Directory.CreateDirectory(root) |> ignore

    let declarations =
        [ for index in 1..220 -> $"let value%d{index} = %d{index}" ]
        @ [ "let NeedleAfterTwoHundred = 221" ]

    File.WriteAllText(
        Path.Combine(root, "Large.fs"),
        String.concat Environment.NewLine ([ "module Large"; "" ] @ declarations)
    )

    let projectPath = Path.Combine(root, "Large.fsproj")

    File.WriteAllText(
        projectPath,
        String.concat
            Environment.NewLine
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
              "  <ItemGroup><Compile Include=\"Large.fs\" /></ItemGroup>"
              "</Project>" ]
    )

    projectPath, root

let private defaultArgs projectPath : FcsProjectOutlineArgs =
    { projectPath = Some projectPath
      workspacePath = None
      includePrivate = None
      includeTests = None
      includeGeneratedFiles = None
      maxFiles = None
      maxResultsPerFile = None
      summaryOnly = Some false   // full detail so we can inspect entries
      cursor = None
      filter = None
      nameContains = None
      timeoutMs = None }

let private filesArray (result: JsonNode) =
    result["files"] :?> JsonArray

let private successfulControlledOutline () =
    let entry = JsonObject()
    entry["name"] <- JsonValue.Create("Needle")
    entry["signature"] <- JsonValue.Create("unit -> int")
    entry["kind"] <- JsonValue.Create("function")
    let entries = JsonArray()
    entries.Add(entry)
    let outline = JsonObject()
    outline["status"] <- JsonValue.Create("succeeded")
    outline["entries"] <- entries
    outline["count"] <- JsonValue.Create(1)
    outline["totalDefinitionCount"] <- JsonValue.Create(1)
    outline["returnedEntryCount"] <- JsonValue.Create(1)
    outline["entriesComplete"] <- JsonValue.Create(true)
    outline :> JsonNode

// ─── F. No filter — all files included ────────────────────────────────────────

[<Fact>]
let ``ProjectOutline without filter returns all project files`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result = bridge.ProjectOutline({ defaultArgs projectPath with maxFiles = Some 100 })

            Assert.Equal("ok", result["status"].GetValue<string>())
            let operationalCoverage = result["coverage"]
            Assert.True(operationalCoverage["complete"].GetValue<bool>())
            Assert.Equal(2, operationalCoverage["filesRequested"].GetValue<int>())
            Assert.Equal(2, operationalCoverage["filesScanned"].GetValue<int>())
            Assert.Equal(1L, bridge.ProjectEvaluationStartedCount)
            Assert.Equal(1L, bridge.ProjectOptionsLoadCount)

            let files = filesArray result
            // We have 2 .fs files; both should be included.
            Assert.Equal(2, files.Count)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline lists compiled files missing on disk in unresolvedFiles`` () : System.Threading.Tasks.Task =
    task {
        // Per-file outlineStatus errors can scroll past on a paginated response; the
        // aggregate must make the coverage gap prominent (#160).
        let projectPath, root = createFixtureProject ()

        File.WriteAllText(
            projectPath,
            String.concat
                Environment.NewLine
                [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                  "  <ItemGroup>"
                  "    <Compile Include=\"File1.fs\" />"
                  "    <Compile Include=\"Missing.fs\" />"
                  "  </ItemGroup>"
                  "</Project>" ]
        )

        let bridge = FcsBridge()

        try
            let! result = bridge.ProjectOutline({ defaultArgs projectPath with maxFiles = Some 100 })

            Assert.Equal("partial", result["status"].GetValue<string>())
            let operationalCoverage = result["coverage"]
            Assert.Equal(1, operationalCoverage["filesFailed"].GetValue<int>())

            let unresolved =
                result["unresolvedFiles"] :?> JsonArray
                |> Seq.map (fun n -> n.GetValue<string>())
                |> Seq.toList

            Assert.Contains(unresolved, fun p -> p.EndsWith "Missing.fs")
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── G. Filter regex — only matching entries pass through ─────────────────────

[<Fact>]
let ``ProjectOutline with filter 'Timer' returns entries matching Timer`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        filter = Some "Timer" }
                )

            Assert.Equal("ok", result["status"].GetValue<string>())

            let files = filesArray result

            // At least one file must have entries that match 'Timer'.
            let hasTimerEntry =
                files
                |> Seq.cast<JsonNode>
                |> Seq.exists (fun file ->
                    let entries = file["entries"] :?> JsonArray

                    entries
                    |> Seq.cast<JsonNode>
                    |> Seq.exists (fun entry ->
                        match entry["name"] with
                        | null -> false
                        | n -> n.GetValue<string>().Contains("Timer", StringComparison.OrdinalIgnoreCase)))

            Assert.True(hasTimerEntry, "Expected at least one 'Timer' entry after filtering")

            // Entries with 'Channel' must NOT appear when filter is 'Timer'.
            let hasChannelEntry =
                files
                |> Seq.cast<JsonNode>
                |> Seq.exists (fun file ->
                    let entries = file["entries"] :?> JsonArray

                    entries
                    |> Seq.cast<JsonNode>
                    |> Seq.exists (fun entry ->
                        match entry["name"] with
                        | null -> false
                        | n -> n.GetValue<string>().Contains("Channel", StringComparison.OrdinalIgnoreCase)))

            Assert.False(hasChannelEntry, "Channel entries must be excluded when filter is 'Timer'")
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline with alternation filter 'Timer|Channel' matches both types`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        filter = Some "Timer|Channel" }
                )

            Assert.Equal("ok", result["status"].GetValue<string>())

            let files = filesArray result

            let allEntryNames =
                files
                |> Seq.cast<JsonNode>
                |> Seq.collect (fun file ->
                    (file["entries"] :?> JsonArray)
                    |> Seq.cast<JsonNode>
                    |> Seq.choose (fun entry ->
                        match entry["name"] with
                        | null -> None
                        | n -> Some(n.GetValue<string>())))
                |> Seq.toList

            let hasTimer = allEntryNames |> List.exists (fun n -> n.Contains("Timer", StringComparison.OrdinalIgnoreCase))
            let hasChannel = allEntryNames |> List.exists (fun n -> n.Contains("Channel", StringComparison.OrdinalIgnoreCase))

            Assert.True(hasTimer, "Expected Timer entry with 'Timer|Channel' filter")
            Assert.True(hasChannel, "Expected Channel entry with 'Timer|Channel' filter")
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── H. Evil pattern — structural DoS regression guard ──────────────────────

[<Fact>]
let ``outline watchdog retains and observes a late-faulting producer`` () : Task =
    task {
        let fixtureName = "OutlineE2ETests.watchdog-regression"

        let root =
            Path.Combine(Path.GetTempPath(), $"fslangmcp_watchdog_regression_{Guid.NewGuid():N}")

        Directory.CreateDirectory(root) |> ignore
        TestRunTrace.fixture "fixture_init" fixtureName root

        let releaseProducer =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let watchdogSignal =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let lateFailure = InvalidOperationException("late producer fault marker")
        let mutable deferredCleanup: Task<TestRunTrace.DeferredCleanupResult> option = None

        let producer =
            task {
                do! releaseProducer.Task
                return raise lateFailure
            }

        let transferProducerOwnership producerTask =
            deferredCleanup <-
                Some(
                    TestRunTrace.deferOwnedDirectoryUntilProducerCompletes
                        fixtureName
                        root
                        producerTask
                )

        let guarded =
            TestTiming.awaitProducerWithWatchdogTask
                "outline hostile-filter probe"
                (TimeSpan.FromSeconds(17.0))
                watchdogSignal.Task
                transferProducerOwnership
                producer

        watchdogSignal.SetResult(())

        let! watchdogFailure =
            Assert.ThrowsAsync<TestTiming.WatchdogTimeoutException>(fun () -> guarded :> Task)

        let producerWasRetained = deferredCleanup.IsSome
        let producerWasStillRunning = not producer.IsCompleted
        let fixtureExistedWhileProducerRan = Directory.Exists(root)
        releaseProducer.SetResult(())

        let! cleanupResult = deferredCleanup.Value

        Assert.Equal("outline hostile-filter probe", watchdogFailure.OperationName)
        Assert.Equal(TimeSpan.FromSeconds(17.0), watchdogFailure.WatchdogDuration)
        Assert.Null(watchdogFailure.InnerException)

        Assert.Equal(
            "Test watchdog 'outline hostile-filter probe' expired after 00:00:17; underlying producer ownership was transferred for observation and deferred fixture cleanup.",
            watchdogFailure.Message
        )

        Assert.True(producerWasRetained)
        Assert.True(producerWasStillRunning)
        Assert.True(fixtureExistedWhileProducerRan)
        Assert.Same(lateFailure, cleanupResult.ProducerFailure.Value)
        Assert.True(cleanupResult.CleanupFailure.IsNone)
        Assert.True(producer.IsFaulted)
        Assert.False(Directory.Exists(root))
    }

[<Fact>]
let ``outline watchdog preserves a producer TimeoutException identity`` () : Task =
    task {
        let productTimeout = TimeoutException("product timeout marker")
        let producer = Task.FromException<int>(productTimeout)

        let watchdogSignal =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let guarded =
            TestTiming.awaitProducerWithWatchdogTask
                "producer timeout identity probe"
                (TimeSpan.FromSeconds(17.0))
                watchdogSignal.Task
                (fun _ -> Assert.Fail("A producer timeout must not be treated as watchdog expiry."))
                producer

        let! observed =
            Assert.ThrowsAsync<TimeoutException>(fun () -> guarded :> Task)

        Assert.Same(productTimeout, observed)
        Assert.Equal("product timeout marker", observed.Message)
    }

[<Fact>]
let ``ProjectOutline uses a bounded non-backtracking regex for hostile filters`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()
        let fixtureName = "OutlineE2ETests.hostile-filter"
        let mutable deferredCleanup: Task<TestRunTrace.DeferredCleanupResult> option = None
        TestRunTrace.fixture "fixture_init" fixtureName root

        let transferProducerOwnership producerTask =
            deferredCleanup <-
                Some(
                    TestRunTrace.deferOwnedDirectoryUntilProducerCompletes
                        fixtureName
                        root
                        producerTask
                )

        try
            let hostileFilter = ProjectOutlineFilter.compile "(a+)+$"
            let hostileOptions = hostileFilter.Options

            Assert.Equal(
                System.Text.RegularExpressions.RegexOptions.NonBacktracking,
                hostileOptions &&& System.Text.RegularExpressions.RegexOptions.NonBacktracking
            )

            Assert.Equal(
                System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                hostileOptions &&& System.Text.RegularExpressions.RegexOptions.IgnoreCase
            )

            Assert.Equal(TimeSpan.FromMilliseconds(250.0), hostileFilter.MatchTimeout)

            // Warm up FCS: parse the project once without a filter so that
            // projectResultsCache is populated. The watchdog below detects a real
            // hang; it is deliberately not a host-performance assertion.
            let! _ = bridge.ProjectOutline({ defaultArgs projectPath with maxFiles = Some 100 })

            // (a+)+$ is the canonical catastrophic-backtracking pattern.
            // Against a long-ish string without NonBacktracking this would hang.
            let producer =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        filter = Some "(a+)+$" }
                )

            let! result =
                TestTiming.awaitProducer
                    "ProjectOutline hostile-filter probe"
                    (TimeSpan.FromSeconds(10.0))
                    transferProducerOwnership
                    producer

            Assert.Equal("ok", result["status"].GetValue<string>())
        finally
            if deferredCleanup.IsNone then
                TestRunTrace.deleteOwnedDirectory fixtureName root
    }

// ─── I. Overlong filter → InvalidArgException ─────────────────────────────────

[<Fact>]
let ``ProjectOutline with filter longer than 1024 chars throws InvalidArgException`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let overlong = String.replicate 1025 "a"

            // invalidArg inside task{} is captured as a faulted Task; await and unwrap.
            let! ex =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    bridge.ProjectOutline({ defaultArgs projectPath with filter = Some overlong })
                    :> System.Threading.Tasks.Task)

            Assert.Contains("1024", ex.Message)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── J. Cursor pagination round-trip ─────────────────────────────────────────
//
// Project has 2 files. We call with maxFiles=1 to force pagination.
// Page 1 → truncated=true, nextCursor is non-null, exactly 1 file returned.
// Page 2 → using cursor from page 1, truncated=false, returns the remaining file.
// The two file paths must be disjoint (no overlap).

[<Fact>]
let ``ProjectOutline cursor pagination: page-1 has nextCursor and page-2 returns disjoint remainder`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            // ── Page 1 ──────────────────────────────────────────────────────────
            let! page1 =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 1   // force pagination
                        cursor = None }
                )

            Assert.Equal("ok", page1["status"].GetValue<string>())
            Assert.True(page1["truncated"].GetValue<bool>(), "Page 1 must have truncated=true")

            let nextCursorNode = page1["nextCursor"]
            Assert.NotNull(nextCursorNode)
            let nextCursor = nextCursorNode.GetValue<string>()
            Assert.NotEmpty(nextCursor)

            let page1Files =
                filesArray page1
                |> Seq.cast<JsonNode>
                |> Seq.map (fun f -> f["file"].GetValue<string>())
                |> Set.ofSeq

            Assert.Equal(1, page1Files.Count)

            // Cursor must decode correctly.
            match tryDecode nextCursor with
            | Error msg -> Assert.Fail($"nextCursor did not decode: {msg}")
            | Ok payload -> Assert.Equal(1, payload.offset)

            // ── Page 2 ──────────────────────────────────────────────────────────
            let! page2 =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 1
                        cursor = Some nextCursor }
                )

            Assert.Equal("ok", page2["status"].GetValue<string>())
            Assert.False(page2["truncated"].GetValue<bool>(), "Page 2 must have truncated=false (last page)")
            Assert.Null(page2["nextCursor"])

            let page2Files =
                filesArray page2
                |> Seq.cast<JsonNode>
                |> Seq.map (fun f -> f["file"].GetValue<string>())
                |> Set.ofSeq

            Assert.Equal(1, page2Files.Count)

            // No overlap between pages.
            let overlap = Set.intersect page1Files page2Files
            Assert.Empty(overlap)

            // Together they cover both project files.
            let combined = Set.union page1Files page2Files
            Assert.Equal(2, combined.Count)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline applies nameContains before file pagination and cursors only matching files`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFilteredPaginationProject ()
        let bridge = FcsBridge()

        try
            let! page1 =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 1
                        nameContains = Some [ "Needle" ] }
                )

            Assert.Equal("ok", page1["status"].GetValue<string>())
            Assert.True(page1["truncated"].GetValue<bool>())
            let firstEstimate = page1["totalEstimate"]
            let firstTotal = firstEstimate["files"].GetValue<int>()
            Assert.Equal(2, firstTotal)

            let firstFile = filesArray page1 |> Seq.exactlyOne
            Assert.EndsWith("B.fs", firstFile["file"].GetValue<string>())

            let cursor = page1["nextCursor"].GetValue<string>()

            match tryDecode cursor with
            | Ok payload -> Assert.Equal(1, payload.offset)
            | Error message -> Assert.Fail($"nextCursor did not decode: %s{message}")

            let! page2 =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 1
                        nameContains = Some [ "Needle" ]
                        cursor = Some cursor }
                )

            Assert.False(page2["truncated"].GetValue<bool>())
            Assert.Null(page2["nextCursor"])
            let secondEstimate = page2["totalEstimate"]
            let secondTotal = secondEstimate["files"].GetValue<int>()
            Assert.Equal(2, secondTotal)

            let secondFile = filesArray page2 |> Seq.exactlyOne
            Assert.EndsWith("C.fs", secondFile["file"].GetValue<string>())
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline reports partial filter coverage when a file outline aborts`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()

        let outlineOverride (args: FcsFileOutlineArgs) =
            let filePath: string = args.path
            let outline = JsonObject()

            if filePath.EndsWith("File1.fs", StringComparison.Ordinal) then
                outline["status"] <- JsonValue.Create("aborted")
                outline["message"] <- JsonValue.Create("controlled outline abort")
            else
                let entry = JsonObject()
                entry["name"] <- JsonValue.Create("Needle")
                entry["signature"] <- JsonValue.Create("unit -> int")
                entry["kind"] <- JsonValue.Create("function")
                let entries = JsonArray()
                entries.Add(entry)
                outline["status"] <- JsonValue.Create("succeeded")
                outline["entries"] <- entries
                outline["count"] <- JsonValue.Create(1)
                outline["totalDefinitionCount"] <- JsonValue.Create(1)
                outline["returnedEntryCount"] <- JsonValue.Create(1)
                outline["entriesComplete"] <- JsonValue.Create(true)

            System.Threading.Tasks.Task.FromResult(outline :> JsonNode)

        let bridge = FcsBridge(projectOutlineFileOutlineOverride = outlineOverride)

        try
            let! result =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        nameContains = Some [ "Needle" ] }
                )

            Assert.Equal("partial", result["status"].GetValue<string>())
            let totalEstimate = result["totalEstimate"]
            Assert.Equal(1, totalEstimate["files"].GetValue<int>())

            let coverage = result["filterCoverage"]
            Assert.False(coverage["complete"].GetValue<bool>())
            Assert.Equal(2, coverage["filesRequested"].GetValue<int>())
            Assert.Equal(1, coverage["filesAnalyzed"].GetValue<int>())
            Assert.Equal(0, coverage["filesIncomplete"].GetValue<int>())
            Assert.Equal(1, coverage["filesFailed"].GetValue<int>())
            Assert.Equal(1, coverage["matchingFiles"].GetValue<int>())
            Assert.True(coverage["matchingFilesIsLowerBound"].GetValue<bool>())
            Assert.Equal(1, coverage["issuesReturned"].GetValue<int>())
            Assert.False(coverage["issuesTruncated"].GetValue<bool>())

            let failure = coverage["issues"] :?> JsonArray |> Seq.exactlyOne
            Assert.EndsWith("File1.fs", failure["file"].GetValue<string>())
            Assert.Equal("aborted", failure["status"].GetValue<string>())
            Assert.Equal("outline_unavailable", failure["errorKind"].GetValue<string>())
            Assert.Equal("controlled outline abort", failure["message"].GetValue<string>())

            let onlyMatch = filesArray result |> Seq.exactlyOne
            Assert.EndsWith("File2.fs", onlyMatch["file"].GetValue<string>())
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline rejects negative timeout and zero is deterministic unknown without starting work`` () =
    task {
        let mutable starts = 0

        let outlineOverride (_: FcsFileOutlineArgs) =
            Interlocked.Increment(&starts) |> ignore
            Task.FromResult(successfulControlledOutline ())

        let bridge = FcsBridge(projectOutlineFileOutlineOverride = outlineOverride)

        let invalidPath =
            Path.Combine(Path.GetTempPath(), $"missing-outline-{Guid.NewGuid():N}.fsproj")

        let! negative =
            bridge.ProjectOutline(
                { defaultArgs invalidPath with
                    timeoutMs = Some -1 }
            )

        Assert.Equal("invalid_args", negative["status"].GetValue<string>())
        Assert.Equal("invalid_timeout", negative["errorKind"].GetValue<string>())

        let! zero =
            bridge.ProjectOutline(
                { defaultArgs invalidPath with
                    timeoutMs = Some 0 }
            )

        Assert.Equal("unknown", zero["status"].GetValue<string>())
        Assert.Equal("project_outline_timeout", zero["errorKind"].GetValue<string>())
        let operationalCoverage = zero["coverage"]
        Assert.False(operationalCoverage["complete"].GetValue<bool>())
        Assert.Equal(0, Volatile.Read(&starts))
        Assert.Equal(0L, bridge.ProjectEvaluationStartedCount)
    }

[<Fact>]
let ``ProjectOutline times out after a completed file retains worker and never starts the next file`` () =
    task {
        let projectPath, root = createFilteredPaginationProject ()

        let secondStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let blockedOutline =
            TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable starts = 0
        let mutable retained: Task option = None

        let outlineOverride (_: FcsFileOutlineArgs) =
            match Interlocked.Increment(&starts) with
            | 1 -> Task.FromResult(successfulControlledOutline ())
            | 2 ->
                secondStarted.TrySetResult(()) |> ignore
                blockedOutline.Task
            | _ -> Task.FromResult(successfulControlledOutline ())

        let bridge = FcsBridge(projectOutlineFileOutlineOverride = outlineOverride)

        try
            let pending =
                bridge.ProjectOutlineWithinDeadline(
                    { defaultArgs projectPath with
                        maxFiles = Some 1
                        nameContains = Some [ "Needle" ]
                        timeoutMs = Some 150 },
                    CancellationToken.None,
                    fun operation -> retained <- Some operation
                )

            do! secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0))
            let! result = pending
            Assert.Equal("partial", result["status"].GetValue<string>())
            let operationalCoverage = result["coverage"]
            Assert.Equal(1, operationalCoverage["filesScanned"].GetValue<int>())
            Assert.Equal(1, operationalCoverage["filesTimedOut"].GetValue<int>())
            Assert.Equal(1, operationalCoverage["filesNotStarted"].GetValue<int>())
            Assert.False(result["resultSetComplete"].GetValue<bool>())
            Assert.True(result["truncated"].GetValue<bool>())
            Assert.Null(result["nextCursor"])
            Assert.True(result["paginationRestartRequired"].GetValue<bool>())
            Assert.Equal(2, Volatile.Read(&starts))
            Assert.True(retained.IsSome)

            blockedOutline.TrySetResult(successfulControlledOutline ()) |> ignore
            do! retained.Value.WaitAsync(TimeSpan.FromSeconds(2.0))
            Assert.Equal(2, Volatile.Read(&starts))
        finally
            blockedOutline.TrySetResult(successfulControlledOutline ()) |> ignore

            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline cancellation returns partial coverage retains worker and starts no later file`` () =
    task {
        let projectPath, root = createFilteredPaginationProject ()
        use cancellation = new CancellationTokenSource()

        let secondStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let blockedOutline =
            TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable starts = 0
        let mutable retained: Task option = None

        let outlineOverride (_: FcsFileOutlineArgs) =
            match Interlocked.Increment(&starts) with
            | 1 -> Task.FromResult(successfulControlledOutline ())
            | 2 ->
                secondStarted.TrySetResult(()) |> ignore
                blockedOutline.Task
            | _ -> Task.FromResult(successfulControlledOutline ())

        let bridge = FcsBridge(projectOutlineFileOutlineOverride = outlineOverride)

        try
            let pending =
                bridge.ProjectOutlineWithinDeadline(
                    { defaultArgs projectPath with
                        maxFiles = Some 3
                        timeoutMs = Some 5_000 },
                    cancellation.Token,
                    fun operation -> retained <- Some operation
                )

            do! secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0))
            cancellation.Cancel()
            let! result = pending
            Assert.Equal("partial", result["status"].GetValue<string>())
            let operationalCoverage = result["coverage"]
            Assert.Equal(1, operationalCoverage["filesScanned"].GetValue<int>())
            Assert.Equal(1, operationalCoverage["filesFailed"].GetValue<int>())
            Assert.Equal(1, operationalCoverage["filesNotStarted"].GetValue<int>())
            Assert.Equal(2, Volatile.Read(&starts))
            Assert.True(retained.IsSome)

            blockedOutline.TrySetResult(successfulControlledOutline ()) |> ignore
            do! retained.Value.WaitAsync(TimeSpan.FromSeconds(2.0))
            Assert.Equal(2, Volatile.Read(&starts))
        finally
            blockedOutline.TrySetResult(successfulControlledOutline ()) |> ignore

            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline pre-wait cancellation retains hot worker and outer FCS gate`` () =
    task {
        let projectPath, root = createFilteredPaginationProject ()
        use operationCancellation = new CancellationTokenSource()
        use gate = new SemaphoreSlim(1, 1)

        let blockedOutline =
            TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable starts = 0
        let mutable lateStarts = 0

        let outlineOverride (_: FcsFileOutlineArgs) =
            Interlocked.Increment(&starts) |> ignore
            // Deterministic pre-wait race: the hot task exists and remains incomplete,
            // while cancellation becomes visible before awaitWithinDeadline calls WaitAsync.
            operationCancellation.Cancel()
            blockedOutline.Task

        let bridge = FcsBridge(projectOutlineFileOutlineOverride = outlineOverride)

        try
            let! result =
                runLimitedWithTimeoutRetained gate CancellationToken.None (Some 5_000) (fun remaining retainUntil ->
                    bridge.ProjectOutlineWithinDeadline(
                        { defaultArgs projectPath with
                            timeoutMs = remaining },
                        operationCancellation.Token,
                        retainUntil
                    ))

            Assert.Equal("unknown", result["status"].GetValue<string>())
            Assert.Equal(1, Volatile.Read(&starts))
            Assert.Equal(0, gate.CurrentCount)

            let! queued =
                runLimitedWithTimeoutRetained gate CancellationToken.None (Some 0) (fun _ _ ->
                    Interlocked.Increment(&lateStarts) |> ignore
                    Task.FromResult(successfulControlledOutline ()))

            Assert.Equal("fcs_admission_timeout", queued["errorKind"].GetValue<string>())
            Assert.Equal(0, Volatile.Read(&lateStarts))

            blockedOutline.TrySetResult(successfulControlledOutline ()) |> ignore
            do! gate.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2.0))
            Assert.Equal(1, Volatile.Read(&starts))
            gate.Release() |> ignore
        finally
            blockedOutline.TrySetResult(successfulControlledOutline ()) |> ignore

            if Directory.Exists(root) then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline options timeout returns unknown and retains actual evaluation`` () =
    task {
        let projectPath, root = createFixtureProject ()
        let fixtureName = "OutlineE2ETests.options-timeout"
        TestRunTrace.fixture "fixture_init" fixtureName root

        let evaluationStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let releaseEvaluation =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable retained: Task option = None
        let mutable pending: Task<JsonNode> option = None
        let mutable retainedObserved = false
        let mutable deferredCleanup: Task<TestRunTrace.DeferredCleanupResult> option = None

        let transferProducerOwnership producerTask =
            deferredCleanup <-
                Some(
                    TestRunTrace.deferOwnedDirectoryUntilProducerCompletes
                        fixtureName
                        root
                        producerTask
                )

        let beforeLoad (_: string) =
            task {
                evaluationStarted.TrySetResult(()) |> ignore
                do! releaseEvaluation.Task
            }
            :> Task

        let bridge = FcsBridge(projectEvaluationBeforeLoadOverride = beforeLoad)

        try
            let request =
                bridge.ProjectOutlineWithinDeadline(
                    { defaultArgs projectPath with
                        timeoutMs = Some 150 },
                    CancellationToken.None,
                    fun operation -> retained <- Some operation
                )

            pending <- Some request
            do! evaluationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2.0))
            let! result = request
            Assert.Equal("unknown", result["status"].GetValue<string>())
            let operationalCoverage = result["coverage"]
            Assert.Equal(0, operationalCoverage["filesScanned"].GetValue<int>())
            Assert.Equal(2, operationalCoverage["filesNotStarted"].GetValue<int>())
            Assert.Equal(1, bridge.ProjectEvaluationActiveCount)
            Assert.True(retained.IsSome)

            releaseEvaluation.TrySetResult(()) |> ignore

            let retainedProducer =
                task {
                    do! retained.Value
                }

            let! productTimeout =
                Assert.ThrowsAsync<TimeoutException>(fun () ->
                    TestTiming.awaitProducer
                        "ProjectOutline retained options evaluation"
                        (TimeSpan.FromSeconds(10.0))
                        transferProducerOwnership
                        retainedProducer
                    :> Task)

            retainedObserved <- true
            Assert.Equal("The project-options worker has no active callers.", productTimeout.Message)
            Assert.Null(productTimeout.InnerException)
            Assert.Equal(0, bridge.ProjectEvaluationActiveCount)
        finally
            releaseEvaluation.TrySetResult(()) |> ignore

            match deferredCleanup with
            | Some _ -> ()
            | None ->
                match retained with
                | Some operation when not retainedObserved ->
                    transferProducerOwnership operation
                | _ ->
                    match pending with
                    | Some operation when not operation.IsCompleted ->
                        transferProducerOwnership operation
                    | _ ->
                        TestRunTrace.deleteOwnedDirectory fixtureName root
    }

[<Fact>]
let ``ProjectOutline does not claim exhaustive filtering when a match is beyond FileOutline's cap``
    ()
    : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createTailMatchProject ()
        let bridge = FcsBridge()

        try
            let! result =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        nameContains = Some [ "NeedleAfterTwoHundred" ] }
                )

            Assert.Equal("partial", result["status"].GetValue<string>())
            let coverage = result["filterCoverage"]
            Assert.False(coverage["complete"].GetValue<bool>())
            Assert.Equal(1, coverage["filesRequested"].GetValue<int>())
            Assert.Equal(0, coverage["filesAnalyzed"].GetValue<int>())
            Assert.Equal(1, coverage["filesIncomplete"].GetValue<int>())
            Assert.Equal(0, coverage["filesFailed"].GetValue<int>())
            Assert.True(coverage["matchingFilesIsLowerBound"].GetValue<bool>())

            let issue = coverage["issues"] :?> JsonArray |> Seq.exactlyOne
            Assert.EndsWith("Large.fs", issue["file"].GetValue<string>())
            Assert.Equal("incomplete", issue["status"].GetValue<string>())
            Assert.Equal("outline_entries_incomplete", issue["errorKind"].GetValue<string>())
            Assert.Contains("definitions", issue["message"].GetValue<string>())
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline directly scoped to a test project includes Tests fs by default`` () : System.Threading.Tasks.Task =
    task {
        let runId = Guid.NewGuid().ToString("N")
        let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_outline_tests_%s{runId}")
        Directory.CreateDirectory(root) |> ignore

        let sourcePath = Path.Combine(root, "Tests.fs")
        File.WriteAllText(sourcePath, "module Direct.Tests\n\nlet testValue = 42\n")

        let projectPath = Path.Combine(root, "Direct.Tests.fsproj")

        File.WriteAllText(
            projectPath,
            String.concat
                Environment.NewLine
                [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                  "  <PropertyGroup>"
                  "    <TargetFramework>net10.0</TargetFramework>"
                  "    <IsTestProject>true</IsTestProject>"
                  "  </PropertyGroup>"
                  "  <ItemGroup><Compile Include=\"Tests.fs\" /></ItemGroup>"
                  "</Project>" ]
        )

        let bridge = FcsBridge()

        try
            let! result = bridge.ProjectOutline(defaultArgs projectPath)

            Assert.Equal("ok", result["status"].GetValue<string>())
            Assert.Equal(1, filesArray result |> Seq.length)
            let onlyFile = filesArray result |> Seq.exactlyOne
            Assert.EndsWith("Tests.fs", onlyFile["file"].GetValue<string>())
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── K. memberCounts replaces _summary sentinel (issue #82) ──────────────────
//
// In summaryOnly mode (the default), each file entry must:
//   * carry a top-level `memberCounts` object: kind → count
//   * NOT contain any synthetic `_summary` sentinel inside `entries`
// The fixture files contain one module + one record + one let per file, so the
// counts are deterministic.

[<Fact>]
let ``ProjectOutline summaryOnly emits memberCounts and drops _summary sentinel`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        summaryOnly = Some true }
                )

            Assert.Equal("ok", result["status"].GetValue<string>())

            let files = filesArray result
            Assert.True(files.Count >= 1, "Expected at least one file in outline")

            for file in files |> Seq.cast<JsonNode> do
                // memberCounts must be a non-null object.
                let counts = file["memberCounts"]
                Assert.NotNull(counts)
                let countsObj = counts :?> JsonObject

                // No kind in memberCounts may be the synthetic "_summary".
                Assert.False(
                    countsObj.ContainsKey("_summary"),
                    "memberCounts must not contain a '_summary' kind"
                )

                // Each value must be a positive integer.
                for kvp in countsObj do
                    let n = kvp.Value.GetValue<int>()
                    let kind = kvp.Key
                    Assert.True(n > 0, $"memberCounts {kind} must be > 0, got {n}")

                // entries must NOT contain the legacy _summary sentinel.
                let entries = file["entries"] :?> JsonArray

                let hasSentinel =
                    entries
                    |> Seq.cast<JsonNode>
                    |> Seq.exists (fun e ->
                        match e["kind"] with
                        | null -> false
                        | k -> k.GetValue<string>() = "_summary")

                Assert.False(hasSentinel, "_summary sentinel must not appear in entries")

                // The fixture has at least a "module" — assert that's tracked.
                let keys =
                    countsObj
                    |> Seq.map _.Key
                    |> String.concat ","

                Assert.True(
                    countsObj.ContainsKey("module"),
                    $"Expected 'module' kind in memberCounts; got keys: {keys}"
                )
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline summaryOnly=false still emits memberCounts`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        summaryOnly = Some false }
                )

            Assert.Equal("ok", result["status"].GetValue<string>())

            let files = filesArray result

            for file in files |> Seq.cast<JsonNode> do
                // memberCounts is always present so callers can rely on the shape.
                Assert.NotNull(file["memberCounts"])
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── K2. Exact memberCounts values for the deterministic fixture ─────────────
//
// Each fixture file declares: 1 module + 1 record + 1 let-bound function. The
// FCS outline also surfaces the record's field as kind="field" — so we assert
// the three kinds the fixture is built around (module, record, function_or_value)
// each have count=1. Field count is intentionally not asserted (it depends on
// whether FCS chose to emit the field as a separate symbol use, which is an
// FCS implementation detail outside this PR's contract).

[<Fact>]
let ``ProjectOutline summaryOnly memberCounts has module=1 record=1 function_or_value=1 per fixture file`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        summaryOnly = Some true }
                )

            Assert.Equal("ok", result["status"].GetValue<string>())

            let files = filesArray result
            Assert.Equal(2, files.Count)

            for file in files |> Seq.cast<JsonNode> do
                let countsObj = file["memberCounts"] :?> JsonObject
                let filePath = file["file"].GetValue<string>()

                let getCount (kind: string) =
                    match countsObj.[kind] with
                    | null -> 0
                    | n -> n.GetValue<int>()

                Assert.Equal(1, getCount "module")
                Assert.Equal(1, getCount "record")
                let fnCount = getCount "function_or_value"
                let msg = sprintf "Expected function_or_value=1 for %s, got %d" filePath fnCount
                Assert.True((fnCount = 1), msg)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── K3. Filter interaction — memberCounts uses post-filter entries ──────────
//
// With filter="Timer":
//   File1: only the Timer record matches by name/signature; its module Alpha
//          and getValue let do NOT match. So memberCounts must show record=1
//          and must NOT show function_or_value (counting only post-filter).
//   File2: nothing matches "Timer" — File2 may be absent from results, or
//          present with empty memberCounts; we don't pin that here. We only
//          assert the contract on File1.

[<Fact>]
let ``ProjectOutline summaryOnly memberCounts reflects post-filter entries`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        summaryOnly = Some true
                        filter = Some "Timer" }
                )

            Assert.Equal("ok", result["status"].GetValue<string>())

            let files = filesArray result

            // Find File1 (the one whose path ends with File1.fs).
            let file1 =
                files
                |> Seq.cast<JsonNode>
                |> Seq.tryFind (fun f -> f["file"].GetValue<string>().EndsWith("File1.fs"))

            Assert.True(file1.IsSome, "File1.fs must be present after filter=Timer")
            let file1 = file1.Value

            let countsObj = file1["memberCounts"] :?> JsonObject

            // Pre-filter, function_or_value=1 (getValue). Post-filter ("Timer"),
            // getValue must NOT contribute to memberCounts.
            let hasFnOrValue =
                countsObj.ContainsKey("function_or_value")
                && countsObj.["function_or_value"].GetValue<int>() > 0

            Assert.False(
                hasFnOrValue,
                "memberCounts.function_or_value must not be > 0 when filter='Timer' excludes getValue"
            )

            // The Timer record must still be counted.
            Assert.True(countsObj.ContainsKey("record"), "record kind must remain after filter=Timer")
            Assert.Equal(1, countsObj.["record"].GetValue<int>())
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── K4. maxResultsPerFile caps entries[] but NOT memberCounts ───────────────
//
// memberCounts is computed over the post-filter entries before truncation, so
// the agent sees the *real* breakdown of what the file contains even when
// entries[] is capped. Without this contract the map degrades into a noisy
// duplicate of `entries.length`, defeating its purpose: telling the agent
// "this file has 50 functions you didn't see in entries[]".

[<Fact>]
let ``ProjectOutline summaryOnly maxResultsPerFile=1 caps entries but memberCounts reflects the full file`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        summaryOnly = Some true
                        maxResultsPerFile = Some 1 }
                )

            Assert.Equal("ok", result["status"].GetValue<string>())

            let files = filesArray result
            Assert.True(files.Count >= 1, "Expected at least one file")

            for file in files |> Seq.cast<JsonNode> do
                // entries (summary view) is truncated per the cap.
                let entries = file["entries"] :?> JsonArray
                Assert.True(
                    entries.Count <= 1,
                    $"entries.Count must be <= 1 with maxResultsPerFile=1; got {entries.Count}"
                )

                // memberCounts must be non-null and reflect the *full* per-file
                // outline, ignoring maxResultsPerFile.
                let counts = file["memberCounts"] :?> JsonObject
                Assert.NotNull(counts)

                // Each fixture file has 1 module + 1 record + 1 let binding,
                // and memberCounts must capture all three regardless of the
                // entries cap.
                Assert.True(counts.ContainsKey("module"), "module kind must be counted")
                Assert.Equal(1, counts.["module"].GetValue<int>())

                Assert.True(
                    counts.ContainsKey("record"),
                    "record kind must be counted even when truncated out of entries[]"
                )
                Assert.Equal(1, counts.["record"].GetValue<int>())

                Assert.True(
                    counts.ContainsKey("function_or_value"),
                    "function_or_value must be counted even when truncated out of entries[]"
                )
                Assert.Equal(1, counts.["function_or_value"].GetValue<int>())
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── L. Reject pathological pagination args (regression test for review feedback)
//
// maxFiles=0 would return empty pages with truncated=true and a nextCursor whose
// offset never advances — a non-terminating loop for cursor-following clients.
// ProjectOutline must reject this up front rather than emit a malformed envelope.

[<Fact>]
let ``ProjectOutline rejects maxFiles=0 with InvalidArgException`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! ex =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    bridge.ProjectOutline({ defaultArgs projectPath with maxFiles = Some 0 })
                    :> System.Threading.Tasks.Task)

            Assert.Contains("maxFiles", ex.Message)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

[<Fact>]
let ``ProjectOutline rejects negative maxResultsPerFile`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! ex =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    bridge.ProjectOutline(
                        { defaultArgs projectPath with
                            maxFiles = Some 10
                            maxResultsPerFile = Some -1 }
                    )
                    :> System.Threading.Tasks.Task)

            Assert.Contains("maxResultsPerFile", ex.Message)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }
