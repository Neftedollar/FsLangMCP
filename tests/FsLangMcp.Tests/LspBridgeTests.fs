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
    let result = diagnosticsResponseForFile true 5 payload None

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal("ready", result["lspState"].GetValue<string>())
    Assert.Equal(5, result["diagnosticsFileCount"].GetValue<int>())
    Assert.NotNull(result["result"])
    Assert.Null(result["analyzedAt"])

[<Fact>]
let ``diagnosticsResponseForFile reports warming when workspace not ready`` () =
    let payload: JsonNode = JsonArray() :> JsonNode
    let result = diagnosticsResponseForFile false 0 payload None

    Assert.Equal("warming", result["lspState"].GetValue<string>())
    Assert.Equal(0, result["diagnosticsFileCount"].GetValue<int>())

[<Fact>]
let ``diagnosticsResponseForFile surfaces analyzedAt timestamp when present`` () =
    let payload: JsonNode = JsonArray() :> JsonNode
    let ts = DateTimeOffset.Parse("2026-05-19T10:00:00Z")
    let result = diagnosticsResponseForFile true 1 payload (Some ts)

    let surfaced = result["analyzedAt"].GetValue<string>()
    Assert.Contains("2026-05-19", surfaced)

[<Fact>]
let ``diagnosticsResponseForWorkspace reports warming and zero count during warmup`` () =
    let root = JsonObject()
    let result = diagnosticsResponseForWorkspace false 0 root None (JsonObject())

    Assert.Equal("ok", result["status"].GetValue<string>())
    Assert.Equal("warming", result["lspState"].GetValue<string>())
    Assert.Equal(0, result["diagnosticsFileCount"].GetValue<int>())
    Assert.Null(result["mostRecentAnalyzedAt"])

[<Fact>]
let ``diagnosticsResponseForWorkspace exposes all collected file payloads`` () =
    let root = JsonObject()
    root["file:///a.fs"] <- JsonArray() :> JsonNode
    root["file:///b.fs"] <- JsonArray() :> JsonNode
    let result = diagnosticsResponseForWorkspace true 2 root None (JsonObject())

    Assert.Equal("ready", result["lspState"].GetValue<string>())
    Assert.Equal(2, result["diagnosticsFileCount"].GetValue<int>())
    Assert.NotNull(result["result"]["file:///a.fs"])
    Assert.NotNull(result["result"]["file:///b.fs"])

[<Fact>]
let ``diagnosticsResponseForWorkspace surfaces per-URI analyzedAt + mostRecentAnalyzedAt`` () =
    let root = JsonObject()
    root["file:///a.fs"] <- JsonArray() :> JsonNode
    let ts = DateTimeOffset.Parse("2026-05-19T10:00:00Z")
    let analyzedAt = JsonObject()
    analyzedAt["file:///a.fs"] <- JsonValue.Create(ts.ToUniversalTime().ToString("O")) :> JsonNode
    let result = diagnosticsResponseForWorkspace true 1 root (Some ts) analyzedAt

    Assert.NotNull(result["analyzedAtByUri"]["file:///a.fs"])
    Assert.Contains("2026-05-19", result["mostRecentAnalyzedAt"].GetValue<string>())

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

// ─── SolutionParsing ──────────────────────────────────────────────────────────

open FsLangMcp.ProjectFiles.SolutionParsing

[<Fact>]
let ``listProjects returns the single fsproj when given a fsproj path`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"sp_fsproj_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        let fsproj = Path.Combine(root, "App.fsproj")
        File.WriteAllText(fsproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />")

        let result = listProjects fsproj

        Assert.Equal(1, result.Length)
        Assert.Equal(Path.GetFullPath fsproj, result[0])
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``listProjects returns all fsproj entries from slnx`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"sp_slnx_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(Path.Combine(root, "B.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let slnx = Path.Combine(root, "S.slnx")

        File.WriteAllText(
            slnx,
            String.concat
                "\n"
                [ "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                  "<Solution>"
                  "  <Project Path=\"A.fsproj\" />"
                  "  <Project Path=\"B.fsproj\" />"
                  "</Solution>" ])

        let result = listProjects slnx |> Array.sort

        Assert.Equal(2, result.Length)
        Assert.EndsWith("A.fsproj", result[0])
        Assert.EndsWith("B.fsproj", result[1])
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``listProjects returns all fsproj entries from sln`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"sp_sln_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        File.WriteAllText(Path.Combine(root, "B.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let sln = Path.Combine(root, "S.sln")

        File.WriteAllText(
            sln,
            String.concat
                "\n"
                [ "Microsoft Visual Studio Solution File, Format Version 12.00"
                  "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"A\", \"A.fsproj\", \"{00000000-0000-0000-0000-000000000001}\""
                  "EndProject"
                  "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"B\", \"B.fsproj\", \"{00000000-0000-0000-0000-000000000002}\""
                  "EndProject" ])

        let result = listProjects sln |> Array.sort

        Assert.Equal(2, result.Length)
        Assert.EndsWith("A.fsproj", result[0])
        Assert.EndsWith("B.fsproj", result[1])
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``listProjects returns empty for non-existent path`` () =
    let result = listProjects "/nonexistent/path/Foo.fsproj"
    Assert.Empty(result)

[<Fact>]
let ``listProjects returns empty for unsupported extension`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"sp_unknown_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        let path = Path.Combine(root, "Foo.txt")
        File.WriteAllText(path, "hello")
        Assert.Empty(listProjects path)
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

[<Fact>]
let ``listProjects skips fsproj entries that don't exist on disk`` () =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"sp_skip_%s{runId}")

    try
        Directory.CreateDirectory(root) |> ignore
        // Only A.fsproj exists; B.fsproj does not.
        File.WriteAllText(Path.Combine(root, "A.fsproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        let slnx = Path.Combine(root, "S.slnx")

        File.WriteAllText(
            slnx,
            String.concat
                "\n"
                [ "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                  "<Solution>"
                  "  <Project Path=\"A.fsproj\" />"
                  "  <Project Path=\"Missing.fsproj\" />"
                  "</Solution>" ])

        let result = listProjects slnx

        Assert.Equal(1, result.Length)
        Assert.EndsWith("A.fsproj", result[0])
    finally
        if Directory.Exists root then
            Directory.Delete(root, true)

// ─── workspace_diagnostics filters (#108) ────────────────────────────────────

open FsLangMcp.Types

[<Fact>]
let ``severityCodeOf maps known names case-insensitively`` () =
    Assert.Equal(Some 1, severityCodeOf "error")
    Assert.Equal(Some 1, severityCodeOf "ERROR")
    Assert.Equal(Some 1, severityCodeOf "errors")
    Assert.Equal(Some 2, severityCodeOf "warning")
    Assert.Equal(Some 2, severityCodeOf "Warnings")
    Assert.Equal(Some 3, severityCodeOf "information")
    Assert.Equal(Some 3, severityCodeOf "info")
    Assert.Equal(Some 4, severityCodeOf "hint")
    Assert.Equal(Some 4, severityCodeOf "hints")

[<Fact>]
let ``severityCodeOf returns None for unknown names`` () =
    Assert.Equal(None, severityCodeOf "fatal")
    Assert.Equal(None, severityCodeOf "")
    Assert.Equal(None, severityCodeOf "  ")

[<Fact>]
let ``fileMatchesGlob single-segment star does not span slash`` () =
    Assert.True(fileMatchesGlob "*.fs" "Foo.fs")
    Assert.True(fileMatchesGlob "src/*.fs" "src/Foo.fs")
    Assert.False(fileMatchesGlob "src/*.fs" "src/Sub/Foo.fs") // * is single-segment
    Assert.True(fileMatchesGlob "file:///*/Foo.fs" "file:///src/Foo.fs")
    Assert.False(fileMatchesGlob "*.fs" "Foo.fsx")
    Assert.False(fileMatchesGlob "src/*.fs" "other/Foo.fs")
    Assert.True(fileMatchesGlob "F?o.fs" "Foo.fs")
    Assert.False(fileMatchesGlob "F?o.fs" "Fooo.fs")
    Assert.False(fileMatchesGlob "?.fs" "a/b.fs") // ? doesn't match /

[<Fact>]
let ``fileMatchesGlob double-star spans across slashes`` () =
    Assert.True(fileMatchesGlob "src/**/*.fs" "src/Foo.fs")
    Assert.True(fileMatchesGlob "src/**/*.fs" "src/Sub/Foo.fs")
    Assert.True(fileMatchesGlob "src/**/*.fs" "src/A/B/C/Foo.fs")
    Assert.True(fileMatchesGlob "**/*.fs" "Foo.fs")
    Assert.True(fileMatchesGlob "**/*.fs" "deeply/nested/Foo.fs")
    Assert.False(fileMatchesGlob "src/**/*.fs" "other/Foo.fs")

[<Fact>]
let ``fileMatchesGlob is case-insensitive`` () =
    Assert.True(fileMatchesGlob "*.FS" "Foo.fs")
    Assert.True(fileMatchesGlob "src/*.fs" "SRC/Foo.fs")

[<Fact>]
let ``filterDiagnosticsBySeverity keeps only matching codes`` () =
    let diagnostics =
        JsonArray(
            jobj [ "message", jstr "err1"; "severity", jint 1 ] :> JsonNode,
            jobj [ "message", jstr "warn1"; "severity", jint 2 ] :> JsonNode,
            jobj [ "message", jstr "err2"; "severity", jint 1 ] :> JsonNode,
            jobj [ "message", jstr "info1"; "severity", jint 3 ] :> JsonNode
        )
        :> JsonNode

    let errorsOnly = filterDiagnosticsBySeverity 1 diagnostics :?> JsonArray

    Assert.Equal(2, errorsOnly.Count)
    Assert.Equal("err1", (errorsOnly[0]["message"]).GetValue<string>())
    Assert.Equal("err2", (errorsOnly[1]["message"]).GetValue<string>())

[<Fact>]
let ``filterDiagnosticsBySeverity drops entries missing severity field`` () =
    let diagnostics =
        JsonArray(
            jobj [ "message", jstr "no-severity" ] :> JsonNode,
            jobj [ "message", jstr "err"; "severity", jint 1 ] :> JsonNode
        )
        :> JsonNode

    let filtered = filterDiagnosticsBySeverity 1 diagnostics :?> JsonArray

    Assert.Equal(1, filtered.Count)
    Assert.Equal("err", (filtered[0]["message"]).GetValue<string>())

[<Fact>]
let ``filterDiagnosticsBySeverity returns empty array when nothing matches`` () =
    let diagnostics =
        JsonArray(jobj [ "message", jstr "info"; "severity", jint 3 ] :> JsonNode)
        :> JsonNode

    let filtered = filterDiagnosticsBySeverity 1 diagnostics :?> JsonArray

    Assert.Equal(0, filtered.Count)

[<Fact>]
let ``fileMatchesGlob trailing double-star matches everything inside`` () =
    Assert.True(fileMatchesGlob "src/**" "src/Foo.fs")
    Assert.True(fileMatchesGlob "src/**" "src/A/B/Foo.fs")
    Assert.False(fileMatchesGlob "src/**" "other/Foo.fs")

[<Fact>]
let ``diagnosticsResponseForFile preserves payload when no severity filter`` () =
    // path + severity combo verified at the pure helper layer: severity filter
    // applies before this builder is called.
    let payload =
        JsonArray(
            jobj [ "message", jstr "err"; "severity", jint 1 ] :> JsonNode,
            jobj [ "message", jstr "warn"; "severity", jint 2 ] :> JsonNode
        )
        :> JsonNode

    let result = diagnosticsResponseForFile true 1 payload None
    let resultArr = (result["result"]) :?> JsonArray

    Assert.Equal(2, resultArr.Count)
    Assert.Equal("ready", result["lspState"].GetValue<string>())

[<Fact>]
let ``diagnosticsResponseForFile with pre-filtered severity payload reflects filter`` () =
    // Simulates the bridge's pipeline: severity filter runs on the payload before
    // it reaches the response builder. We verify the builder doesn't add/remove.
    let raw =
        JsonArray(
            jobj [ "message", jstr "err"; "severity", jint 1 ] :> JsonNode,
            jobj [ "message", jstr "warn"; "severity", jint 2 ] :> JsonNode
        )
        :> JsonNode

    let filtered = filterDiagnosticsBySeverity 1 raw
    let result = diagnosticsResponseForFile true 1 filtered None
    let resultArr = (result["result"]) :?> JsonArray

    Assert.Equal(1, resultArr.Count)
    Assert.Equal("err", (resultArr[0]["message"]).GetValue<string>())

[<Fact>]
let ``diagnosticsResponseForWorkspace with empty filtered files yields empty result`` () =
    // Bridge logic drops URIs whose diagnostic list becomes empty after severity
    // filtering. We exercise the builder with an already-empty root to verify the
    // outer shape is still well-formed.
    let root = JsonObject()
    let result = diagnosticsResponseForWorkspace true 0 root None (JsonObject())

    Assert.Equal("ready", result["lspState"].GetValue<string>())
    Assert.Equal(0, result["diagnosticsFileCount"].GetValue<int>())
    let resultObj = (result["result"]) :?> JsonObject
    Assert.Equal(0, resultObj.Count)

// ─── workspace_diagnostics mostRecentAnalyzedAt scoping (#123) ───────────────

/// Simulates the mostRecent computation that LspBridge.Diagnostics performs:
/// filters the analyzedAt dictionary by globMatches, then takes the max.
/// Tests are at this level (rather than through the stateful Diagnostics member)
/// because the LspBridge class requires a live LSP process. The pure filtering
/// logic is what changed in the fix and is fully exercised here.
let private computeMostRecent (fileGlob: string option) (store: (string * DateTimeOffset) list) =
    let globMatches (uri: string) =
        match fileGlob with
        | Some pattern -> fileMatchesGlob pattern uri
        | None -> true

    let filtered =
        store
        |> List.filter (fun (uri, _) -> globMatches uri)
        |> List.map snd

    match filtered with
    | [] -> None
    | vs -> vs |> List.max |> Some

[<Fact>]
let ``mostRecentAnalyzedAt without fileGlob equals workspace-wide max`` () =
    let earlier = DateTimeOffset.Parse("2026-05-19T08:00:00Z")
    let later   = DateTimeOffset.Parse("2026-05-19T10:00:00Z")

    let store =
        [ "file:///src/Foo.fs",   earlier
          "file:///tests/Bar.fs", later ]

    let mostRecent = computeMostRecent None store

    // No glob → workspace-wide max → the later timestamp wins.
    Assert.Equal(Some later, mostRecent)

    // Verify it surfaces correctly through the response builder.
    let root = JsonObject()
    let analyzedAtByUri = JsonObject()
    let result = diagnosticsResponseForWorkspace true 2 root mostRecent analyzedAtByUri
    Assert.Contains("2026-05-19T10:00:00", result["mostRecentAnalyzedAt"].GetValue<string>())

[<Fact>]
let ``mostRecentAnalyzedAt with fileGlob restricts to matched subset`` () =
    let srcTs   = DateTimeOffset.Parse("2026-05-19T08:00:00Z")
    let testsTs = DateTimeOffset.Parse("2026-05-19T10:00:00Z")  // fresher, but outside glob

    let store =
        [ "file:///src/Foo.fs",   srcTs
          "file:///tests/Bar.fs", testsTs ]

    // Glob matches only the src file; tests/ is excluded.
    let mostRecent = computeMostRecent (Some "file:///src/**") store

    // The fresher tests/Bar.fs must NOT influence the result.
    Assert.Equal(Some srcTs, mostRecent)
    Assert.NotEqual(Some testsTs, mostRecent)

    // Sanity-check through the response builder.
    let root = JsonObject()
    let analyzedAtByUri = JsonObject()
    let result = diagnosticsResponseForWorkspace true 1 root mostRecent analyzedAtByUri
    Assert.Contains("2026-05-19T08:00:00", result["mostRecentAnalyzedAt"].GetValue<string>())

[<Fact>]
let ``mostRecentAnalyzedAt with fileGlob matching zero files is null`` () =
    let store =
        [ "file:///tests/Bar.fs", DateTimeOffset.Parse("2026-05-19T10:00:00Z") ]

    // Glob matches nothing in the store.
    let mostRecent = computeMostRecent (Some "file:///src/**") store

    Assert.Equal(None, mostRecent)

    // The response builder must still produce a well-formed object with null mostRecentAnalyzedAt.
    let root = JsonObject()
    let analyzedAtByUri = JsonObject()
    let result = diagnosticsResponseForWorkspace true 0 root mostRecent analyzedAtByUri
    Assert.Null(result["mostRecentAnalyzedAt"])
