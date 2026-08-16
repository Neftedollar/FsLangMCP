module FsLangMcp.Tests.PublicApiCharBudgetTests

/// End-to-end tests for fcs_public_api's character-budget paging + in-band
/// narrowing hint (issue #206).
///
/// Coverage:
///   * A synthetic API surface big enough to trip the ~60k-char response budget:
///     the page closes under budget (truncatedByBudget=true) even though
///     maxResults left plenty of count-based room; hint names real namespaces
///     from the remainder; cursor pagination still reconstructs the whole
///     surface with no overlap between pages.
///   * Count-close (pre-#206 behavior): a page that closes because maxResults
///     was reached (not the budget) gets the new hint but NOT truncatedByBudget.
///   * Small-surface regression guard: a surface that fits on one page emits
///     neither hint nor truncatedByBudget — the pre-#206 response shape is
///     byte-for-byte unchanged.

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge
open FsLangMcp.Cursor
open FsLangMcp.Tools

// ─── Fixtures ──────────────────────────────────────────────────────────────────

/// A single module with a couple of small public functions — deliberately far
/// under the char budget, so a default (unfiltered, unpaginated) call returns
/// everything on one page.
let private smallSurfaceFs =
    String.concat
        "\n"
        [ "module Small.Surface"
          ""
          "module GroupA ="
          "    let add (a: int) (b: int) : int = a + b"
          ""
          "module GroupB ="
          "    let sub (a: int) (b: int) : int = a - b"
          ""
          "module GroupC ="
          "    let mul (a: int) (b: int) : int = a * b"
          "" ]

/// Three namespaces (Wide.Alpha / Wide.Beta / Wide.Gamma), `perGroup` modules
/// each, every module carrying two functions with a six-parameter signature.
/// Sized (see the module doc comment) so the first page's cumulative
/// serialized size crosses the ~60k-char responseCharBudget (FcsBridge.fs)
/// well before `perGroup * 3` entities or the requested maxResults are reached.
let private wideSurfaceFs (perGroup: int) =
    let groups = [ "Alpha"; "Beta"; "Gamma" ]

    let moduleLines (i: int) =
        let n = i.ToString("D4")

        [ $"    module M{n} ="
          $"        let compute (alpha: string) (beta: string) (gamma: string) (delta: string) (epsilon: string) (zeta: string) : string ="
          "            alpha + beta + gamma + delta + epsilon + zeta"
          $"        let transform (alpha: int) (beta: int) (gamma: int) (delta: int) (epsilon: int) (zeta: int) : int ="
          "            alpha + beta + gamma + delta + epsilon + zeta" ]

    let groupLines (g: string) =
        [ $"namespace Wide.{g}"
          "" ]
        @ (List.init perGroup moduleLines |> List.concat)
        @ [ "" ]

    groups |> List.collect groupLines |> String.concat "\n"

let private writeFixture (dirTag: string) (source: string) : string * string =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_publicapi_budget_{dirTag}_{runId}")
    Directory.CreateDirectory(root) |> ignore

    let sourcePath = Path.Combine(root, "Surface.fs")
    File.WriteAllText(sourcePath, source)

    let projectPath = Path.Combine(root, "Surface.fsproj")

    File.WriteAllText(
        projectPath,
        String.concat
            Environment.NewLine
            [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
              "  <PropertyGroup>"
              "    <TargetFramework>net10.0</TargetFramework>"
              "  </PropertyGroup>"
              "  <ItemGroup>"
              "    <Compile Include=\"Surface.fs\" />"
              "  </ItemGroup>"
              "</Project>" ]
    )

    projectPath, root

// ─── Helpers ─────────────────────────────────────────────────────────────────────

let private baseArgs (project: string) : FcsPublicApiArgs =
    { projectPath = Some project
      includeInternal = None
      namespaceFilter = None
      maxResults = None
      cursor = None }

let private entities (result: JsonNode) : JsonNode list =
    match result["entities"] with
    | :? JsonArray as arr -> arr |> Seq.cast<JsonNode> |> Seq.toList
    | _ -> []

let private entityFullNames (result: JsonNode) : string list =
    entities result |> List.map (fun e -> e["fullName"].GetValue<string>())

let private optBool (node: JsonNode) (key: string) : bool option =
    match node[key] with
    | null -> None
    | v -> Some(v.GetValue<bool>())

let private optString (node: JsonNode) (key: string) : string option =
    match node[key] with
    | null -> None
    | v -> Some(v.GetValue<string>())

// ─────────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``budget close: page closes under the char budget, hint names real namespaces, cursor reconstructs the whole surface`` () : Task =
    task {
        // 90 modules/group * 3 groups = 270 entities; each ~600-900 serialized
        // chars comfortably crosses the 60k budget well before maxResults=1000
        // (the hard ceiling) or the full 270-entity count would.
        let project, root = writeFixture "wide" (wideSurfaceFs 90)
        let bridge = FcsBridge()

        try
            let! page1 = bridge.PublicApi({ baseArgs project with maxResults = Some 1000 })

            Assert.Equal("ok", page1["status"].GetValue<string>())

            let total = page1["entityCount"].GetValue<int>()
            Assert.Equal(270, total)

            // The budget closed this page well short of the 1000-entity/270-total
            // ceiling — a pure count cap would never have cut here.
            let returned = (entities page1).Length
            Assert.True(returned > 0 && returned < total, $"expected a partial page, got {returned}/{total}")

            Assert.Equal(Some true, optBool page1 "truncated")
            Assert.Equal(Some true, optBool page1 "truncatedByBudget")

            match optString page1 "hint" with
            | None -> Assert.Fail("expected a hint field on a budget-closed page")
            | Some hint ->
                Assert.Contains("namespaceFilter", hint)
                // Every namespace this hint could truthfully cite is one of the
                // three synthetic groups — assert it actually names one of them
                // (not a placeholder), without pinning the exact cutoff entity.
                let namesRealGroup =
                    [ "Wide.Alpha"; "Wide.Beta"; "Wide.Gamma" ]
                    |> List.exists hint.Contains

                Assert.True(namesRealGroup, $"hint must name a real Wide.* namespace, got: {hint}")

            // #206 review Min-1: the invariant this whole issue exists for is that the
            // SHIPPED response fits the ~72k-char MCP ceiling — not that the internal
            // accumulator believes it closed at "60k". Assert the actual rendered bytes
            // (the same `renderToken` every tool response goes out through), not a proxy.
            let rendered = renderToken page1
            Assert.True(
                rendered.Length <= 72_000,
                $"budget-closed page rendered to {rendered.Length} chars, over the ~72k MCP ceiling"
            )

            let cursorNode = page1["nextCursor"]
            Assert.NotNull(cursorNode)

            // Walk every remaining page via nextCursor; pages must be disjoint and
            // their union must reconstruct the full, unpaginated surface exactly.
            // Bounded so a cursor-progress regression fails fast instead of hanging
            // the suite (#206 review Min-4) — 270 entities can never need this many
            // pages even at the smallest plausible page size.
            let maxPages = 500
            let mutable acc = entityFullNames page1 |> Set.ofList
            let mutable nextCursor = Some(cursorNode.GetValue<string>())
            let mutable pages = 1

            while nextCursor.IsSome do
                Assert.True(pages < maxPages, $"cursor walk did not terminate within {maxPages} pages")

                let! page = bridge.PublicApi({ baseArgs project with maxResults = Some 1000; cursor = nextCursor })
                pages <- pages + 1
                let pageNames = entityFullNames page |> Set.ofList
                Assert.Empty(Set.intersect acc pageNames)
                acc <- Set.union acc pageNames

                nextCursor <-
                    match page["nextCursor"] with
                    | null -> None
                    | n -> Some(n.GetValue<string>())

            Assert.True(pages > 1, "expected the budget to force more than one page")
            Assert.Equal(total, acc.Count)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``count close: a page that closes at maxResults gets the hint but not truncatedByBudget`` () : Task =
    task {
        let project, root = writeFixture "count" smallSurfaceFs
        let bridge = FcsBridge()

        try
            // 3 tiny modules; maxResults=1 forces a count-close with 2 remaining —
            // far too small to ever cross the char budget.
            let! page1 = bridge.PublicApi({ baseArgs project with maxResults = Some 1 })

            Assert.Equal("ok", page1["status"].GetValue<string>())
            Assert.Equal(Some true, optBool page1 "truncated")
            Assert.Equal(None, optBool page1 "truncatedByBudget")

            match optString page1 "hint" with
            | None -> Assert.Fail("expected a hint field on a count-closed page with more entities remaining")
            | Some hint -> Assert.Contains("namespaceFilter", hint)
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }

[<Fact>]
let ``small surface regression guard: a page that returns everything emits neither hint nor truncatedByBudget`` () : Task =
    task {
        let project, root = writeFixture "small" smallSurfaceFs
        let bridge = FcsBridge()

        try
            let! result = bridge.PublicApi(baseArgs project)

            Assert.Equal("ok", result["status"].GetValue<string>())
            Assert.Equal(Some false, optBool result "truncated")
            Assert.Equal(None, optBool result "truncatedByBudget")
            Assert.Equal(None, optString result "hint")
        finally
            if Directory.Exists root then
                Directory.Delete(root, true)
    }
