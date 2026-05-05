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

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge
open FsLangMcp.Cursor

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

let private defaultArgs projectPath : FcsProjectOutlineArgs =
    { projectPath = projectPath
      workspacePath = None
      includePrivate = None
      includeTests = None
      includeGeneratedFiles = None
      maxFiles = None
      maxResultsPerFile = None
      summaryOnly = Some false   // full detail so we can inspect entries
      cursor = None
      filter = None
      nameContains = None }

let private filesArray (result: JsonNode) =
    result["files"] :?> JsonArray

// ─── F. No filter — all files included ────────────────────────────────────────

[<Fact>]
let ``ProjectOutline without filter returns all project files`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result = bridge.ProjectOutline({ defaultArgs projectPath with maxFiles = Some 100 })

            Assert.Equal("ok", result["status"].GetValue<string>())

            let files = filesArray result
            // We have 2 .fs files; both should be included.
            Assert.Equal(2, files.Count)
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

// ─── H. Evil pattern — must complete in << 1 s (DoS regression guard) ────────

[<Fact>]
let ``ProjectOutline with catastrophic-backtracking pattern completes in under 1 second`` () : System.Threading.Tasks.Task =
    task {
        let projectPath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            // Warm up FCS: parse the project once without a filter so that
            // projectResultsCache is populated.  FCS cold-start (JIT + project
            // compilation) easily takes 1–2 s and must not be charged to the
            // regex-guard measurement.
            let! _ = bridge.ProjectOutline({ defaultArgs projectPath with maxFiles = Some 100 })

            // (a+)+$ is the canonical catastrophic-backtracking pattern.
            // Against a long-ish string without NonBacktracking this would hang.
            // The FCS cache is warm, so only regex work is timed here.
            let sw = System.Diagnostics.Stopwatch.StartNew()

            let! result =
                bridge.ProjectOutline(
                    { defaultArgs projectPath with
                        maxFiles = Some 100
                        filter = Some "(a+)+$" }
                )

            sw.Stop()

            Assert.Equal("ok", result["status"].GetValue<string>())
            // NonBacktracking makes the regex portion instant; assert well under 1 s.
            Assert.True(
                sw.Elapsed.TotalSeconds < 1.0,
                $"Pattern (a+)+$ took {sw.Elapsed.TotalMilliseconds:F0}ms — NonBacktracking not applied?"
            )
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
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
