module FsLangMcp.Tests.LspBridgeTests

open System
open System.IO
open System.Text.Json
open Xunit
open FsLangMcp.LspBridge

let private jsonElement (json: string) =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()

[<Fact>]
let ``workspace notification parser recognizes FSAC content wrapped finished event`` () =
    let payload =
        jsonElement """{"content":"{\"Kind\":\"workspaceLoad\",\"Data\":{\"Status\":\"finished\"}}"}"""

    Assert.True(WorkspaceNotification.isWorkspaceLoadFinished payload)

[<Fact>]
let ``workspace notification parser recognizes direct finished status`` () =
    let payload = jsonElement """{"status":"finished"}"""

    Assert.True(WorkspaceNotification.isWorkspaceLoadFinished payload)

[<Fact>]
let ``workspace notification parser ignores project loading events`` () =
    let payload =
        jsonElement """{"content":"{\"Kind\":\"projectLoading\",\"Data\":{\"Project\":\"/tmp/App.fsproj\"}}"}"""

    Assert.False(WorkspaceNotification.isWorkspaceLoadFinished payload)

[<Fact>]
let ``workspace notification parser ignores malformed content`` () =
    let payload = jsonElement """{"content":"not json"}"""

    Assert.False(WorkspaceNotification.isWorkspaceLoadFinished payload)

[<Fact>]
let ``workspace selection reports ambiguous solutions in a directory`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_workspace_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.slnx"), "")
        File.WriteAllText(Path.Combine(root, "B.slnx"), "")

        match WorkspaceSelection.select root with
        | WorkspaceSelection.Ambiguous candidates ->
            Assert.Equal(2, candidates.Length)
            Assert.All(candidates, fun candidate -> Assert.Equal(WorkspaceSelection.Solution, candidate.Kind))
        | WorkspaceSelection.Selected _ -> Assert.Fail("Expected ambiguous workspace selection.")
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``workspace selection prefers single solution over directory auto selection`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_workspace_one_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        let solutionPath = Path.Combine(root, "Only.slnx")
        File.WriteAllText(solutionPath, "")

        match WorkspaceSelection.select root with
        | WorkspaceSelection.Selected(path, candidates) ->
            Assert.Equal(solutionPath, path)
            Assert.Single(candidates) |> ignore
        | WorkspaceSelection.Ambiguous _ -> Assert.Fail("Expected selected workspace.")
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

// ─── LspResponseShape (response building, pure) ──────────────────────────────

open System.Text.Json.Nodes
open FsLangMcp.LspBridge.LspResponseShape

[<Fact>]
let ``lspStateString maps true to ready`` () =
    Assert.Equal("ready", lspStateString true)

[<Fact>]
let ``lspStateString maps false to warming`` () =
    Assert.Equal("warming", lspStateString false)

[<Fact>]
let ``assessSymbolIndex is true for any non-empty response`` () =
    let response: JsonNode = JsonArray(JsonValue.Create("a") :> JsonNode) :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-10.0))

    Assert.True(assessSymbolIndex response ready now (TimeSpan.FromSeconds 3.0))

[<Fact>]
let ``assessSymbolIndex is true for empty response when workspaceReadyAt is None`` () =
    // Defensive fallback — if we never observed ready, don't claim the index is warming
    let response: JsonNode = JsonArray() :> JsonNode
    let now = DateTimeOffset.UtcNow

    Assert.False(assessSymbolIndex response ValueNone now (TimeSpan.FromSeconds 3.0))

[<Fact>]
let ``assessSymbolIndex is false for empty response within warmup window`` () =
    let response: JsonNode = JsonArray() :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-1.0))

    Assert.False(assessSymbolIndex response ready now (TimeSpan.FromSeconds 3.0))

[<Fact>]
let ``assessSymbolIndex is true for empty response after warmup window elapsed`` () =
    let response: JsonNode = JsonArray() :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-10.0))

    Assert.True(assessSymbolIndex response ready now (TimeSpan.FromSeconds 3.0))

[<Fact>]
let ``assessSymbolIndex treats non-array responses as ready regardless of timing`` () =
    let response: JsonNode = JsonObject() :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-1.0))

    Assert.True(assessSymbolIndex response ready now (TimeSpan.FromSeconds 3.0))

[<Fact>]
let ``diagnosticsResponseForFile builds status+lspState+count+result`` () =
    let payload: JsonNode = JsonArray() :> JsonNode
    let result = diagnosticsResponseForFile true 5 payload

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal("ready", result["lspState"].GetValue<string>())
    Assert.Equal(5, result["diagnosticsFileCount"].GetValue<int>())
    Assert.NotNull(result["result"])

[<Fact>]
let ``diagnosticsResponseForFile reports warming when workspace not ready`` () =
    let payload: JsonNode = JsonArray() :> JsonNode
    let result = diagnosticsResponseForFile false 0 payload

    Assert.Equal("warming", result["lspState"].GetValue<string>())
    Assert.Equal(0, result["diagnosticsFileCount"].GetValue<int>())

[<Fact>]
let ``diagnosticsResponseForWorkspace reports warming and zero count during warmup`` () =
    let root = JsonObject()
    let result = diagnosticsResponseForWorkspace false 0 root

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal("warming", result["lspState"].GetValue<string>())
    Assert.Equal(0, result["diagnosticsFileCount"].GetValue<int>())

[<Fact>]
let ``diagnosticsResponseForWorkspace exposes all collected file payloads`` () =
    let root = JsonObject()
    root["file:///a.fs"] <- JsonArray() :> JsonNode
    root["file:///b.fs"] <- JsonArray() :> JsonNode
    let result = diagnosticsResponseForWorkspace true 2 root

    Assert.Equal("ready", result["lspState"].GetValue<string>())
    Assert.Equal(2, result["diagnosticsFileCount"].GetValue<int>())
    Assert.NotNull(result["result"]["file:///a.fs"])
    Assert.NotNull(result["result"]["file:///b.fs"])

[<Fact>]
let ``workspaceSymbolResponse flags symbolIndexReady=false for empty result inside warmup window`` () =
    let response: JsonNode = JsonArray() :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-1.0))
    let result = workspaceSymbolResponse response ready now (TimeSpan.FromSeconds 3.0)

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal("ready", result["lspState"].GetValue<string>())
    Assert.False(result["symbolIndexReady"].GetValue<bool>())

[<Fact>]
let ``workspaceSymbolResponse flags symbolIndexReady=true for non-empty result`` () =
    let response: JsonNode = JsonArray(JsonValue.Create("hit") :> JsonNode) :> JsonNode
    let now = DateTimeOffset.UtcNow
    let ready = ValueSome(now.AddSeconds(-1.0))
    let result = workspaceSymbolResponse response ready now (TimeSpan.FromSeconds 3.0)

    Assert.True(result["symbolIndexReady"].GetValue<bool>())
    Assert.NotNull(result["result"])
