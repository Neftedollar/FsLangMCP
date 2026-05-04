module FsLangMcp.Tests.LspBridgeTests

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
