module FsLangMcp.Tests.CursorPropertyTests

/// Property-based tests for FsLangMcp.Cursor — round-trips, envelope invariants,
/// and rejection of malformed inputs. These complement the example tests in
/// OutlinePaginationTests.fs by exploring boundary cases via random generation.

open System.Text.Json.Nodes
open FsCheck
open FsCheck.Xunit
open FsCheck.FSharp
open FsLangMcp.Cursor

// ─── Round-trip ───────────────────────────────────────────────────────────────

[<Property>]
let ``encode then tryDecode roundtrips for any non-negative offset`` (NonNegativeInt n) =
    match tryDecode (encode n) with
    | Ok payload -> payload.offset = n
    | Error _ -> false

// ─── Rejection ────────────────────────────────────────────────────────────────

[<Property>]
let ``tryDecode never raises on arbitrary string input`` (s: string) =
    // We do not constrain s; empty, control chars, anything goes. The contract
    // is that tryDecode returns a structured Error rather than throwing.
    match tryDecode s with
    | Ok _ | Error _ -> true

// ─── Pagination envelope invariants ───────────────────────────────────────────

let private decodeEnvelope (fields: (string * JsonNode) list) =
    let m = fields |> Map.ofList
    let truncated = m.["truncated"].GetValue<bool>()
    let pageOffset = m.["pageOffset"].GetValue<int>()
    let pageSize = m.["pageSize"].GetValue<int>()
    let nextCursor =
        match m.["nextCursor"] with
        | null -> None
        | n -> Some (n.GetValue<string>())
    truncated, pageOffset, pageSize, nextCursor

[<Property>]
let ``paginationFields: truncated iff pageOffset + pageCount < totalCount``
    (NonNegativeInt total) (NonNegativeInt offset) (PositiveInt size) (NonNegativeInt count) =
    let fields = paginationFields "items" total offset size count
    let truncated, _, _, _ = decodeEnvelope fields
    truncated = (offset + count < total)

[<Property>]
let ``paginationFields: nextCursor is non-null iff truncated``
    (NonNegativeInt total) (NonNegativeInt offset) (PositiveInt size) (NonNegativeInt count) =
    let fields = paginationFields "items" total offset size count
    let truncated, _, _, nextCursor = decodeEnvelope fields
    truncated = nextCursor.IsSome

[<Property>]
let ``paginationFields: when truncated, nextCursor offset = pageOffset + pageCount``
    (NonNegativeInt total) (NonNegativeInt offset) (PositiveInt size) (NonNegativeInt count) =
    let fields = paginationFields "items" total offset size count
    let truncated, _, _, nextCursor = decodeEnvelope fields
    if truncated then
        match nextCursor with
        | None -> false
        | Some c ->
            match tryDecode c with
            | Ok payload -> payload.offset = offset + count
            | Error _ -> false
    else
        true  // vacuous when not truncated

[<Property>]
let ``paginationFields: pageOffset and pageSize echo the inputs verbatim``
    (NonNegativeInt total) (NonNegativeInt offset) (PositiveInt size) (NonNegativeInt count) =
    let fields = paginationFields "items" total offset size count
    let _, po, ps, _ = decodeEnvelope fields
    po = offset && ps = size

[<Property>]
let ``paginationFields: totalEstimate carries the caller-supplied unitName``
    (NonNegativeInt total) (NonNegativeInt offset) (PositiveInt size) (NonNegativeInt count) =
    // Pick a stable non-empty unit name so the JsonObject lookup is well-defined.
    let unit = "things"
    let fields = paginationFields unit total offset size count
    let m = fields |> Map.ofList
    let totalEstimate = m.["totalEstimate"]
    totalEstimate.[unit].GetValue<int>() = total
