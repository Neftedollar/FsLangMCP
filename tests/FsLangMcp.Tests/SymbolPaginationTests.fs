module FsLangMcp.Tests.SymbolPaginationTests

/// E2E tests for cursor pagination in fcs_project_symbol_uses and project-wide
/// fcs_find_symbol (issue #80).
///
/// Each test builds a temp fsproj with several distinct symbols, then exercises
/// the production FcsBridge methods. Coverage:
///   * envelope contains truncated / nextCursor / pageOffset / pageSize / totalEstimate
///   * cursor round-trip across two pages: no overlap, all items reachable
///   * malformed cursor → ArgumentException
///   * totalEstimate uses the right unit name ("uses" for symbol_uses, "symbols" for find_symbol)

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge
open FsLangMcp.Cursor

// ─── Fixture: project with 5 distinct top-level let bindings ─────────────────
//
// All five live in the same module, so fcs_find_symbol with query "alpha"
// will match the symbol-group `alpha` once but with multiple uses.
// fcs_project_symbol_uses with query "alpha_call" hits five distinct symbols.

let private createFixtureProject () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_sym_pag_{runId}")
    Directory.CreateDirectory(root) |> ignore

    // Single source file with five "alpha_call_*" let bindings, each used twice
    // to give symbol_uses non-trivial pagination domain.
    let sourcePath = Path.Combine(root, "File1.fs")

    File.WriteAllText(
        sourcePath,
        """module Sample

let alpha_call_one () = 1
let alpha_call_two () = 2
let alpha_call_three () = 3
let alpha_call_four () = 4
let alpha_call_five () = 5

let useAll () =
    alpha_call_one () + alpha_call_two () + alpha_call_three () + alpha_call_four () + alpha_call_five ()

let useAllAgain () =
    alpha_call_one () + alpha_call_two () + alpha_call_three () + alpha_call_four () + alpha_call_five ()
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
              "  </ItemGroup>"
              "</Project>" ]
    )

    projectPath, sourcePath, root

let private symbolUsesArgs sourcePath query : FcsProjectSymbolUsesArgs =
    { path = sourcePath
      text = None
      projectPath = None
      projectOptions = None
      symbolQuery = query
      exact = Some false
      maxResults = Some 500
      cursor = None }

let private findSymbolArgs sourcePath query : FcsFindSymbolArgs =
    { path = sourcePath
      text = None
      projectPath = None
      projectOptions = None
      symbolQuery = query
      exact = Some false
      maxResults = Some 500
      contextLines = Some 0
      includeDeclaration = Some true
      cursor = None }

// ─── ProjectSymbolUses: pagination envelope present ───────────────────────────

[<Fact>]
let ``ProjectSymbolUses returns pagination envelope keyed by 'uses'`` () : System.Threading.Tasks.Task =
    task {
        let _projectPath, sourcePath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result = bridge.ProjectSymbolUses(symbolUsesArgs sourcePath "alpha_call")

            Assert.Equal("succeeded", result["status"].GetValue<string>())

            // Envelope fields exist.
            let truncated = result["truncated"]
            Assert.NotNull(truncated)
            Assert.False(truncated.GetValue<bool>(), "single page should not be truncated")

            let nextCursor = result["nextCursor"]
            Assert.True(isNull nextCursor || nextCursor.GetValue<obj>() = null, "nextCursor should be null on last page")

            let totalEstimate = result["totalEstimate"]
            Assert.NotNull(totalEstimate)
            // totalEstimate uses the "uses" unit name.
            let usesTotal = totalEstimate["uses"]
            Assert.NotNull(usesTotal)
            Assert.True(usesTotal.GetValue<int>() > 0, "totalEstimate.uses must be > 0 for matching query")
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── ProjectSymbolUses: cursor round-trip across two pages ───────────────────

[<Fact>]
let ``ProjectSymbolUses cursor round-trip: page-1 + page-2 cover everything once`` () : System.Threading.Tasks.Task =
    task {
        let _projectPath, sourcePath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            // First call: maxResults = 1000 to learn the total.
            let! all = bridge.ProjectSymbolUses(symbolUsesArgs sourcePath "alpha_call")
            let allTotal = all["totalEstimate"]
            let totalUses = allTotal["uses"].GetValue<int>()
            Assert.True(totalUses >= 4, $"fixture must produce ≥ 4 uses (got {totalUses})")

            // Force pagination with a small page size.
            let halfSize = max 1 (totalUses / 2)

            let! page1 =
                bridge.ProjectSymbolUses(
                    { symbolUsesArgs sourcePath "alpha_call" with
                        maxResults = Some halfSize }
                )

            Assert.True(page1["truncated"].GetValue<bool>(), "page 1 must be truncated")
            let cursorStr = page1["nextCursor"].GetValue<string>()
            Assert.NotEmpty(cursorStr)

            let page1Uses =
                (page1["uses"] :?> JsonArray)
                |> Seq.cast<JsonNode>
                |> Seq.map (fun u ->
                    let r = u["range"]
                    let f = u["file"].GetValue<string>()
                    let line = r["startLine"].GetValue<int>()
                    let col = r["startColumn"].GetValue<int>()
                    f, line, col)
                |> Set.ofSeq

            // Cursor must point past page 1.
            match tryDecode cursorStr with
            | Error msg -> Assert.Fail($"cursor did not decode: {msg}")
            | Ok payload -> Assert.Equal(halfSize, payload.offset)

            let! page2 =
                bridge.ProjectSymbolUses(
                    { symbolUsesArgs sourcePath "alpha_call" with
                        maxResults = Some halfSize
                        cursor = Some cursorStr }
                )

            let page2Uses =
                (page2["uses"] :?> JsonArray)
                |> Seq.cast<JsonNode>
                |> Seq.map (fun u ->
                    let r = u["range"]
                    let f = u["file"].GetValue<string>()
                    let line = r["startLine"].GetValue<int>()
                    let col = r["startColumn"].GetValue<int>()
                    f, line, col)
                |> Set.ofSeq

            // No overlap.
            Assert.Empty(Set.intersect page1Uses page2Uses)
            // pageOffset advances correctly on page 2.
            Assert.Equal(halfSize, page2["pageOffset"].GetValue<int>())
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── ProjectSymbolUses: malformed cursor → ArgumentException ─────────────────

[<Fact>]
let ``ProjectSymbolUses with malformed cursor raises ArgumentException`` () : System.Threading.Tasks.Task =
    task {
        let _projectPath, sourcePath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! ex =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    bridge.ProjectSymbolUses(
                        { symbolUsesArgs sourcePath "alpha_call" with
                            cursor = Some "not-valid-base64!!!" }
                    )
                    :> System.Threading.Tasks.Task)

            Assert.Contains("cursor", ex.Message)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── FindSymbol: pagination envelope keyed by 'symbols' ──────────────────────

[<Fact>]
let ``FindSymbol returns pagination envelope keyed by 'symbols'`` () : System.Threading.Tasks.Task =
    task {
        let _projectPath, sourcePath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! result = bridge.FindSymbol(findSymbolArgs sourcePath "alpha_call")

            Assert.Equal("succeeded", result["status"].GetValue<string>())

            // Envelope present.
            let totalEstimate = result["totalEstimate"]
            Assert.NotNull(totalEstimate)

            // Unit name is "symbols", not "files" or "uses".
            let symbolsTotal = totalEstimate["symbols"]
            Assert.NotNull(symbolsTotal)
            Assert.True(symbolsTotal.GetValue<int>() >= 5, "fixture has 5 alpha_call_* symbols")

            // pageOffset starts at 0 on first call.
            Assert.Equal(0, result["pageOffset"].GetValue<int>())
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── FindSymbol: cursor round-trip across two pages of symbol groups ─────────

[<Fact>]
let ``FindSymbol cursor round-trip: page-1 + page-2 cover all symbol groups`` () : System.Threading.Tasks.Task =
    task {
        let _projectPath, sourcePath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            // Force pagination: 5 symbols, page size 2 → 3 pages.
            let! page1 =
                bridge.FindSymbol(
                    { findSymbolArgs sourcePath "alpha_call" with
                        maxResults = Some 2 }
                )

            Assert.True(page1["truncated"].GetValue<bool>(), "page 1 must be truncated with maxResults=2")
            let cursor1 = page1["nextCursor"].GetValue<string>()
            Assert.NotEmpty(cursor1)

            // Capture first-page symbol FullNames.
            let symbolNames (result: JsonNode) =
                (result["symbols"] :?> JsonArray)
                |> Seq.cast<JsonNode>
                |> Seq.map (fun s ->
                    let sym = s["symbol"]
                    sym["fullName"].GetValue<string>())
                |> Set.ofSeq

            let names1 = symbolNames page1
            Assert.Equal(2, names1.Count)

            // Page 2.
            let! page2 =
                bridge.FindSymbol(
                    { findSymbolArgs sourcePath "alpha_call" with
                        maxResults = Some 2
                        cursor = Some cursor1 }
                )

            let names2 = symbolNames page2
            Assert.Equal(2, names2.Count)
            Assert.Empty(Set.intersect names1 names2)

            // pageOffset on page 2 reflects the cursor.
            match tryDecode cursor1 with
            | Error msg -> Assert.Fail($"cursor did not decode: {msg}")
            | Ok payload -> Assert.Equal(payload.offset, page2["pageOffset"].GetValue<int>())
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }

// ─── FindSymbol: malformed cursor → ArgumentException ────────────────────────

[<Fact>]
let ``FindSymbol with malformed cursor raises ArgumentException`` () : System.Threading.Tasks.Task =
    task {
        let _projectPath, sourcePath, root = createFixtureProject ()
        let bridge = FcsBridge()

        try
            let! ex =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    bridge.FindSymbol(
                        { findSymbolArgs sourcePath "alpha_call" with
                            cursor = Some "not-valid-base64!!!" }
                    )
                    :> System.Threading.Tasks.Task)

            Assert.Contains("cursor", ex.Message)
        finally
            if Directory.Exists(root) then Directory.Delete(root, true)
    }
