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
