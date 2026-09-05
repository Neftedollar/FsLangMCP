module FsLangMcp.Tests.FindCursorV2Tests

open System
open System.Text
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.Cursor

let private hashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
let private hashB = "__________________________________________8"

let private encodeRaw (json: string) =
    Convert.ToBase64String(Encoding.UTF8.GetBytes(json))

let private assertDecodeError expectedKind cursor =
    match tryDecodeFind cursor with
    | Ok value -> Assert.Fail($"Expected {expectedKind}, decoded offset {value.Offset}.")
    | Error error -> Assert.Equal(expectedKind, findCursorErrorKind error)

let private query path target : FindQueryV2 =
    { Schema = 2
      Query = "Target"
      RequestedKind = "auto"
      RequestedScope = "workspace"
      Target = target
      Path = path
      Position = None
      Exact = true
      Member = None
      Field = None
      ContextLines = 0
      IncludeDeclaration = true
      IncludeInfo = false
      IncludePerProject = true
      IncludeSiteTypes = false }

[<Fact>]
let ``find v2 cursor roundtrips and contains only the accepted opaque fields`` () =
    let encoded = encodeFindV2 17 hashA hashB

    match tryDecodeFind encoded with
    | Error error -> Assert.Fail(error.Message)
    | Ok decoded ->
        Assert.Equal(17, decoded.Offset)
        Assert.Equal(hashA, decoded.Query)
        Assert.Equal(hashB, decoded.Snapshot)

    let json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
    let node = JsonNode.Parse(json).AsObject()
    Assert.Equal<string array>([| "v"; "tool"; "offset"; "query"; "snapshot" |], node |> Seq.map _.Key |> Seq.toArray)
    Assert.Equal(2, node["v"].GetValue<int>())
    Assert.Equal("find", node["tool"].GetValue<string>())
    Assert.DoesNotContain("Target", json, StringComparison.Ordinal)
    Assert.DoesNotContain("/workspace", json, StringComparison.Ordinal)

[<Theory>]
[<InlineData("{\"offset\":1}", "cursor_version_unsupported")>]
[<InlineData("{\"v\":3,\"tool\":\"find\",\"offset\":1,\"query\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"snapshot\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}", "cursor_version_unsupported")>]
[<InlineData("{\"v\":2,\"tool\":\"other\",\"offset\":1,\"query\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"snapshot\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}", "cursor_tool_mismatch")>]
[<InlineData("{\"tool\":\"other\"}", "cursor_malformed")>]
[<InlineData("{\"v\":3,\"tool\":\"find\",\"offset\":1,\"query\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"snapshot\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"extra\":true}", "cursor_malformed")>]
[<InlineData("{\"v\":2,\"tool\":\"find\",\"offset\":-1,\"query\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"snapshot\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}", "cursor_malformed")>]
[<InlineData("{\"v\":2,\"tool\":\"find\",\"offset\":1,\"query\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\",\"snapshot\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}", "cursor_malformed")>]
[<InlineData("{\"v\":2,\"tool\":\"find\",\"offset\":1,\"query\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"snapshot\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"extra\":true}", "cursor_malformed")>]
[<InlineData("{\"v\":2,\"tool\":\"find\",\"offset\":1,\"offset\":2,\"query\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"snapshot\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}", "cursor_malformed")>]
let ``find decoder returns stable error kinds`` (json: string) (expectedKind: string) =
    assertDecodeError expectedKind (encodeRaw json)

[<Theory>]
[<InlineData("\"2\"")>]
[<InlineData("null")>]
[<InlineData("true")>]
[<InlineData("false")>]
[<InlineData("{}")>]
[<InlineData("[]")>]
[<InlineData("2.5")>]
[<InlineData("2147483648")>]
let ``noninteger version types and malicious duplicates never escape decoding`` (version: string) =
    let fields = $"\"tool\":\"find\",\"offset\":1,\"query\":\"{hashA}\",\"snapshot\":\"{hashA}\""
    for json in
        [ $"{{\"v\":{version},{fields}}}"
          $"{{\"v\":{version},\"v\":2,{fields}}}"
          $"{{\"v\":2,\"v\":{version},{fields}}}"
          $"{{\"v\":{version},{fields},\"extra\":true}}" ] do
        assertDecodeError "cursor_malformed" (encodeRaw json)

[<Theory>]
[<InlineData("\\uD800")>]
[<InlineData("\\uDC00")>]
let ``invalid Unicode strings and property names never escape either decoder`` (escape: string) =
    for field in [ "tool"; "query"; "snapshot"; "propertyName" ] do
        let value name normal = if field = name then escape else normal
        let tool = value "tool" "find"
        let query = value "query" hashA
        let snapshot = value "snapshot" hashA
        let name = value "propertyName" "v"
        let json = $"{{\"{name}\":2,\"tool\":\"{tool}\",\"offset\":1,\"query\":\"{query}\",\"snapshot\":\"{snapshot}\"}}"
        assertDecodeError "cursor_malformed" (encodeRaw json)

    let invalidLegacyName = encodeRaw $"{{\"offset\":1,\"{escape}\":2}}"
    Assert.True(Result.isError (tryDecode invalidLegacyName))
    Assert.True(Result.isError (tryDecode (encodeRaw $"{{\"{escape}\":1}}")))

[<Fact>]
let ``find decoder rejects oversized encoded and decoded payloads before parsing`` () =
    assertDecodeError "cursor_malformed" (String.replicate (MaxFindCursorEncodedChars + 1) "A")
    assertDecodeError "cursor_malformed" (encodeRaw (String.replicate (MaxFindCursorDecodedBytes + 1) "x"))

[<Fact>]
let ``legacy decoder accepts only the exact offset payload and rejects find v2 as a tool mismatch`` () =
    match tryDecode (encode 9) with
    | Ok payload -> Assert.Equal(9, payload.offset)
    | Error reason -> Assert.Fail(reason)

    match tryDecode (encodeRaw "{\"offset\":9,\"extra\":true}") with
    | Ok _ -> Assert.Fail("Unknown legacy properties must be rejected.")
    | Error reason -> Assert.Contains("exactly", reason, StringComparison.Ordinal)

    match tryDecode (encodeFindV2 9 hashA hashB) with
    | Ok _ -> Assert.Fail("A legacy consumer must not extract a find v2 offset.")
    | Error reason -> Assert.StartsWith("cursor_tool_mismatch", reason, StringComparison.Ordinal)

[<Fact>]
let ``canonical query bytes use explicit option tags and stable declared field order`` () =
    let withoutPath = canonicalFindQueryBytesV2 (query None "/workspace")
    let withEmptyPath = canonicalFindQueryBytesV2 (query (Some "") "/workspace")
    let repeated = canonicalFindQueryBytesV2 (query None "/workspace")

    Assert.Equal<byte array>(withoutPath, repeated)
    Assert.False(String.Equals(Convert.ToHexString(withoutPath), Convert.ToHexString(withEmptyPath), StringComparison.Ordinal))
    Assert.Equal(findQueryIdentityV2 (query None "/workspace"), findQueryIdentityV2 (query None "/workspace"))

[<Fact>]
let ``find pagination cursor advances by delivered rows and carries both identities`` () =
    let fields = findPaginationFieldsV2 "sites" 10 2 8 3 hashA hashB |> Map.ofList
    Assert.True(fields["truncated"].GetValue<bool>())
    Assert.Equal(2, fields["pageOffset"].GetValue<int>())
    Assert.Equal(8, fields["pageSize"].GetValue<int>())

    match tryDecodeFind (fields["nextCursor"].GetValue<string>()) with
    | Error error -> Assert.Fail(error.Message)
    | Ok decoded ->
        Assert.Equal(5, decoded.Offset)
        Assert.Equal(hashA, decoded.Query)
        Assert.Equal(hashB, decoded.Snapshot)
