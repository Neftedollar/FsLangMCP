module FsLangMcp.Tests.FindCursorBoundaryTests

open System
open System.IO
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open FsLangMcp.Cursor
open FsLangMcp.FcsBridge
open FsLangMcp.Types
open FsLangMcp.Tests.FindCursorIntegrationTests

let private args project : FindArgs =
    { query = "target"; kind = Some "symbol"; scope = Some "project"
      exact = Some true; ``member`` = None; field = None; path = None
      line = None; word = None; occurrence = None; character = None
      contextLines = Some 0; includeDeclaration = Some true; includeInfo = Some false
      includePerProject = Some false; includeSiteTypes = Some false
      projectPath = Some project; maxResults = Some 1; timeoutMs = Some 30_000; cursor = None }

let private text (node: JsonNode) (key: string) = node[key].GetValue<string>()

let private rejected kind restart (node: JsonNode) =
    Assert.Equal("invalid_cursor", text node "status")
    Assert.Equal(kind, text node "errorKind")
    Assert.Equal(restart, node["paginationRestartRequired"].GetValue<bool>())
    Assert.Equal(not restart, node["retrySameCursor"].GetValue<bool>())
    Assert.Null(node["nextCursor"])
    Assert.Empty(node["sites"].AsArray())
    Assert.True(renderedLength node <= FindResponseBudget.MaxSerializedChars)

let private solution extension members =
    if extension = "slnx" then
        "<Solution>" + (members |> List.map (fun name -> $"<Project Path=\"{name}/{name}.fsproj\" />") |> String.concat "") + "</Solution>"
    else
        let entries =
            members |> List.mapi (fun index name ->
                $"Project(\"{{F2A71F9B-5D33-465A-A702-920D77279786}}\") = \"{name}\", \"{name}/{name}.fsproj\", \"{{00000000-0000-0000-0000-{index + 1:D12}}}\"\nEndProject")
        String.concat "\n" ([ "Microsoft Visual Studio Solution File, Format Version 12.00" ] @ entries @ [ "Global"; "EndGlobal" ])

type FindCursorBoundaryTests(fixture: FindCursorFixture) =
    interface IClassFixture<FindCursorFixture>

    [<Theory>]
    [<InlineData("snapshot-start", false)>]
    [<InlineData("snapshot-start", true)>]
    [<InlineData("snapshot-project-ledger", false)>]
    [<InlineData("snapshot-project-ledger", true)>]
    [<InlineData("snapshot-project-order", false)>]
    [<InlineData("snapshot-project-order", true)>]
    [<InlineData("snapshot-sites", false)>]
    [<InlineData("snapshot-sites", true)>]
    [<InlineData("snapshot-json", false)>]
    [<InlineData("snapshot-json", true)>]
    [<InlineData("snapshot-complete", false)>]
    [<InlineData("snapshot-complete", true)>]
    member _.``snapshot construction expiry never delivers an unvalidated page``(phase: string, continuation: bool) : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let request = args fixture.AProject
            let! first = FcsBridge().Find(request)
            let next =
                if continuation then { request with cursor = Some(text first "nextCursor") }
                else request

            let expired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let deadline = FindRequestDeadline(30_000, responseExpirySignal = expired.Task)
            let mutable reached = false
            let bridge = FcsBridge(findResponseConstructionBeforeStepOverride = (fun step _ ->
                if step = phase then
                    reached <- true
                    expired.TrySetResult(()) |> ignore))

            let! result = bridge.FindWithinDeadline(next, deadline, CancellationToken.None, ignore, None)
            Assert.True(reached, $"The {phase} control must reach the actual snapshot builder.")
            Assert.Empty(result["sites"].AsArray())
            Assert.Null(result["nextCursor"])

            if continuation then
                rejected "cursor_validation_incomplete" false result
            else
                Assert.Equal("find_response_timeout", text result "errorKind")
                Assert.True(result["paginationRestartRequired"].GetValue<bool>())
                Assert.False(result["retrySameCursor"].GetValue<bool>())
                Assert.True(result["truncated"].GetValue<bool>())
                Assert.False((result["resolution"]["complete"]).GetValue<bool>())

            let! retried = FcsBridge().Find(next)
            Assert.Equal("succeeded", text retried "status")
            Assert.Equal((if continuation then 1 else 0), retried["pageOffset"].GetValue<int>())
            Assert.Single(retried["sites"].AsArray()) |> ignore
        }

    [<Theory>]
    [<InlineData("slnx", "workspace")>]
    [<InlineData("slnx", "auto")>]
    [<InlineData("slnx", "project")>]
    [<InlineData("slnx", "file")>]
    [<InlineData("sln", "workspace")>]
    [<InlineData("sln", "auto")>]
    [<InlineData("sln", "project")>]
    [<InlineData("sln", "file")>]
    member _.``lost last or selected declared target invalidates an unchanged continuation``(extension: string, scope: string) : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let target = Path.Combine(fixture.Root, $"Membership-{scope}.{extension}")
            File.WriteAllText(target, solution extension [ "A"; "B" ])
            let scoped = scope = "project" || scope = "file"
            let request =
                { args target with scope = Some scope
                                   path = if scoped then Some fixture.ASource else None }
            let! first = FcsBridge().Find(request)
            let cursor = text first "nextCursor"
            let! unchanged = FcsBridge().Find({ request with cursor = Some cursor })
            Assert.Equal("succeeded", text unchanged "status")
            Assert.Equal(1, unchanged["pageOffset"].GetValue<int>())

            File.WriteAllText(target, solution extension (if scoped then [ "B" ] else []))
            // The source and both project files still exist; only declared membership changed.
            Assert.True(File.Exists(fixture.AProject) && File.Exists(fixture.BProject) && File.Exists(fixture.ASource))
            let! initial = FcsBridge().Find(request)
            Assert.Equal("invalid_args", text initial "status")
            let! malformed = FcsBridge().Find({ request with cursor = Some "not base64" })
            rejected "cursor_malformed" true malformed
            let! mismatch = FcsBridge().Find({ request with query = "a1"; cursor = Some cursor })
            rejected "cursor_query_mismatch" true mismatch
            let! missingContext = FcsBridge().Find({ request with projectPath = None; path = None; scope = Some "workspace"; cursor = Some cursor })
            Assert.Equal("invalid_args", text missingContext "status")
            let! stale = FcsBridge().Find({ request with cursor = Some cursor })
            rejected "cursor_stale" true stale
        }

    [<Theory>]
    [<InlineData("snapshot", true, "before")>]
    [<InlineData("snapshot", false, "before")>]
    [<InlineData("offset", true, "before")>]
    [<InlineData("offset", false, "before")>]
    [<InlineData("query", true, "before")>]
    [<InlineData("query", false, "before")>]
    [<InlineData("snapshot", true, "after")>]
    [<InlineData("snapshot", false, "after")>]
    [<InlineData("offset", true, "after")>]
    [<InlineData("offset", false, "after")>]
    [<InlineData("query", true, "after")>]
    [<InlineData("query", false, "after")>]
    member _.``final error serialization respects expiry only when it occurs``(reason: string, expire: bool, phase: string) : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let request = args fixture.AProject
            let! first = FcsBridge().Find(request)
            let cursor = text first "nextCursor"
            let payload = match tryDecodeFind cursor with Ok payload -> payload | Error error -> failwith error.Message
            let next, errorKind =
                match reason with
                | "snapshot" ->
                    { request with cursor = Some(encodeFindV2 payload.Offset payload.Query (String.replicate 43 "A")) }, "cursor_stale"
                | "offset" ->
                    { request with cursor = Some(encodeFindV2 Int32.MaxValue payload.Query payload.Snapshot) }, "cursor_out_of_range"
                | "query" -> { request with cursor = Some cursor; query = "a1" }, "cursor_query_mismatch"
                | _ -> failwith "Unexpected test case"
            let expired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let deadline = FindRequestDeadline(30_000, responseExpirySignal = expired.Task)
            let mutable measured = false
            let onMeasure step () =
                if step = phase then
                    measured <- true
                    if expire then expired.TrySetResult(()) |> ignore
            let bridge = FcsBridge(
                findFinalResponseBeforeMeasureOverride = onMeasure "before",
                findFinalResponseAfterMeasureOverride = onMeasure "after")
            let! result = bridge.FindWithinDeadline(next, deadline, CancellationToken.None, ignore, None)
            Assert.True(measured)
            rejected (if expire then "cursor_validation_incomplete" else errorKind) (not expire) result
            // Retrying without injected expiry must yield the actual validation result.
            let! retried = FcsBridge().Find(next)
            rejected errorKind true retried
        }

    [<Theory>]
    [<InlineData("malformed")>]
    [<InlineData("legacy")>]
    [<InlineData("other-tool")>]
    [<InlineData("invalid-args")>]
    [<InlineData("missing-context")>]
    [<InlineData("position-missing-path")>]
    [<InlineData("position-missing-line")>]
    member _.``final guard expiry does not promote invalid requests or rejected tokens to retryable continuations``(reason: string) : Task =
        task {
            let request = args fixture.AProject
            let! first = FcsBridge().Find(request)
            let cursor = text first "nextCursor"
            let next, expected =
                match reason with
                | "malformed" -> { request with cursor = Some "not base64" }, Some "cursor_malformed"
                | "legacy" -> { request with cursor = Some(encode 1) }, Some "cursor_version_unsupported"
                | "other-tool" ->
                    let raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor))
                    let other = raw.Replace("\"find\"", "\"other\"", StringComparison.Ordinal)
                    { request with cursor = Some(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(other))) }, Some "cursor_tool_mismatch"
                | "invalid-args" -> { request with cursor = Some cursor; maxResults = Some 0 }, None
                | "missing-context" -> { request with cursor = Some cursor; projectPath = None }, None
                | "position-missing-path" -> { request with cursor = Some cursor; kind = Some "position"; line = Some 1 }, None
                | "position-missing-line" -> { request with cursor = Some cursor; kind = Some "position"; path = Some fixture.ASource }, None
                | _ -> failwith "Unexpected test case"
            let expired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let deadline = FindRequestDeadline(30_000, responseExpirySignal = expired.Task)
            let mutable measured = false
            let bridge = FcsBridge(findFinalResponseBeforeMeasureOverride = (fun () ->
                measured <- true
                expired.TrySetResult(()) |> ignore))
            let! result = bridge.FindWithinDeadline(next, deadline, CancellationToken.None, ignore, None)
            Assert.True(measured)
            match expected with
            | Some errorKind -> rejected errorKind true result
            | None -> Assert.Equal("invalid_args", text result "status")
        }

    [<Fact>]
    member _.``already measured continuation pages do not reenter the final error guard``() : Task =
        task {
            let request = args fixture.AProject
            let! first = FcsBridge().Find(request)
            let cursor = text first "nextCursor"
            let expired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let deadline = FindRequestDeadline(30_000, responseExpirySignal = expired.Task)
            let mutable guardCalls = 0
            let onMeasure () =
                guardCalls <- guardCalls + 1
                expired.TrySetResult(()) |> ignore
            let bridge = FcsBridge(
                findFinalResponseBeforeMeasureOverride = onMeasure,
                findFinalResponseAfterMeasureOverride = onMeasure)
            let! result = bridge.FindWithinDeadline({ request with cursor = Some cursor }, deadline, CancellationToken.None, ignore, None)
            Assert.Equal(0, guardCalls)
            Assert.False(deadline.ResponseExpired)
            Assert.Equal("succeeded", text result "status")
            Assert.Equal(1, result["pageOffset"].GetValue<int>())
            Assert.Single(result["sites"].AsArray()) |> ignore
            Assert.True(renderedLength result <= FindResponseBudget.MaxSerializedChars)
        }

    [<Theory>]
    [<InlineData(true)>]
    [<InlineData(false)>]
    member _.``budget error construction obeys the same continuation deadline priority``(expire: bool) : Task =
        task {
            let request = args fixture.AProject
            let! first = FcsBridge().Find(request)
            let cursor = text first "nextCursor"
            let next = { request with cursor = Some cursor }
            let expired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let deadline = FindRequestDeadline(30_000, responseExpirySignal = expired.Task)
            let mutable measured = false
            let bridge = FcsBridge(
                findResponseBudgetCharsOverride = 1,
                findFinalResponseAfterMeasureOverride = (fun () ->
                    measured <- true
                    if expire then expired.TrySetResult(()) |> ignore))
            let! result = bridge.FindWithinDeadline(next, deadline, CancellationToken.None, ignore, None)
            Assert.True(measured)
            if expire then
                rejected "cursor_validation_incomplete" false result
            else
                Assert.Equal("aborted", text result "status")
                Assert.Equal("find_metadata_exceeds_response_budget", text result "errorKind")
                Assert.True(result["paginationRestartRequired"].GetValue<bool>())
                Assert.False(result["retrySameCursor"].GetValue<bool>())
                Assert.Empty(result["sites"].AsArray())
                Assert.Null(result["nextCursor"])
                Assert.True(renderedLength result <= FindResponseBudget.MaxSerializedChars)
            // The error-construction timeout delivered no page; the unchanged
            // token still walks the original stream under the production ceiling.
            let! retried = FcsBridge().Find(next)
            Assert.Equal("succeeded", text retried "status")
            Assert.Equal(1, retried["pageOffset"].GetValue<int>())
        }
