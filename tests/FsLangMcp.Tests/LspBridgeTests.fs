module FsLangMcp.Tests.LspBridgeTests

open System.Text.Json
open System.Text.Json.Nodes
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
let ``compile result classifier uses exitCode when available`` () =
    let response = JsonNode.Parse("""{"exitCode":0,"errors":[{"message":"ignored"}]}""")
    let status, exitCode, diagnosticsCount = CompileResult.classify response

    Assert.Equal("succeeded", status)
    Assert.Equal(Some 0, exitCode)
    Assert.Equal(Some 1, diagnosticsCount)

[<Fact>]
let ``compile result classifier uses diagnostics count when exitCode missing`` () =
    let response = JsonNode.Parse("""{"diagnostics":[{"message":"FS0039"}]}""")
    let status, exitCode, diagnosticsCount = CompileResult.classify response

    Assert.Equal("failed", status)
    Assert.Equal(None, exitCode)
    Assert.Equal(Some 1, diagnosticsCount)

[<Fact>]
let ``compile result classifier returns unknown for unrecognized payload`` () =
    let response = JsonNode.Parse("""{"message":"ok"}""")
    let status, exitCode, diagnosticsCount = CompileResult.classify response

    Assert.Equal("unknown", status)
    Assert.Equal(None, exitCode)
    Assert.Equal(None, diagnosticsCount)

[<Fact>]
let ``compile result detects missing fsac compile endpoint`` () =
    Assert.True(CompileResult.fsacCompileUnavailable "No method by the name 'fsharp/compile' is found.")
