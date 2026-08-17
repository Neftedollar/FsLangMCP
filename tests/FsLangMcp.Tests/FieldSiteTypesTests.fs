module FsLangMcp.Tests.FieldSiteTypesTests

// ─── #207 review finding 1: the DEGRADED path, unit-tested ──────────────────────
//
// Every naturally-occurring record-field site in every fixture resolves — including the
// parse-error fixture, and a 3434-site sweep of this repo (degraded = 0 in both). FCS
// either resolves the field type (recovering an undefined one to `obj`) or omits the
// symbol use entirely; review independently re-tested two more candidate degradations
// (a file excluded from compile order, a field typed from an unreferenced project) and
// both collapsed to ABSENCE rather than degradation.
//
// So the degraded arms had no test coverage at all: a dropped key, the STRING "null"
// instead of JSON null, or a wrong status string silently breaking the
// `typed + degraded == fieldSites` identity would each have passed the entire suite,
// because the ledger assertions only bind when degraded > 0.
//
// FcsBridge.FieldSiteTypes exists so those arms are free functions over primitives.
// These tests exercise every one of them directly. The end-to-end half — the deadline
// arm driven through a real Find sweep — lives in FindTests via
// findSiteTypeDeadlineExpiredOverride.

open System.Text.Json.Nodes
open Xunit
open FsLangMcp.FcsBridge

// ── FieldSiteTypes.outcome — the (siteType, typeStatus) decision ────────────────

[<Fact>]
let ``outcome types a site when the formatter produces a type`` () =
    let siteType, status = FieldSiteTypes.outcome false (fun () -> Some "int")

    Assert.Equal("int", siteType)
    Assert.Equal(FieldSiteTypes.Typed, status)
    Assert.Equal("typed", status)

[<Fact>]
let ``outcome degrades to unresolved with a NULL siteType when the formatter yields nothing`` () =
    let siteType, status = FieldSiteTypes.outcome false (fun () -> None)

    // `null`, not "", not "unknown" — the row must render an explicit JSON null.
    Assert.Null(siteType)
    Assert.Equal(FieldSiteTypes.Unresolved, status)
    Assert.Equal("unresolved", status)

[<Fact>]
let ``outcome degrades to timeout past the deadline WITHOUT invoking the formatter`` () =
    // The thunk is the whole point: past the deadline find must stop paying FCS
    // formatting cost on the tail of a large sweep, not merely discard the result.
    let mutable formatterCalls = 0

    let siteType, status =
        FieldSiteTypes.outcome true (fun () ->
            formatterCalls <- formatterCalls + 1
            Some "int")

    Assert.Null(siteType)
    Assert.Equal(FieldSiteTypes.TimedOut, status)
    Assert.Equal("timeout", status)
    Assert.Equal(0, formatterCalls)

[<Fact>]
let ``outcome's three statuses are distinct — the ledger identity depends on it`` () =
    // countTypeStatus buckets sites by these exact strings; two of them collapsing into
    // one would silently break `typed + degraded == fieldSites`.
    let statuses =
        [ FieldSiteTypes.Typed; FieldSiteTypes.Unresolved; FieldSiteTypes.TimedOut ]

    Assert.Equal(3, statuses |> List.distinct |> List.length)

// ── FieldSiteTypes.rowFields — the JSON projection ──────────────────────────────

let private siteTypeNode (fields: (string * JsonNode) list) =
    fields |> List.tryFind (fst >> (=) "siteType") |> Option.map snd

[<Fact>]
let ``rowFields emits nothing when includeSiteTypes is off`` () =
    Assert.Empty(FieldSiteTypes.rowFields false "int" FieldSiteTypes.Typed)

[<Fact>]
let ``rowFields emits nothing for a non-field row even when includeSiteTypes is on`` () =
    // typeStatus = null is how a definition / reference / member-usage row is marked;
    // those must stay byte-identical to a pre-#207 response.
    Assert.Empty(FieldSiteTypes.rowFields true null null)

[<Fact>]
let ``rowFields emits the type as a JSON string on a typed row`` () =
    let fields = FieldSiteTypes.rowFields true "int" FieldSiteTypes.Typed

    Assert.Equal(1, List.length fields)

    match siteTypeNode fields with
    | None -> Assert.Fail "a typed row must carry a siteType key"
    | Some node ->
        Assert.False(isNull node, "a typed row's siteType must not be JSON null")
        Assert.Equal("int", node.GetValue<string>())

[<Theory>]
[<InlineData("unresolved")>]
[<InlineData("timeout")>]
let ``rowFields KEEPS the key and emits JSON null — not a missing key, not the string "null"`` (status: string) =
    let fields = FieldSiteTypes.rowFields true null status

    // 1. The key is present, so a consumer reads an explicit null instead of having to
    //    distinguish "could not resolve" from "this build of the tool has no such field".
    Assert.Equal(1, List.length fields)
    Assert.Equal("siteType", fst fields[0])

    // 2. The value is JSON null. A JsonNode holding the STRING "null" is also non-missing
    //    and would satisfy a naive presence check — assert the node itself is null.
    match siteTypeNode fields with
    | None -> Assert.Fail "a degraded row must still carry the siteType key"
    | Some node -> Assert.True(isNull node, "a degraded row's siteType must be JSON null")

    // 3. Serialized shape, which is what an agent actually parses.
    let serialized = (FsLangMcp.Types.jobj fields).ToJsonString()
    Assert.Equal("""{"siteType":null}""", serialized)
    Assert.DoesNotContain("\"null\"", serialized)

[<Fact>]
let ``rowFields round-trips every outcome the resolver can produce`` () =
    // Ties the two halves together: whatever `outcome` decides, `rowFields` must render a
    // key for it, because both are driven by the same typeStatus vocabulary.
    let cases =
        [ FieldSiteTypes.outcome false (fun () -> Some "string")
          FieldSiteTypes.outcome false (fun () -> None)
          FieldSiteTypes.outcome true (fun () -> Some "string") ]

    for siteType, status in cases do
        let fields = FieldSiteTypes.rowFields true siteType status
        Assert.Equal(1, List.length fields)

        match siteTypeNode fields with
        | None -> Assert.Fail $"outcome status '{status}' produced no siteType key"
        | Some node -> Assert.Equal(isNull siteType, isNull node)

// ── #207 review: one physical site, several projects ────────────────────────────
//
// find de-duplicates sites by physical location, so a `.fs` linked into more than one
// `.fsproj` is swept once per project. Those projects can resolve the same field to
// different types (different conditional symbols, a different generic instantiation).
// The row used to be overwritten by whichever project the sweep visited last, and the
// payload then claimed one type with nothing saying another had been seen.

[<Fact>]
let ``mergeAlternatives keeps a second project's different type, attributed to that project`` () =
    Assert.Equal<(string * string) list>(
        [ ("string", "ProjB") ],
        FieldSiteTypes.mergeAlternatives "int" "string" "ProjB" []
    )

[<Fact>]
let ``mergeAlternatives ignores an identical answer, a degraded answer, and duplicates`` () =
    // Same type from another project is agreement, not a conflict.
    Assert.Empty(FieldSiteTypes.mergeAlternatives "int" "int" "ProjB" [])
    // A project that could not type the site says nothing about the type that IS known.
    Assert.Empty(FieldSiteTypes.mergeAlternatives "int" null "ProjB" [])
    // Nor does the very same (type, project) pair arriving twice.
    Assert.Equal<(string * string) list>(
        [ ("string", "ProjB") ],
        FieldSiteTypes.mergeAlternatives "int" "string" "ProjB" [ ("string", "ProjB") ]
    )
    // But the SAME type from a DIFFERENT project is a distinct fact: it says ProjC
    // disagrees with the primary too, which is what "plan per project" needs.
    Assert.Equal<(string * string) list>(
        [ ("string", "ProjB"); ("string", "ProjC") ],
        FieldSiteTypes.mergeAlternatives "int" "string" "ProjC" [ ("string", "ProjB") ]
    )

[<Fact>]
let ``mergeAlternatives orders alternatives deterministically, whatever the sweep order`` () =
    // Sweep order is project enumeration order; the payload must not depend on it.
    let forward =
        []
        |> FieldSiteTypes.mergeAlternatives "int" "string" "ProjC"
        |> FieldSiteTypes.mergeAlternatives "int" "bool" "ProjB"

    let reverse =
        []
        |> FieldSiteTypes.mergeAlternatives "int" "bool" "ProjB"
        |> FieldSiteTypes.mergeAlternatives "int" "string" "ProjC"

    Assert.Equal<(string * string) list>([ ("bool", "ProjB"); ("string", "ProjC") ], forward)
    Assert.Equal<(string * string) list>(forward, reverse)

[<Fact>]
let ``alternativesFields stays absent on the common single-project row`` () =
    // The row shape for a normal sweep must be byte-identical to before the fix.
    Assert.Empty(FieldSiteTypes.alternativesFields true true [])
    Assert.Empty(FieldSiteTypes.alternativesFields false true [ ("string", "ProjB") ])

[<Fact>]
let ``alternativesFields names each other type WITH the projects that resolved it`` () =
    let fields =
        FieldSiteTypes.alternativesFields true true [ ("bool", "ProjD"); ("string", "ProjB"); ("string", "ProjC") ]

    Assert.Equal(1, List.length fields)
    Assert.Equal("siteTypeAlternatives", fst fields[0])

    // #207 review 2: "plan the site per project" is only actionable when the payload says
    // WHICH project expects which type — a bare list of type strings cannot answer that.
    let serialized = (FsLangMcp.Types.jobj fields).ToJsonString()

    Assert.Equal(
        """{"siteTypeAlternatives":[{"siteType":"bool","projects":["ProjD"]},{"siteType":"string","projects":["ProjB","ProjC"]}]}""",
        serialized
    )

// ── #207 review 1: the column is bounded, and says so when it truncates ─────────

[<Fact>]
let ``alternativeEntries caps distinct types per row and reports how many it dropped`` () =
    let pairs =
        [ ("aaa", "P1"); ("bbb", "P2"); ("ccc", "P3"); ("ddd", "P4"); ("eee", "P5") ]

    let entries, typesOmitted = FieldSiteTypes.alternativeEntries pairs

    Assert.Equal(FieldSiteTypes.AlternativeTypesPerSite, List.length entries)
    Assert.Equal<string list>([ "aaa"; "bbb"; "ccc" ], entries |> List.map (fun (t, _, _) -> t))
    Assert.Equal(2, typesOmitted)

[<Fact>]
let ``alternativeEntries caps projects per type and reports the remainder on the entry`` () =
    let pairs =
        [ for project in [ "P1"; "P2"; "P3"; "P4"; "P5" ] -> ("string", project) ]

    let entries, typesOmitted = FieldSiteTypes.alternativeEntries pairs
    let siteType, projects, projectsOmitted = List.exactlyOne entries

    Assert.Equal("string", siteType)
    Assert.Equal<string list>([ "P1"; "P2"; "P3" ], projects)
    Assert.Equal(2, projectsOmitted)
    Assert.Equal(0, typesOmitted)

[<Fact>]
let ``alternativesFields past the page allowance keeps the COUNT and drops the strings`` () =
    // The count is what makes the truncation non-silent: the caller still learns the site is
    // contested and by how many types, and the page cannot blow the response ceiling.
    let pairs = [ ("bool", "ProjB"); ("string", "ProjC") ]
    let fields = FieldSiteTypes.alternativesFields true false pairs

    Assert.Equal(1, List.length fields)
    Assert.Equal("siteTypeAlternativesOmitted", fst fields[0])

    let serialized = (FsLangMcp.Types.jobj fields).ToJsonString()
    Assert.Equal("""{"siteTypeAlternativesOmitted":2}""", serialized)

[<Fact>]
let ``a full page of pathological rows stays inside the alternatives allowance`` () =
    // The bound the per-type 200-char cap does NOT give on its own: alternatives grow with
    // the number of projects a linked file is compiled by, once per site. Render a full
    // default page of worst-case rows against the real budget loop and measure.
    let hugeType index =
        String.replicate 200 "x" + string index // each already at the siteType cap

    let pairs =
        [ for typeIndex in 1..10 do
              for projectIndex in 1..10 -> (hugeType typeIndex, $"AVeryLongProjectName{projectIndex}") ]

    let mutable budget = FieldSiteTypes.AlternativesPageBudgetChars
    let mutable rendered = 0

    for _ in 1..80 do
        let fields = FieldSiteTypes.alternativesFields true (budget > 0) pairs
        let size = (FsLangMcp.Types.jobj fields).ToJsonString().Length
        rendered <- rendered + size

        for _, node in fields do
            if not (isNull node) then
                budget <- budget - node.ToJsonString().Length

    // Allowance, plus the small per-row counters every remaining row still carries.
    let ceiling = FieldSiteTypes.AlternativesPageBudgetChars + 80 * 64

    Assert.True(
        rendered <= ceiling,
        $"80 pathological rows rendered {rendered} chars of alternatives; allowance + counters is {ceiling}"
    )

[<Fact>]
let ``alternativesTruncated reports every cap, including the projects-only one`` () =
    // The ledger counter must not be inferred from the rendered keys: a row whose TYPES all
    // fit but whose project list was capped renders `siteTypeAlternatives` with no
    // `siteTypeAlternativesOmitted` key, and sniffing keys would miss it.
    let manyProjects =
        [ for project in [ "P1"; "P2"; "P3"; "P4" ] -> ("string", project) ]

    let manyTypes =
        [ ("aaa", "P1"); ("bbb", "P2"); ("ccc", "P3"); ("ddd", "P4") ]

    Assert.True(FieldSiteTypes.alternativesTruncated true manyProjects, "projects cap must count")
    Assert.True(FieldSiteTypes.alternativesTruncated true manyTypes, "types cap must count")
    Assert.True(FieldSiteTypes.alternativesTruncated false [ ("string", "P1") ], "budget exhaustion must count")

    // The ordinary case is not truncation, and neither is an empty column.
    Assert.False(FieldSiteTypes.alternativesTruncated true [ ("string", "P1") ])
    Assert.False(FieldSiteTypes.alternativesTruncated false [])
