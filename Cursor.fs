/// Opaque cursor for stable, offset-based pagination of large tool results.
///
/// Design:
///   - Wire format: Base64(UTF-8 JSON) — opaque to callers, no construction required.
///   - Internal payload: {"offset": N} — integer byte offset into a deterministic (sorted) list.
///   - Malformed cursors yield a structured error; callers must treat the value as opaque.
///   - Consistent with the MCP list-pagination pattern used by resources/list and prompts/list.
module FsLangMcp.Cursor

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

// ─── Payload ──────────────────────────────────────────────────────────────────

[<Struct>]
type CursorPayload = { offset: int }

// ─── Encode ───────────────────────────────────────────────────────────────────

/// Encode an integer offset into an opaque, Base64-encoded cursor string.
let encode (offset: int) : string =
    let json = $"""{"{"}"offset":{offset}{"}"}"""
    Convert.ToBase64String(Encoding.UTF8.GetBytes(json))

// ─── Decode ───────────────────────────────────────────────────────────────────

/// Attempt to decode a cursor string. Returns Ok with the payload or Error with a
/// human-readable message. Agents must not construct cursors; use the value returned
/// by a prior paginated call.
let tryDecode (cursor: string) : Result<CursorPayload, string> =
    if String.IsNullOrWhiteSpace(cursor) then
        Error "cursor must not be empty"
    else
        try
            let bytes = Convert.FromBase64String(cursor)
            let json = Encoding.UTF8.GetString(bytes)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            // TryGetProperty throws InvalidOperationException unless the root is an
            // object. Reject arrays / scalars with a structured Error rather than letting
            // the exception escape and surface as an unexpected server error.
            if root.ValueKind <> JsonValueKind.Object then
                Error "cursor payload must be a JSON object"
            else
            match root.TryGetProperty("offset") with
            | true, offsetEl ->
                match offsetEl.ValueKind with
                | JsonValueKind.Number ->
                    match offsetEl.TryGetInt32() with
                    | true, offset when offset >= 0 -> Ok { offset = offset }
                    | true, _ -> Error "cursor offset must be a non-negative integer"
                    | false, _ -> Error "cursor offset is not a valid int32"
                | _ -> Error "cursor 'offset' field must be a JSON number"
            | false, _ -> Error "cursor JSON is missing required 'offset' field"
        with
        | :? FormatException -> Error "cursor is not valid Base64"
        | :? JsonException as ex -> Error $"cursor payload is not valid JSON: {ex.Message}"

// ─── JSON helpers ─────────────────────────────────────────────────────────────

open FsLangMcp.Types

/// Build the cursor-aware pagination envelope fields.
/// `unitName` names the paginated unit ("files", "uses", "symbols", …) and is
/// surfaced inside `totalEstimate` so callers can self-describe their page shape.
/// Returns a list of (name, JsonNode) pairs to be merged into the tool response object.
let paginationFields
    (unitName: string)
    (totalCount: int)
    (pageOffset: int)
    (pageSize: int)
    (pageCount: int)
    : (string * JsonNode) list =
    let isTruncated = pageOffset + pageCount < totalCount
    let nextCursorNode: JsonNode =
        if isTruncated then
            JsonValue.Create(encode (pageOffset + pageCount))
        else
            null

    [ "truncated", jbool isTruncated
      "nextCursor", nextCursorNode
      "totalEstimate", jobj [ unitName, jint totalCount ]
      "pageOffset", jint pageOffset
      "pageSize", jint pageSize ]
