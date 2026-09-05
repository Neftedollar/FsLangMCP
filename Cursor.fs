/// Opaque cursor for stable, offset-based pagination of large tool results.
///
/// Design:
///   - Wire format: Base64(UTF-8 JSON) — opaque to callers, no construction required.
///   - Internal payload: {"offset": N} — integer byte offset into a deterministic (sorted) list.
///   - Malformed cursors yield a structured error; callers must treat the value as opaque.
///   - Consistent with the MCP list-pagination pattern used by resources/list and prompts/list.
module FsLangMcp.Cursor

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes

// ─── Payload ──────────────────────────────────────────────────────────────────

[<Struct>]
type CursorPayload = { offset: int }

/// Stable request inputs that identify one logical find stream. Fields are written
/// in this declared order by canonicalFindQueryBytesV2; JSON property ordering is
/// deliberately not part of the identity contract.
[<NoEquality; NoComparison>]
type FindPositionRequestV2 =
    { Path: string
      Line: int option
      Character: int option
      Word: string option
      Occurrence: int option }

[<NoEquality; NoComparison>]
type FindQueryV2 =
    { Schema: int
      Query: string
      RequestedKind: string
      RequestedScope: string
      Target: string
      Path: string option
      Position: FindPositionRequestV2 option
      Exact: bool
      Member: string option
      Field: string option
      ContextLines: int
      IncludeDeclaration: bool
      IncludeInfo: bool
      IncludePerProject: bool
      IncludeSiteTypes: bool }

[<NoEquality; NoComparison>]
type FindCursorV2 =
    { Offset: int
      Query: string
      Snapshot: string }

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type FindCursorDecodeErrorKind =
    | Legacy
    | UnsupportedVersion
    | Malformed
    | ToolMismatch

[<NoEquality; NoComparison>]
type FindCursorDecodeError =
    { Kind: FindCursorDecodeErrorKind
      Message: string }

[<Literal>]
let MaxFindCursorDecodedBytes = 1024

/// Maximum canonical Base64 length whose decoded form can fit in 1 KiB. The raw
/// input is rejected above this size before Convert.FromBase64String can allocate.
[<Literal>]
let MaxFindCursorEncodedChars = 1368

let findCursorErrorKind (error: FindCursorDecodeError) =
    match error.Kind with
    | FindCursorDecodeErrorKind.Legacy
    | FindCursorDecodeErrorKind.UnsupportedVersion -> "cursor_version_unsupported"
    | FindCursorDecodeErrorKind.Malformed -> "cursor_malformed"
    | FindCursorDecodeErrorKind.ToolMismatch -> "cursor_tool_mismatch"

let private base64UrlWithoutPadding (bytes: byte array) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private hashBytes (bytes: byte array) =
    SHA256.HashData(bytes) |> base64UrlWithoutPadding

let private decodeBounded (cursor: string) =
    if String.IsNullOrWhiteSpace(cursor) then
        Error "cursor must not be empty"
    elif cursor.Length > MaxFindCursorEncodedChars then
        Error $"cursor exceeds the maximum encoded size of %d{MaxFindCursorEncodedChars} characters"
    else
        try
            let bytes = Convert.FromBase64String(cursor)

            if bytes.Length > MaxFindCursorDecodedBytes then
                Error $"cursor payload exceeds the maximum decoded size of %d{MaxFindCursorDecodedBytes} bytes"
            else
                Ok bytes
        with :? FormatException ->
            Error "cursor is not valid Base64"

let private propertiesWithoutDuplicates (root: JsonElement) =
    let properties = root.EnumerateObject() |> Seq.toArray
    let names = HashSet<string>(StringComparer.Ordinal)
    let duplicate = properties |> Array.tryFind (fun property -> not (names.Add(property.Name)))

    match duplicate with
    | Some property -> Error $"cursor JSON contains duplicate property '%s{property.Name}'"
    | None -> Ok(properties, names)

let private hasExactly (expected: string array) (properties: JsonProperty array) (names: HashSet<string>) =
    properties.Length = expected.Length
    && expected |> Array.forall names.Contains

let private tryNonNegativeOffset (element: JsonElement) =
    if element.ValueKind <> JsonValueKind.Number then
        Error "cursor 'offset' field must be a JSON number"
    else
        match element.TryGetInt32() with
        | true, offset when offset >= 0 -> Ok offset
        | true, _ -> Error "cursor offset must be a non-negative integer"
        | false, _ -> Error "cursor offset is not a valid int32"

let private isCanonicalSha256 (value: string) =
    if isNull value || value.Length <> 43 then
        false
    elif
        value
        |> Seq.exists (fun character ->
            not (
                Char.IsAsciiLetterOrDigit character
                || character = '-'
                || character = '_'
            ))
    then
        false
    else
        try
            let padded = value.Replace('-', '+').Replace('_', '/') + "="
            let bytes = Convert.FromBase64String(padded)
            bytes.Length = 32 && String.Equals(base64UrlWithoutPadding bytes, value, StringComparison.Ordinal)
        with :? FormatException ->
            false

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
    match decodeBounded cursor with
    | Error reason -> Error reason
    | Ok bytes ->
        try
            use doc = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
            let root = doc.RootElement

            // TryGetProperty throws InvalidOperationException unless the root is an
            // object. Reject arrays / scalars with a structured Error rather than letting
            // the exception escape and surface as an unexpected server error.
            if root.ValueKind <> JsonValueKind.Object then
                Error "cursor payload must be a JSON object"
            else
                match propertiesWithoutDuplicates root with
                | Error reason -> Error reason
                | Ok(properties, names) when names.Contains("tool") ->
                    Error "cursor_tool_mismatch: this cursor is tagged for another tool and cannot be used by a legacy cursor consumer"
                | Ok(properties, names) when hasExactly [| "offset" |] properties names ->
                    tryNonNegativeOffset (root.GetProperty("offset"))
                    |> Result.map (fun offset -> { offset = offset })
                | Ok _ -> Error "legacy cursor payload must contain exactly the 'offset' property"
        with
        | :? JsonException as ex -> Error $"cursor payload is not valid JSON: {ex.Message}"
        // JsonDocument accepts escaped lone surrogates; accessing such a property
        // name throws InvalidOperationException rather than JsonException.
        | :? InvalidOperationException -> Error "cursor JSON strings must contain valid Unicode"

// ─── Find v2 cursor and identity helpers ──────────────────────────────────────

let private findDecodeError kind message =
    Error { Kind = kind; Message = message }

/// Strictly decode the stateless find continuation contract. Legacy offset-only
/// tokens are recognized separately so callers can return the version/restart route.
let tryDecodeFind (cursor: string) : Result<FindCursorV2, FindCursorDecodeError> =
    match decodeBounded cursor with
    | Error reason -> findDecodeError FindCursorDecodeErrorKind.Malformed reason
    | Ok bytes ->
        try
            use doc = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
            let root = doc.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                findDecodeError FindCursorDecodeErrorKind.Malformed "find cursor payload must be a JSON object"
            else
                match propertiesWithoutDuplicates root with
                | Error reason -> findDecodeError FindCursorDecodeErrorKind.Malformed reason
                | Ok(properties, names) when hasExactly [| "offset" |] properties names ->
                    match tryNonNegativeOffset (root.GetProperty("offset")) with
                    | Ok _ ->
                        findDecodeError
                            FindCursorDecodeErrorKind.Legacy
                            "Offset-only find cursors are unsupported by the v2 continuation contract."
                    | Error reason -> findDecodeError FindCursorDecodeErrorKind.Malformed reason
                | Ok(properties, names) ->
                    let expected = [| "v"; "tool"; "offset"; "query"; "snapshot" |]

                    if not (hasExactly expected properties names) then
                        findDecodeError
                            FindCursorDecodeErrorKind.Malformed
                            "find cursor payload must contain exactly v, tool, offset, query, and snapshot"
                    else
                        let version = root.GetProperty("v")
                        let tool = root.GetProperty("tool")
                        let query = root.GetProperty("query")
                        let snapshot = root.GetProperty("snapshot")

                        // TryGetInt32 still throws on non-number JSON kinds. Guard
                        // before calling it, not in a tuple pattern evaluated eagerly.
                        let numericVersion =
                            if version.ValueKind = JsonValueKind.Number then version.TryGetInt32()
                            else false, 0

                        match version.ValueKind, numericVersion with
                        | JsonValueKind.Number, (true, value) when value <> 2 ->
                            findDecodeError
                                FindCursorDecodeErrorKind.UnsupportedVersion
                                $"Find cursor version %d{value} is unsupported."
                        | JsonValueKind.Number, (true, 2) when
                            tool.ValueKind = JsonValueKind.String
                            && not (String.Equals(tool.GetString(), "find", StringComparison.Ordinal))
                            ->
                            findDecodeError
                                FindCursorDecodeErrorKind.ToolMismatch
                                "This cursor was issued for another tool and cannot be used with find."
                        | JsonValueKind.Number, (true, 2) when tool.ValueKind <> JsonValueKind.String ->
                            findDecodeError FindCursorDecodeErrorKind.Malformed "find cursor 'tool' must be 'find'"
                        | JsonValueKind.Number, (true, 2) when
                            query.ValueKind <> JsonValueKind.String
                            || not (isCanonicalSha256 (query.GetString()))
                            ->
                            findDecodeError
                                FindCursorDecodeErrorKind.Malformed
                                "find cursor 'query' must be a canonical unpadded Base64URL SHA-256 value"
                        | JsonValueKind.Number, (true, 2) when
                            snapshot.ValueKind <> JsonValueKind.String
                            || not (isCanonicalSha256 (snapshot.GetString()))
                            ->
                            findDecodeError
                                FindCursorDecodeErrorKind.Malformed
                                "find cursor 'snapshot' must be a canonical unpadded Base64URL SHA-256 value"
                        | JsonValueKind.Number, (true, 2) ->
                            match tryNonNegativeOffset (root.GetProperty("offset")) with
                            | Error reason -> findDecodeError FindCursorDecodeErrorKind.Malformed reason
                            | Ok offset ->
                                Ok
                                    { Offset = offset
                                      Query = query.GetString()
                                      Snapshot = snapshot.GetString() }
                        | _ ->
                            findDecodeError FindCursorDecodeErrorKind.Malformed "find cursor 'v' must be an integer"
        with
        | :? JsonException as ex ->
            findDecodeError FindCursorDecodeErrorKind.Malformed $"find cursor payload is not valid JSON: {ex.Message}"
        | :? InvalidOperationException ->
            findDecodeError FindCursorDecodeErrorKind.Malformed "find cursor JSON strings must contain valid Unicode"

let private writeStringOption (writer: Utf8JsonWriter) (value: string option) =
    writer.WriteStartArray()

    match value with
    | None -> writer.WriteNumberValue(0)
    | Some text ->
        writer.WriteNumberValue(1)
        writer.WriteStringValue(text)

    writer.WriteEndArray()

let private writeIntOption (writer: Utf8JsonWriter) (value: int option) =
    writer.WriteStartArray()

    match value with
    | None -> writer.WriteNumberValue(0)
    | Some number ->
        writer.WriteNumberValue(1)
        writer.WriteNumberValue(number)

    writer.WriteEndArray()

/// Canonical ordered UTF-8 query bytes. Arrays provide structural tags for every
/// option, so None, Some "", and materialized defaults cannot collapse.
let canonicalFindQueryBytesV2 (query: FindQueryV2) =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream)
    writer.WriteStartArray()
    writer.WriteNumberValue(query.Schema)
    writer.WriteStringValue(query.Query)
    writer.WriteStringValue(query.RequestedKind)
    writer.WriteStringValue(query.RequestedScope)
    writer.WriteStringValue(query.Target)
    writeStringOption writer query.Path
    writer.WriteStartArray()

    match query.Position with
    | None -> writer.WriteNumberValue(0)
    | Some position ->
        writer.WriteNumberValue(1)
        writer.WriteStringValue(position.Path)
        writeIntOption writer position.Line
        writeIntOption writer position.Character
        writeStringOption writer position.Word
        writeIntOption writer position.Occurrence

    writer.WriteEndArray()
    writer.WriteBooleanValue(query.Exact)
    writeStringOption writer query.Member
    writeStringOption writer query.Field
    writer.WriteNumberValue(query.ContextLines)
    writer.WriteBooleanValue(query.IncludeDeclaration)
    writer.WriteBooleanValue(query.IncludeInfo)
    writer.WriteBooleanValue(query.IncludePerProject)
    writer.WriteBooleanValue(query.IncludeSiteTypes)
    writer.WriteEndArray()
    writer.Flush()
    stream.ToArray()

let findQueryIdentityV2 query =
    canonicalFindQueryBytesV2 query |> hashBytes

let rec private writeCanonicalJson (writer: Utf8JsonWriter) (node: JsonNode) =
    match node with
    | null -> writer.WriteNullValue()
    | :? JsonObject as objectNode ->
        writer.WriteStartObject()

        objectNode
        |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left.Key, right.Key))
        |> Seq.iter (fun property ->
            writer.WritePropertyName(property.Key)
            writeCanonicalJson writer property.Value)

        writer.WriteEndObject()
    | :? JsonArray as arrayNode ->
        writer.WriteStartArray()
        arrayNode |> Seq.iter (writeCanonicalJson writer)
        writer.WriteEndArray()
    | value -> value.WriteTo(writer)

let private canonicalJsonBytes (node: JsonNode) =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream)
    writeCanonicalJson writer node
    writer.Flush()
    stream.ToArray()

/// Hash a complete canonical find snapshot model. Object properties are sorted
/// ordinally while array order is preserved, so construction order is irrelevant
/// but the ordered result stream remains identity-bearing.
let findSnapshotIdentityV2 (snapshot: JsonNode) =
    canonicalJsonBytes snapshot |> hashBytes

/// Fixed-size identity for free-form text such as compiler diagnostic messages.
let textIdentityV2 (text: string) =
    (if isNull text then "" else text)
    |> Encoding.UTF8.GetBytes
    |> hashBytes

let encodeFindV2 (offset: int) (queryIdentity: string) (snapshotIdentity: string) : string =
    if offset < 0 then
        invalidArg (nameof offset) "find cursor offset must be non-negative"

    if not (isCanonicalSha256 queryIdentity) then
        invalidArg (nameof queryIdentity) "query identity must be a canonical unpadded Base64URL SHA-256 value"

    if not (isCanonicalSha256 snapshotIdentity) then
        invalidArg (nameof snapshotIdentity) "snapshot identity must be a canonical unpadded Base64URL SHA-256 value"

    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream)
    writer.WriteStartObject()
    writer.WriteNumber("v", 2)
    writer.WriteString("tool", "find")
    writer.WriteNumber("offset", offset)
    writer.WriteString("query", queryIdentity)
    writer.WriteString("snapshot", snapshotIdentity)
    writer.WriteEndObject()
    writer.Flush()
    Convert.ToBase64String(stream.ToArray())

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
      "totalEstimate", jobj [ (unitName, jint totalCount) ]
      "pageOffset", jint pageOffset
      "pageSize", jint pageSize ]

/// Build find-only pagination fields using the stateless v2 continuation. Unlike
/// paginationFields this never mints an offset-only token.
let findPaginationFieldsV2
    (unitName: string)
    (totalCount: int)
    (pageOffset: int)
    (pageSize: int)
    (pageCount: int)
    (queryIdentity: string)
    (snapshotIdentity: string)
    : (string * JsonNode) list =
    let isTruncated = pageOffset + pageCount < totalCount
    let nextCursorNode: JsonNode =
        if isTruncated && pageCount > 0 then
            JsonValue.Create(encodeFindV2 (pageOffset + pageCount) queryIdentity snapshotIdentity)
        else
            null

    [ "truncated", jbool isTruncated
      "nextCursor", nextCursorNode
      "totalEstimate", jobj [ (unitName, jint totalCount) ]
      "pageOffset", jint pageOffset
      "pageSize", jint pageSize ]
