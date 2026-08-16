module FsLangMcp.Tests.FileOutlineBudgetTests

/// End-to-end tests for fcs_file_outline's response-char-budget size guard
/// (issue #206).
///
/// Coverage:
///   * A file whose requested summaryOnly=false output would exceed the shared
///     response budget downgrades to header-only entries (downgradedToSummary=true)
///     plus an explanatory hint, instead of ever emitting the over-budget payload.
///   * A small file with summaryOnly=false is unchanged: full per-member
///     entries, no downgradedToSummary, no hint.

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge
open FsLangMcp.Tools

// ─── Fixtures ──────────────────────────────────────────────────────────────────

/// `count` top-level functions, each with a six-parameter signature. At the
/// default maxResults=200 this is rich enough (name + fullName + kind +
/// accessibility + range + declarationRange + signature per entry) that 200
/// of them cross the shared responseCharBudget (FcsBridge.fs, `Types.fs`'s
/// `renderedLength`).
let private bigOutlineSource (count: int) =
    let lines = ResizeArray<string>()
    lines.Add("module Big.Outline")
    lines.Add("")

    for i in 0 .. count - 1 do
        let n = i.ToString("D4")
        lines.Add($"let compute{n} (alpha: string) (beta: string) (gamma: string) (delta: string) (epsilon: string) (zeta: string) : string =")
        lines.Add("    alpha + beta + gamma + delta + epsilon + zeta")
        lines.Add("")

    String.concat "\n" lines

let private smallOutlineSource =
    String.concat
        "\n"
        [ "module Small.Outline"
          ""
          "let addOne (x: int) : int = x + 1"
          ""
          "let addTwo (x: int) : int = x + 2"
          "" ]

/// Write `source` as the single compiled file of a fresh temp fsproj. Returns
/// (sourcePath, projectPath, tempRoot) — caller deletes tempRoot when done.
let private writeFixture (dirTag: string) (source: string) : string * string * string =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_outline_budget_{dirTag}_{runId}")
    Directory.CreateDirectory(root) |> ignore

    let sourcePath = Path.Combine(root, "Outline.fs")
    File.WriteAllText(sourcePath, source)

    let projectPath = Path.Combine(root, "Outline.fsproj")

    File.WriteAllText(
        projectPath,
        String.concat
            Environment.NewLine
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup>"
              "    <TargetFramework>net10.0</TargetFramework>"
              "  </PropertyGroup>"
              "  <ItemGroup>"
              "    <Compile Include=\"Outline.fs\" />"
              "  </ItemGroup>"
              "</Project>" ]
    )

    sourcePath, projectPath, root

// ─── Helpers ─────────────────────────────────────────────────────────────────────

let private optBool (node: JsonNode) (key: string) : bool option =
    match node[key] with
    | null -> None
    | v -> Some(v.GetValue<bool>())

let private optString (node: JsonNode) (key: string) : string option =
    match node[key] with
    | null -> None
    | v -> Some(v.GetValue<string>())

let private baseArgs (path: string) (project: string) : FcsFileOutlineArgs =
    { path = path
      text = None
      projectPath = Some project
      projectOptions = None
      includePrivate = None
      includeLocal = None
      summaryOnly = Some false
      maxResults = None }

// ─────────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``over-budget file downgrades summaryOnly=false to headers plus a hint`` () : Task =
    task {
        let sourcePath, projectPath, root = writeFixture "big" (bigOutlineSource 220)
        let bridge = FcsBridge()

        try
            let! result = bridge.FileOutline(baseArgs sourcePath projectPath)

            Assert.Equal("succeeded", result["status"].GetValue<string>())
            Assert.Equal(Some false, optBool result "summaryOnly")
            Assert.Equal(Some true, optBool result "downgradedToSummary")

            match optString result "hint" with
            | None -> Assert.Fail("expected a hint field when the outline is downgraded to summary")
            | Some hint ->
                Assert.Contains("budget", hint)
                Assert.Contains("maxResults", hint)

            // Downgraded entries are header-shaped: no per-member `signature`. The
            // fixture is 220 free `let` functions inside one module, so `headersOf`'s
            // containerKinds filter leaves exactly the module header — Assert.NotEmpty
            // makes sure that one entry is really there (#206 review Min-2: a loop with
            // no NotEmpty check passes vacuously on an empty array).
            match result["entries"] with
            | :? JsonArray as entries ->
                Assert.NotEmpty(entries)

                for entry in entries |> Seq.cast<JsonNode> do
                    Assert.Null(entry["signature"])
            | _ -> Assert.Fail("expected entries to be a JsonArray")

            // count is the post-truncation length (maxResults=200 caps `entries`, hence
            // `count`, to 200 even though the fixture declares 221 definitions); only
            // memberCounts describes the full, untruncated definition set (#206 review
            // Imp-2 — the original comment here claimed count was also uncapped, which
            // contradicted the very next assertion).
            Assert.Equal(200, result["count"].GetValue<int>())
            Assert.NotNull(result["memberCounts"])

            // #206 review Min-1: assert the actual SHIPPED (indented) response fits the
            // ~72k-char MCP ceiling, not just that the internal accumulator believes it
            // closed under responseCharBudget — this is the assertion that would have
            // caught round-1's Imp-1 (wrong unit) and round-2's depth residual alike.
            let rendered = renderToken result
            Assert.True(
                rendered.Length <= 72_000,
                $"downgraded outline rendered to {rendered.Length} chars, over the ~72k MCP ceiling"
            )
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``lowering maxResults after a downgrade resolves it: the response returns to full entries once small enough`` () : Task =
    task {
        // #206 review H5: the implementer flagged this round-trip as an untested but
        // traced-sound gap. `entries` is truncated to maxResults BEFORE the budget
        // measurement (FcsBridge.fs), so the measured payload is monotonic in
        // maxResults — halving it roughly halves the measured size, and `overBudget`
        // must eventually flip back to false. Pin that mechanism with a real call.
        let sourcePath, projectPath, root = writeFixture "shrink" (bigOutlineSource 220)
        let bridge = FcsBridge()

        try
            let! full = bridge.FileOutline(baseArgs sourcePath projectPath)
            Assert.Equal(Some true, optBool full "downgradedToSummary")

            let! shrunk =
                bridge.FileOutline({ baseArgs sourcePath projectPath with maxResults = Some 10 })

            Assert.Equal("succeeded", shrunk["status"].GetValue<string>())
            Assert.Equal(None, optBool shrunk "downgradedToSummary")
            Assert.Equal(None, optString shrunk "hint")
            Assert.Equal(10, shrunk["count"].GetValue<int>())

            // Full per-member entries are back — every returned entry carries a signature.
            match shrunk["entries"] with
            | :? JsonArray as entries ->
                Assert.Equal(10, entries.Count)

                for entry in entries |> Seq.cast<JsonNode> do
                    Assert.NotNull(entry["signature"])
            | _ -> Assert.Fail("expected entries to be a JsonArray")
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``small file with summaryOnly=false is unchanged: full entries, no downgrade fields`` () : Task =
    task {
        let sourcePath, projectPath, root = writeFixture "small" smallOutlineSource
        let bridge = FcsBridge()

        try
            let! result = bridge.FileOutline(baseArgs sourcePath projectPath)

            Assert.Equal("succeeded", result["status"].GetValue<string>())
            Assert.Equal(Some false, optBool result "summaryOnly")
            Assert.Equal(None, optBool result "downgradedToSummary")
            Assert.Equal(None, optString result "hint")

            match result["entries"] with
            | :? JsonArray as entries ->
                Assert.True(entries.Count >= 2, $"expected >= 2 entries, got {entries.Count}")

                for entry in entries |> Seq.cast<JsonNode> do
                    Assert.NotNull(entry["signature"])
            | _ -> Assert.Fail("expected entries to be a JsonArray")
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }
