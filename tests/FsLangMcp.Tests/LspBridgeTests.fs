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
