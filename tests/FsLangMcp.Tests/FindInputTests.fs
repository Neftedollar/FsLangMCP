module FsLangMcp.Tests.FindInputTests

open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FsMcp.Core
open FsMcp.Server
open FsLangMcp.Types
open FsLangMcp.FindInput

let private input : FindToolArgs =
    { query = None; queries = None; countsOnly = None; kind = None; scope = None
      exact = None; ``member`` = None; field = None; path = None; line = None
      word = None; occurrence = None; character = None; contextLines = None
      includeDeclaration = None; includeInfo = None; includePerProject = None
      includeSiteTypes = None; projectPath = None; maxResults = None
      timeoutMs = None; cursor = None }

let private rejected args =
    match prepare args with
    | Error response -> Assert.Equal("invalid_args", response["status"].GetValue<string>())
    | Ok _ -> Assert.Fail("Unsupported inventory input was accepted.")

[<Fact>]
let ``legacy query stays a site request with unchanged selectors and cursor`` () =
    let legacy = { input with query = Some " Type "; kind = Some "field"; field = Some "Value"; cursor = Some "v2-token" }
    match prepare legacy with
    | Ok(Sites args) ->
        Assert.Equal(" Type ", args.query)
        Assert.Equal(legacy.kind, args.kind)
        Assert.Equal(legacy.field, args.field)
        Assert.Equal(legacy.cursor, args.cursor)
    | _ -> Assert.Fail("Legacy site request was altered.")

[<Fact>]
let ``batch defaults to count-only symbol inventory and preserves order`` () =
    match prepare { input with queries = Some [ " First "; "Second" ]; kind = Some "auto" } with
    | Ok(Inventory(args, queries)) ->
        Assert.Equal<string array>([| "First"; "Second" |], queries)
        Assert.Equal(Some "symbol", args.kind)
    | _ -> Assert.Fail("Batch did not become inventory.")

[<Fact>]
let ``one name can opt into inventory without changing old default`` () =
    match prepare { input with query = Some "Name"; countsOnly = Some true; kind = Some "definition" } with
    | Ok(Inventory(args, queries)) ->
        Assert.Equal(Some "definition", args.kind)
        Assert.Equal<string array>([| "Name" |], queries)
    | _ -> Assert.Fail("Single-name countsOnly was not inventory.")

[<Fact>]
let ``query alternative cardinality and name validation fail before admission`` () =
    for args in
        [ input
          { input with query = Some "Name"; queries = Some [ "Other" ] }
          { input with queries = Some [] }
          { input with queries = Some [ " " ] }
          { input with queries = Some [ null ] }
          { input with queries = Some [ "Name"; " Name " ] }
          { input with queries = Some [ for index in 0..50 -> $"Name{index}" ] }
          { input with queries = Some [ "Name" ]; countsOnly = Some false } ] do
        rejected args

[<Fact>]
let ``inventory rejects every unsupported selector instead of silently ignoring it`` () =
    let inventory = { input with queries = Some [ "Name" ] }
    for args in
        [ { inventory with kind = Some "field" }
          { inventory with kind = Some "members" }
          { inventory with kind = Some "position" }
          { inventory with ``member`` = Some "Call" }
          { inventory with field = Some "Field" }
          { inventory with line = Some 0 }
          { inventory with character = Some 0 }
          { inventory with occurrence = Some -1 }
          { inventory with word = Some "Name" }
          { inventory with contextLines = Some 1 }
          { inventory with contextLines = Some -1 }
          { inventory with includeSiteTypes = Some true }
          { inventory with cursor = Some "anything" } ] do
        rejected args

let private definition handler =
    match TypedTool.define<FindToolArgs> "find" "Count inventory binding regression." handler with
    | Ok tool -> tool
    | Error error -> failwith $"Cannot construct find input tool: {error}"

let private arguments json =
    use document = JsonDocument.Parse(json: string)
    document.RootElement.EnumerateObject()
    |> Seq.map (fun property -> property.Name, property.Value.Clone())
    |> Map.ofSeq

let private assertSchemaType (expected: string) (schema: JsonElement) =
    let declaredType = schema.GetProperty("type")
    let types =
        match declaredType.ValueKind with
        | JsonValueKind.String -> [| declaredType.GetString() |]
        | JsonValueKind.Array -> declaredType.EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
        | _ -> failwith $"Unexpected schema type declaration: {declaredType}"
    Assert.Contains<string>(expected, types)
    Assert.All(types, fun actual -> Assert.True(actual = expected || actual = "null", $"Unexpected schema type: {actual}"))

[<Fact>]
let ``typed tool publishes an optional query and an array queries schema`` () =
    let tool = definition (fun _ _ -> Task.FromResult(Ok [ Text "{}" ]))
    let schema = tool.InputSchema.Value
    let properties = schema.GetProperty("properties")
    assertSchemaType "string" (properties.GetProperty("query"))
    assertSchemaType "array" (properties.GetProperty("queries"))
    assertSchemaType "string" (properties.GetProperty("queries").GetProperty("items"))
    assertSchemaType "boolean" (properties.GetProperty("countsOnly"))
    let mutable required = Unchecked.defaultof<JsonElement>
    if schema.TryGetProperty("required", &required) then
        Assert.DoesNotContain("query", required.EnumerateArray() |> Seq.map _.GetString())

[<Fact>]
let ``typed tool binds array inventory and legacy query while rejecting malformed arrays`` () =
    task {
        let mutable received: FindToolArgs option = None
        let tool = definition (fun args _ ->
            received <- Some args
            Task.FromResult(Ok [ Text "{}" ]))

        let! batch = tool.Handler (arguments """{"queries":["A","B"],"countsOnly":true}""") CancellationToken.None
        Assert.True(Result.isOk batch)
        Assert.Equal(Some [ "A"; "B" ], received.Value.queries)
        let! legacy = tool.Handler (arguments """{"query":"A","cursor":"token"}""") CancellationToken.None
        Assert.True(Result.isOk legacy)
        Assert.Equal(Some "A", received.Value.query)
        Assert.Equal(Some "token", received.Value.cursor)

        for json in [ """{"queries":"A"}"""; """{"queries":["A",1]}""" ] do
            received <- None
            let! _ = Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(fun () ->
                tool.Handler (arguments json) CancellationToken.None :> Task)
            Assert.True(received.IsNone, "Invalid arrays must not enter the public handler.")

        // The SDK can bind a null string element. Domain validation must reject
        // it before trimming or gate admission, even though binding succeeded.
        received <- None
        let! nullable = tool.Handler (arguments """{"queries":[null]}""") CancellationToken.None
        Assert.True(Result.isOk nullable)
        Assert.True(received.IsSome)
        rejected received.Value
    }
