module FsLangMcp.Tests.FindCursorReviewTests

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open FsLangMcp.FcsBridge
open FsLangMcp.Types
open FsLangMcp.Tests.FindCursorIntegrationTests
open FsLangMcp.Tests.FindTests

let private args project : FindArgs =
    { query = "target"; kind = Some "symbol"; scope = Some "project"
      exact = Some true; ``member`` = None; field = None; path = None
      line = None; word = None; occurrence = None; character = None
      contextLines = Some 0; includeDeclaration = Some true; includeInfo = Some false
      includePerProject = Some false; includeSiteTypes = Some false
      projectPath = Some project; maxResults = Some 1; timeoutMs = Some 30_000; cursor = None }

let private text (node: JsonNode) (key: string) = node[key].GetValue<string>()
let private rows (node: JsonNode) = node["sites"].AsArray() |> Seq.map _.ToJsonString() |> Seq.toArray

let private tryValue<'T> (key: string) (node: JsonNode) : 'T option =
    match node with
    | :? JsonObject as fields ->
        match fields[key] with
        | :? JsonValue as value ->
            match value.TryGetValue<'T>() with
            | true, result -> Some result
            | false, _ -> None
        | _ -> None
    | _ -> None

let private requireEnvelope condition message (node: JsonNode) =
    if not condition then
        let envelope = if isNull node then "<null>" else node.ToJsonString()
        raise (Xunit.Sdk.XunitException($"{message}\nFull find envelope: {envelope}"))

let private hasEmptySites (node: JsonNode) =
    match node with
    | :? JsonObject as fields ->
        match fields["sites"] with
        | :? JsonArray as sites -> sites.Count = 0
        | _ -> false
    | _ -> false

// Absent optional contract fields stay absent. Present null/mistyped values are
// not equivalent to a compatible typed value.
let private optionalValue<'T> key predicate (fields: JsonObject) =
    not (fields.ContainsKey key) || (tryValue<'T> key fields |> Option.exists predicate)

let private optionalEquals key expected fields = optionalValue key ((=) expected) fields

let private typedMetadata (fields: JsonObject) =
    ([ "retryable"; "paginationRestartRequired"; "retrySameCursor"; "resultSetComplete"; "truncated";
      "totalEstimateIsLowerBound"; "breakdownComplete"; "sitesTruncatedByBudget"; "responseTruncatedByBudget";
      "projectDiagnosticsCountComplete"; "projectDiagnosticsTruncated"; "projectDiagnosticsTruncatedByBudget";
      "perProjectTruncatedByBudget" ]
    |> List.forall (fun key -> optionalValue<bool> key (fun _ -> true) fields))
    && ([ "timeoutMs"; "responseConstructionAllowanceMs"; "elapsedMs"; "totalSites"; "matchedUseCount"
          "pageOffset"; "pageSize"; "returnedSiteCount"; "cursorAdvancedBy"; "responseBudgetChars"; "sweepElapsedMs"
          "projectsSwept"; "projectsRequested"; "projectsAnalyzed"; "projectsFailed"; "projectsMissing"
          "projectsTimedOut"; "projectsBusy"; "projectsNotStarted"; "projectDiagnosticsTotalCount"
          "projectDiagnosticsReturnedCount"; "perProjectTotalCount"; "perProjectReturnedCount" ]
        |> List.forall (fun key -> optionalValue<int> key (fun count -> count >= 0) fields))

let private isInitialResponseTimeout (node: JsonNode) =
    match node with
    | :? JsonObject as fields ->
        typedMetadata fields
        &&
        (tryValue<string> "status" node = Some "partial"
         || tryValue<string> "status" node = Some "unknown")
        && tryValue<string> "errorKind" node = Some "find_response_timeout"
        && tryValue<bool> "retryable" node = Some true
        && tryValue<bool> "paginationRestartRequired" node = Some true
        && tryValue<bool> "retrySameCursor" node = Some false
        && hasEmptySites node
        && tryValue<int> "pageOffset" node = Some 0
        && tryValue<int> "returnedSiteCount" node = Some 0
        && tryValue<int> "cursorAdvancedBy" node = Some 0
        && tryValue<bool> "resultSetComplete" node = Some false
        && tryValue<string> "deliveryStatus" node = Some "partial"
        && tryValue<bool> "truncated" node = Some true
        && tryValue<bool> "totalEstimateIsLowerBound" node = Some true
        && tryValue<string> "paginationIncompleteReason" node = Some "deadline_incomplete"
        && fields.ContainsKey("nextCursor")
        && isNull fields["nextCursor"]
        && isNull fields["errorCode"]
    | _ -> false

let private isContinuationValidationIncomplete cursor offset (node: JsonNode) =
    match cursor, node with
    | Some token, (:? JsonObject as fields) when not (String.IsNullOrWhiteSpace token) ->
        let unchangedProgress key expected =
            not (fields.ContainsKey key) || tryValue<int> key node = Some expected

        tryValue<string> "status" node = Some "invalid_cursor"
        && tryValue<string> "errorKind" node = Some "cursor_validation_incomplete"
        && tryValue<bool> "retryable" node = Some true
        && tryValue<bool> "paginationRestartRequired" node = Some false
        && tryValue<bool> "retrySameCursor" node = Some true
        && hasEmptySites node
        && fields.ContainsKey("nextCursor")
        && isNull fields["nextCursor"]
        && isNull fields["errorCode"]
        && unchangedProgress "pageOffset" offset
        && unchangedProgress "returnedSiteCount" 0
        && unchangedProgress "cursorAdvancedBy" 0
        && typedMetadata fields
        && optionalEquals "resultSetComplete" false fields
        && optionalEquals "deliveryStatus" "partial" fields
        && optionalEquals "truncated" true fields
        && optionalEquals "totalEstimateIsLowerBound" true fields
        && optionalEquals "paginationIncompleteReason" "deadline_incomplete" fields
    | _ -> false

let private isSucceededFind (node: JsonNode) =
    match node with
    | :? JsonObject as fields ->
        tryValue<string> "status" node = Some "succeeded"
        && isNull fields["errorKind"]
        && isNull fields["errorCode"]
        && typedMetadata fields
        && optionalEquals "paginationRestartRequired" false fields
        && optionalEquals "retrySameCursor" false fields
        && optionalEquals "retryable" false fields
        && optionalEquals "totalEstimateIsLowerBound" false fields
        && optionalEquals "deliveryStatus" "complete" fields
        && (not (fields.ContainsKey "paginationIncompleteReason") || isNull fields["paginationIncompleteReason"])
        && (tryValue<int> "pageOffset" node |> Option.exists (fun count -> count >= 0))
        && (tryValue<int> "totalSites" node |> Option.exists (fun count -> count > 0))
        && (tryValue<int> "returnedSiteCount" node |> Option.exists (fun count -> count > 0))
        && tryValue<int> "cursorAdvancedBy" node = tryValue<int> "returnedSiteCount" node
        && (tryValue<bool> "resultSetComplete" node |> Option.isSome)
        && (tryValue<bool> "truncated" node |> Option.isSome)
        && (match fields["coverage"] with | :? JsonObject as coverage -> tryValue<bool> "complete" coverage = Some true && typedMetadata coverage | _ -> false)
        && fields.ContainsKey "nextCursor"
        && (isNull fields["nextCursor"] || (tryValue<string> "nextCursor" node |> Option.exists (String.IsNullOrWhiteSpace >> not)))
        && (match fields["sites"] with | :? JsonArray as sites -> Some sites.Count = tryValue<int> "returnedSiteCount" node | _ -> false)
    | _ -> false

// Only a fully typed, zero-progress deadline envelope permits one identical
// request. A stale cursor, missing evidence, or a second expiry remains a failure.
let private successfulPageWithOneRetry emit description cursor offset (request: unit -> Task<JsonNode>) =
    task {
        let evidence = ResizeArray<string>()

        let attempt number =
            task {
                try
                    let! page = request ()
                    let envelope = if isNull page then "<null>" else page.ToJsonString()
                    evidence.Add($"{description} attempt={number}: {envelope}")
                    emit evidence[evidence.Count - 1]
                    return page
                with error ->
                    evidence.Add($"{description} attempt={number} threw: {error}")
                    return raise error
            }

        let fail () =
            let responses = String.concat "\n" evidence
            raise (Xunit.Sdk.XunitException($"Expected a successful find page.\n{responses}"))

        let! first = attempt 1
        if isSucceededFind first then
            return first
        else
            let retryable =
                match cursor with
                | None -> isInitialResponseTimeout first
                | Some _ -> isContinuationValidationIncomplete cursor offset first

            if not retryable then
                return fail ()
            else
                let! second = attempt 2
                if isSucceededFind second then return second else return fail ()
    }

[<NoEquality; NoComparison>]
type private CompleteFind =
    { FirstPage: JsonNode
      Envelopes: string array
      Rows: string array }

let private cursorIdentity offset token =
    let decoded = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(token)))
    requireEnvelope (tryValue<int> "v" decoded = Some 2 && tryValue<string> "tool" decoded = Some "find") "Expected a v2 find cursor." decoded
    requireEnvelope (tryValue<int> "offset" decoded = Some offset) "Cursor offset differs from delivered site count." decoded
    let query, snapshot = tryValue<string> "query" decoded, tryValue<string> "snapshot" decoded
    requireEnvelope (query |> Option.exists (fun value -> value.Length = 43)) "Cursor omitted a valid query identity." decoded
    requireEnvelope (snapshot |> Option.exists (fun value -> value.Length = 43)) "Cursor omitted a valid snapshot identity." decoded
    query.Value, snapshot.Value

let private collectCompletePositiveFind emit description expectedCount (request: string option -> Task<JsonNode>) =
    task {
        let collected = ResizeArray<string>()
        let envelopes = ResizeArray<string>()
        let seenCursors = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let mutable cursor = None
        let mutable firstPage = null
        let mutable identity = None
        let mutable complete = false
        let mutable pageNumber = 0

        while not complete do
            let offset = collected.Count
            let requestedCursor = cursor
            let! page =
                successfulPageWithOneRetry
                    emit
                    $"{description} page={pageNumber} offset={offset}"
                    requestedCursor
                    offset
                    (fun () -> request requestedCursor)

            if isNull firstPage then firstPage <- page
            envelopes.Add(page.ToJsonString())

            let pageRows =
                match page["sites"] with
                | :? JsonArray -> rows page
                | _ -> [||]

            let nextCursor = tryValue<string> "nextCursor" page
            let resultSetComplete = tryValue<bool> "resultSetComplete" page
            let coverageComplete = tryValue<bool> "complete" page["coverage"]

            requireEnvelope (pageRows.Length > 0) $"{description} returned no positive site evidence." page
            requireEnvelope (tryValue<int> "pageOffset" page = Some offset) $"{description} returned the wrong page offset." page
            requireEnvelope (tryValue<int> "returnedSiteCount" page = Some pageRows.Length) $"{description} returned an inconsistent site count." page
            requireEnvelope (tryValue<int> "cursorAdvancedBy" page = Some pageRows.Length) $"{description} did not advance by the delivered sites." page
            requireEnvelope (coverageComplete = Some true) $"{description} did not completely analyze project coverage." page
            requireEnvelope (expectedCount > 0 && tryValue<int> "totalSites" page = Some expectedCount) $"{description} disagrees with independent expected site count." page
            // resultSetComplete is per response, not an exhausted-traversal flag:
            // production requires offset zero AND every site in that one page.
            requireEnvelope (resultSetComplete = Some(offset = 0 && pageRows.Length = expectedCount)) $"{description} contradicted single-response completeness." page

            collected.AddRange(pageRows)

            match nextCursor with
            | Some token ->
                requireEnvelope (not (String.IsNullOrWhiteSpace token)) $"{description} returned a blank continuation cursor." page
                requireEnvelope (tryValue<bool> "truncated" page = Some true) $"{description} hid remaining sites." page
                requireEnvelope (collected.Count < expectedCount) $"{description} returned a cursor after all sites were delivered." page
                requireEnvelope (seenCursors.Add token) $"{description} repeated a continuation cursor." page
                let currentIdentity = cursorIdentity collected.Count token
                requireEnvelope (identity.IsNone || identity = Some currentIdentity) $"{description} changed query/snapshot while paginating." page
                identity <- Some currentIdentity
                cursor <- Some token
            | None ->
                let fields = page.AsObject()
                requireEnvelope (collected.Count = expectedCount && fields.ContainsKey("nextCursor") && isNull fields["nextCursor"]) $"{description} ended before all expected sites were delivered or returned a malformed cursor." page
                requireEnvelope (tryValue<bool> "truncated" page = Some false) $"{description} ended with unresolved site truncation." page
                complete <- true

            pageNumber <- pageNumber + 1
            requireEnvelope (pageNumber <= expectedCount) $"{description} exceeded the positive-site traversal bound." page

        requireEnvelope (collected.Count > 0) $"{description} completed without expected positive sites." firstPage
        requireEnvelope (Seq.distinct collected |> Seq.length = collected.Count) $"{description} repeated canonical site rows." firstPage

        return
            { FirstPage = firstPage
              Envelopes = envelopes.ToArray()
              Rows = collected.ToArray() }
    }

let private initialCursorWithPositiveEvidence emit description expectedCount (request: unit -> Task<JsonNode>) =
    task {
        let! page = successfulPageWithOneRetry emit description None 0 request
        let pageRows = match page["sites"] with | :? JsonArray -> rows page | _ -> [||]
        let cursor = tryValue<string> "nextCursor" page
        requireEnvelope (pageRows.Length = 1) $"{description} did not return exactly one site." page
        requireEnvelope (tryValue<int> "pageOffset" page = Some 0) $"{description} did not begin at offset zero." page
        requireEnvelope (tryValue<int> "returnedSiteCount" page = Some 1) $"{description} returned an inconsistent site count." page
        requireEnvelope (tryValue<int> "cursorAdvancedBy" page = Some 1) $"{description} did not advance by one site." page
        requireEnvelope (tryValue<bool> "complete" page["coverage"] = Some true) $"{description} lacked complete project coverage." page
        requireEnvelope (cursor |> Option.exists (String.IsNullOrWhiteSpace >> not)) $"{description} did not return the required continuation cursor." page
        requireEnvelope (tryValue<int> "totalSites" page = Some expectedCount && expectedCount > 1 && tryValue<bool> "resultSetComplete" page = Some false) $"{description} lacked the expected additional sites." page
        cursorIdentity 1 cursor.Value |> ignore
        return cursor.Value, page
    }

// Ground truth comes from the physical fixture, not from find's reported total.
let private expectedFixtureSites path =
    File.ReadAllLines(path)
    |> Array.mapi (fun index line ->
        [| for occurrence in System.Text.RegularExpressions.Regex.Matches(line, @"\btarget\b") ->
               Path.GetFullPath(path), index + 1, occurrence.Index, occurrence.Index + occurrence.Length |])
    |> Array.concat

let private assertFixtureSites expected (complete: CompleteFind) =
    let actual =
        complete.Rows |> Array.map (fun row ->
            let site = JsonNode.Parse(row)
            let range = site["range"]
            Path.GetFullPath(text site "file"), range["startLine"].GetValue<int>(), range["startColumn"].GetValue<int>(), range["endColumn"].GetValue<int>())
    Assert.True((expected = actual), String.concat "\n" complete.Envelopes)
    Assert.Equal<(string * int * int * int) array>(expected, actual)

let private assertRejected kind restart (node: JsonNode) =
    requireEnvelope (tryValue<string> "status" node = Some "invalid_cursor") "Expected an invalid cursor response." node
    requireEnvelope (tryValue<string> "errorKind" node = Some kind) $"Expected cursor error kind '{kind}'." node
    requireEnvelope (tryValue<bool> "paginationRestartRequired" node = Some restart) "Unexpected restart guidance." node
    requireEnvelope (tryValue<bool> "retrySameCursor" node = Some(not restart)) "Unexpected same-cursor retry guidance." node
    requireEnvelope (hasEmptySites node) "Rejected cursor response returned sites." node
    Assert.Equal("invalid_cursor", text node "status")
    Assert.Equal(kind, text node "errorKind")
    Assert.Equal(restart, node["paginationRestartRequired"].GetValue<bool>())
    Assert.Equal(not restart, node["retrySameCursor"].GetValue<bool>())
    Assert.Empty(node["sites"].AsArray())
    Assert.Null(node["nextCursor"])

let private overloadSource =
    String.concat "\n"
        [ "module CursorReview"
          ""
          "type C ="
          "    static member target (x: int) = x"
          "    static member target (x: string) = x"
          ""
          "let value = 1"
          "let call = C.target value"
          "let again = C.target 2" ]

type FindCursorPositiveEvidencePolicyTests() =
    [<Fact>]
    member _.``positive evidence policy rejects malformed expiry and repeated expiry without a third call``() : Task =
        task {
            let initial = JsonNode.Parse("""{"status":"partial","errorKind":"find_response_timeout","retryable":true,"paginationRestartRequired":true,"retrySameCursor":false,"sites":[],"pageOffset":0,"returnedSiteCount":0,"cursorAdvancedBy":0,"resultSetComplete":false,"deliveryStatus":"partial","truncated":true,"totalEstimateIsLowerBound":true,"paginationIncompleteReason":"deadline_incomplete","nextCursor":null}""")
            let continuation = JsonNode.Parse("""{"status":"invalid_cursor","errorKind":"cursor_validation_incomplete","retryable":true,"paginationRestartRequired":false,"retrySameCursor":true,"sites":[],"nextCursor":null}""")
            let healthy = JsonNode.Parse("""{"status":"succeeded","sites":[{"id":1}],"totalSites":1,"pageOffset":0,"returnedSiteCount":1,"cursorAdvancedBy":1,"coverage":{"complete":true},"resultSetComplete":true,"truncated":false,"nextCursor":null}""")
            for cursor, expiry in [ None, initial; Some "unchanged-input-token", continuation ] do
                let variants = ResizeArray<JsonNode>()
                for field in expiry.AsObject() do
                    let missing = expiry.DeepClone()
                    missing.AsObject().Remove(field.Key) |> ignore
                    variants.Add(missing)
                    let wrongType = expiry.DeepClone()
                    wrongType[field.Key] <- JsonValue.Create(42)
                    variants.Add(wrongType)
                for key, value in
                    [ "sites", "[{}]"; "pageOffset", "9"; "returnedSiteCount", "1"; "cursorAdvancedBy", "1"
                      "nextCursor", "\"unexpected\""; "errorCode", "\"unexpected\""
                      "status", "\"unknown\""; "errorKind", "\"cursor_stale\""; "errorKind", "\"find_timeout\""
                      "resultSetComplete", "true"; "deliveryStatus", "\"complete\""; "truncated", "false"
                      "totalEstimateIsLowerBound", "false"; "paginationIncompleteReason", "\"other\"" ] do
                    // Unknown is a legal initial expiry, but never continuation success.
                    if key <> "status" || cursor.IsSome then
                        let malformed = expiry.DeepClone()
                        malformed[key] <- JsonNode.Parse(value)
                        variants.Add(malformed)
                for malformed in variants do
                    let mutable calls = 0
                    let messages = ResizeArray<string>()
                    let! error = Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () ->
                        successfulPageWithOneRetry messages.Add "negative expiry proof" cursor 0 (fun () ->
                            calls <- calls + 1
                            Task.FromResult(if calls = 1 then malformed else healthy)) :> Task)
                    Assert.Equal(1, calls)
                    Assert.Contains(malformed.ToJsonString(), error.Message)
                let mutable calls = 0
                let! repeated = Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () ->
                    successfulPageWithOneRetry ignore "repeated expiry proof" cursor 0 (fun () ->
                        calls <- calls + 1
                        Task.FromResult(expiry)) :> Task)
                Assert.Equal(2, calls)
                Assert.Contains("attempt=1", repeated.Message)
                Assert.Contains("attempt=2", repeated.Message)
        }

    [<Fact>]
    member _.``positive success rejects missing mistyped and contradictory contract evidence``() : Task =
        task {
            let healthy = JsonNode.Parse("""{"status":"succeeded","sites":[{"id":1}],"totalSites":1,"pageOffset":0,"returnedSiteCount":1,"cursorAdvancedBy":1,"coverage":{"complete":true},"resultSetComplete":true,"truncated":false,"nextCursor":null}""")
            let variants = ResizeArray<JsonNode>()
            // All required positive fields: absence, null and wrong type are distinct.
            for key in [ "status"; "sites"; "totalSites"; "pageOffset"; "returnedSiteCount"; "cursorAdvancedBy"; "coverage"; "resultSetComplete"; "truncated"; "nextCursor" ] do
                let missing = healthy.DeepClone()
                missing.AsObject().Remove(key) |> ignore
                variants.Add(missing)
                if key <> "nextCursor" then
                    let nullValue = healthy.DeepClone()
                    nullValue[key] <- null
                    variants.Add(nullValue)
                let mistyped = healthy.DeepClone()
                mistyped[key] <- JsonValue.Create("wrong")
                variants.Add(mistyped)
            // Routing flags are absent in real success pages; if supplied they must
            // be typed false, never null, strings, numbers or contradictory true.
            for key in [ "paginationRestartRequired"; "retrySameCursor"; "retryable"; "totalEstimateIsLowerBound" ] do
                let compatible = healthy.DeepClone()
                compatible[key] <- JsonValue.Create(false)
                let! _ = collectCompletePositiveFind ignore "typed optional success flag" 1 (fun _ -> Task.FromResult(compatible))
                for value in [ "null"; "\"true\""; "42"; "true" ] do
                    let malformed = healthy.DeepClone()
                    malformed[key] <- JsonNode.Parse(value)
                    variants.Add(malformed)
            for key in [ "breakdownComplete"; "sitesTruncatedByBudget"; "responseTruncatedByBudget"; "projectDiagnosticsCountComplete"; "projectDiagnosticsTruncated"; "projectDiagnosticsTruncatedByBudget"; "perProjectTruncatedByBudget" ] do
                for value in [ "null"; "\"false\""; "42" ] do
                    let malformed = healthy.DeepClone()
                    malformed[key] <- JsonNode.Parse(value)
                    variants.Add(malformed)
            for key in [ "timeoutMs"; "responseConstructionAllowanceMs"; "elapsedMs"; "totalSites"; "matchedUseCount"; "pageOffset"; "pageSize"; "returnedSiteCount"; "cursorAdvancedBy"; "responseBudgetChars"; "sweepElapsedMs"; "projectsSwept"; "projectsRequested"; "projectsAnalyzed"; "projectsFailed"; "projectsMissing"; "projectsTimedOut"; "projectsBusy"; "projectsNotStarted"; "projectDiagnosticsTotalCount"; "projectDiagnosticsReturnedCount"; "perProjectTotalCount"; "perProjectReturnedCount" ] do
                for value in [ "null"; "\"0\""; "false"; "-1" ] do
                    let malformed = healthy.DeepClone()
                    malformed[key] <- JsonNode.Parse(value)
                    variants.Add(malformed)
            for malformed in variants do
                let mutable calls = 0
                let! failure = Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () ->
                    collectCompletePositiveFind ignore "typed positive proof" 1 (fun _ ->
                        calls <- calls + 1
                        Task.FromResult(if calls = 1 then malformed else healthy)) :> Task)
                Assert.Equal(1, calls)
                Assert.Contains(malformed.ToJsonString(), failure.Message)
            // Missing optional routing flags is the actual legal success schema.
            let! control = collectCompletePositiveFind ignore "absent optional success flags" 1 (fun _ -> Task.FromResult(healthy))
            Assert.Single(control.Rows) |> ignore
        }

    [<Fact>]
    member _.``complete positive traversal rejects missing empty skipped duplicate and contradictory pages``() : Task =
        task {
            let hash = String.replicate 43 "A"
            let token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"v\":2,\"tool\":\"find\",\"offset\":1,\"query\":\"{hash}\",\"snapshot\":\"{hash}\"}}"))
            let first = JsonNode.Parse($"{{\"status\":\"succeeded\",\"sites\":[{{\"id\":1}}],\"totalSites\":2,\"pageOffset\":0,\"returnedSiteCount\":1,\"cursorAdvancedBy\":1,\"coverage\":{{\"complete\":true}},\"resultSetComplete\":false,\"truncated\":true,\"nextCursor\":\"{token}\"}}")
            let last = JsonNode.Parse("""{"status":"succeeded","sites":[{"id":2}],"totalSites":2,"pageOffset":1,"returnedSiteCount":1,"cursorAdvancedBy":1,"coverage":{"complete":true},"resultSetComplete":false,"truncated":false,"nextCursor":null}""")
            let messages = ResizeArray<string>()
            let mutable calls = 0
            let! complete = collectCompletePositiveFind messages.Add "healthy two-page control" 2 (fun cursor ->
                calls <- calls + 1
                Assert.Equal<string option>((if calls = 1 then None else Some token), cursor)
                Task.FromResult(if calls = 1 then first else last))
            Assert.Equal(2, complete.Rows.Length)
            for key, value in
                [ "sites", "[]"; "sites", "null"; "sites", "[{\"id\":1}]"
                  "pageOffset", "0"; "returnedSiteCount", "0"; "cursorAdvancedBy", "0"
                  "totalSites", "1"; "coverage", "{\"complete\":false}"
                  "resultSetComplete", "true"; "truncated", "true"; "nextCursor", "42"
                  "errorKind", "\"find_response_timeout\""; "paginationRestartRequired", "true" ] do
                let malformed = last.DeepClone()
                malformed[key] <- JsonNode.Parse(value)
                let mutable attempts = 0
                let! error = Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () ->
                    collectCompletePositiveFind ignore "invalid complete page" 2 (fun _ ->
                        attempts <- attempts + 1
                        Task.FromResult(if attempts = 1 then first else malformed)) :> Task)
                Assert.Equal(2, attempts)
                Assert.Contains("find", error.Message, StringComparison.OrdinalIgnoreCase)
            let incomplete = last.DeepClone()
            incomplete.AsObject().Remove("nextCursor") |> ignore
            let mutable requests = 0
            let! _ = Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () ->
                collectCompletePositiveFind ignore "missing terminal cursor" 2 (fun _ ->
                    requests <- requests + 1
                    Task.FromResult(if requests = 1 then first else incomplete)) :> Task)
            Assert.Equal(2, requests)
            // Changing either opaque identity on a minted continuation is not tolerated.
            for key in [ "query"; "snapshot"; "offset" ] do
                let decoded = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(token)))
                decoded[key] <- if key = "offset" then JsonValue.Create(7) :> JsonNode else JsonValue.Create(String.replicate 43 "B") :> JsonNode
                let malformed = first.DeepClone()
                malformed["pageOffset"] <- JsonValue.Create(1)
                malformed["totalSites"] <- JsonValue.Create(3)
                malformed["nextCursor"] <- JsonValue.Create(Convert.ToBase64String(Encoding.UTF8.GetBytes(decoded.ToJsonString())))
                let beginning = first.DeepClone()
                beginning["totalSites"] <- JsonValue.Create(3)
                let mutable attempts = 0
                let! _ = Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () ->
                    collectCompletePositiveFind ignore "changed cursor identity" 3 (fun _ ->
                        attempts <- attempts + 1
                        Task.FromResult(if attempts = 1 then beginning else malformed)) :> Task)
                Assert.Equal(2, attempts)
        }

type FindCursorReviewTests(fixture: FindCursorFixture, output: ITestOutputHelper) =
    interface IClassFixture<FindCursorFixture>

    [<Fact>]
    member _.``production malformed version envelopes are restartable not infrastructure exceptions``() : Task =
        task {
            let hash = String.replicate 43 "A"
            for version in [ "\"2\""; "null"; "true"; "false"; "{}"; "[]" ] do
                let json = $"{{\"v\":{version},\"tool\":\"find\",\"offset\":1,\"query\":\"{hash}\",\"snapshot\":\"{hash}\"}}"
                let cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes json)
                let! result = FcsBridge().Find({ args fixture.AProject with cursor = Some cursor })
                assertRejected "cursor_malformed" true result
        }

    [<Theory>]
    [<InlineData("response-planning")>]
    [<InlineData("response-planning-complete")>]
    [<InlineData("response-serialization")>]
    [<InlineData("response-serialization-complete")>]
    member _.``late continuation expiry retains the same cursor retry route``(targetPhase: string) : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let request = args fixture.AProject
            let! first = FcsBridge().Find(request)
            let cursor = text first "nextCursor"
            let expired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable triggered = false
            let bridge =
                FcsBridge(
                    findResponseDeadlineSignalOverride = (fun () -> expired.Task :> Task),
                    findResponseConstructionBeforeStepOverride = (fun phase _ ->
                        if phase = targetPhase then
                            triggered <- true
                            expired.TrySetResult(()) |> ignore))
            let! timed = bridge.Find({ request with cursor = Some cursor })
            Assert.True(triggered, targetPhase)
            assertRejected "cursor_validation_incomplete" false timed
            let! retried = FcsBridge().Find({ request with cursor = Some cursor; maxResults = Some 100; timeoutMs = Some 60_000 })
            let! complete = FcsBridge().Find({ request with maxResults = Some 100 })
            Assert.Equal<string array>(rows complete, Array.append (rows first) (rows retried))
        }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``complete four-site diagnostic traversal recovers only the forced deadline page``(continuationExpiry: bool) : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let originalSource, originalProject = File.ReadAllText(fixture.ASource), File.ReadAllText(fixture.AProject)
            let requestedCursors = ResizeArray<string option>()
            let expected = expectedFixtureSites fixture.ASource
            Assert.Equal(4, expected.Length)
            let mutable expired = false
            let mutable expireThisRequest = false
            let mutable signal = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let bridge =
                FcsBridge(
                    findResponseDeadlineSignalOverride = (fun () ->
                        signal <- TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                        signal.Task :> Task),
                    findResponseConstructionBeforeStepOverride = (fun phase _ ->
                        if expireThisRequest && phase = "response-planning" then
                            expired <- true
                            signal.TrySetResult(()) |> ignore))
            let requests = ResizeArray<FindArgs>()
            let ledger = ResizeArray<string>()
            let mutable failedRequest: (FindArgs * string array) option = None
            try
                File.WriteAllText(fixture.AProject, originalProject.Replace("</PropertyGroup>", "<OtherFlags>--maxerrors:500</OtherFlags></PropertyGroup>", StringComparison.Ordinal))
                File.WriteAllText(fixture.ASource, originalSource + "\n" + String.concat "\n" [ for index in 0 .. 204 -> $"let diagnostic{index:D3} = missing{index:D3}" ])
                let request = { args fixture.AProject with maxResults = Some 1 }
                let messages = ResizeArray<string>()
                let emit message = messages.Add(message); output.WriteLine(message)
                let! complete =
                    collectCompletePositiveFind emit "forced diagnostic response expiry" expected.Length (fun cursor ->
                        task {
                            requestedCursors.Add cursor
                            let currentRequest =
                                match failedRequest with
                                | Some(previous, previousLedger) ->
                                    Assert.Equal<string option>(previous.cursor, cursor)
                                    Assert.Equal<string array>(previousLedger, ledger.ToArray())
                                    previous // exactly the same immutable request instance
                                | None -> { request with cursor = cursor }
                            requests.Add(currentRequest)
                            expireThisRequest <- not expired && cursor.IsSome = continuationExpiry
                            let! page = bridge.Find(currentRequest)
                            if isSucceededFind page then
                                Assert.Equal(ledger.Count, page["pageOffset"].GetValue<int>())
                                ledger.AddRange(rows page)
                                failedRequest <- None
                            else
                                Assert.Empty(page["sites"].AsArray())
                                failedRequest <- Some(currentRequest, ledger.ToArray())
                            return page
                        })
                Assert.True(expired)
                Assert.Equal(5, requestedCursors.Count) // four advancing pages, one zero-progress expiry
                let failedIndex = if continuationExpiry then 1 else 0
                Assert.Equal<string option>(requestedCursors[failedIndex], requestedCursors[failedIndex + 1])
                Assert.Same(requests[failedIndex], requests[failedIndex + 1])
                Assert.Equal(5, requests.Count)
                Assert.Contains(messages, fun message -> message.Contains((if continuationExpiry then "cursor_validation_incomplete" else "find_response_timeout"), StringComparison.Ordinal))
                Assert.Equal(expected.Length, complete.Rows.Length)
                Assert.Equal<string array>(complete.Rows, ledger.ToArray())
                assertFixtureSites expected complete
            finally
                File.WriteAllText(fixture.ASource, originalSource)
                File.WriteAllText(fixture.AProject, originalProject)
        }

    [<Fact>]
    member _.``position overload retyping invalidates identical full canonical site rows``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            try
                File.WriteAllText(fixture.ASource, overloadSource)
                let request =
                    { args fixture.AProject with
                        query = "requested-position"; kind = Some "position"
                        path = Some fixture.ASource; line = Some 7; word = Some "target"; occurrence = Some 0 }
                let! first = FcsBridge().Find(request)
                let cursor = text first "nextCursor"
                let symbolRequest : FcsSymbolAtWordArgs =
                    { path = fixture.ASource; line = 7; word = Some "target"; occurrence = Some 0
                      projectPath = Some fixture.AProject; projectOptions = None; text = None; includeDocumentation = Some false }
                let! selectedBefore = FcsBridge().SymbolAtWord(symbolRequest)
                Assert.Equal(4, selectedBefore["definitionRange"].["startLine"].GetValue<int>())
                let! original = FcsBridge().Find({ request with maxResults = Some 100 })
                Assert.Equal(4, (rows original).Length)
                // Change the inferred argument elsewhere; neither call nor declaration
                // row changes. FullName and the complete row stream cannot detect this.
                File.WriteAllText(fixture.ASource, overloadSource.Replace("let value = 1", "let value = \"a\"", StringComparison.Ordinal))
                let! selectedAfter = FcsBridge().SymbolAtWord(symbolRequest)
                Assert.Equal(5, selectedAfter["definitionRange"].["startLine"].GetValue<int>())
                Assert.NotEqual<string>(text selectedBefore "typeString", text selectedAfter "typeString")
                let! changed = FcsBridge().Find({ request with maxResults = Some 100 })
                Assert.Equal<string array>(rows original, rows changed)
                let! continuation = FcsBridge().Find({ request with cursor = Some cursor })
                assertRejected "cursor_stale" true continuation
            finally
                fixture.RestoreSource()
        }

    [<Fact>]
    member _.``unavailable position validation returns no page and the same cursor succeeds on retry``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let request =
                { args fixture.AProject with
                    query = "requested-position"; kind = Some "position"
                    path = Some fixture.ASource; line = Some 2; word = Some "target"; occurrence = Some 0 }
            let! first = FcsBridge().Find(request)
            let cursor = text first "nextCursor"
            let bridge = FcsBridge(findPositionResolutionBeforeComputeOverride = (fun () ->
                Task.FromException(InvalidOperationException("Controlled unavailable type-check context"))))
            let! unavailable = bridge.Find({ request with cursor = Some cursor })
            assertRejected "cursor_validation_incomplete" false unavailable
            let! retry = FcsBridge().Find({ request with cursor = Some cursor })
            Assert.Equal("succeeded", text retry "status")
            Assert.Equal(1, retry["pageOffset"].GetValue<int>())
            Assert.Single(retry["sites"].AsArray()) |> ignore
        }

    [<Theory>]
    [<InlineData(false, false)>]
    [<InlineData(true, false)>]
    [<InlineData(false, true)>]
    [<InlineData(true, true)>]
    member _.``fully checked unresolved position target is stale even with compiler errors``
        (useCharacter: bool, withUnrelatedError: bool) : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let sourceWith selectedLine =
                String.concat "\n"
                    ([ "module CursorUnresolvedReview"
                       "let target () = ()"
                       selectedLine
                       "target ()"
                       "target ()" ]
                     @ if withUnrelatedError then [ "let unrelated = unknownElsewhere" ] else [])
            try
                File.WriteAllText(fixture.ASource, sourceWith "target ()")
                let request =
                    { args fixture.AProject with
                        query = "requested-position"; kind = Some "position"
                        path = Some fixture.ASource; line = Some 2; word = None
                        character = if useCharacter then Some 2 else None }
                let bridge = FcsBridge()
                let! first = bridge.Find(request)
                let cursor = text first "nextCursor"
                Assert.Single(first["sites"].AsArray()) |> ignore

                // Keep the exact request and location, but replace the selected
                // target with an unresolved identifier. FS0039 is conclusive
                // semantic evidence here, not an unavailable type-check.
                File.WriteAllText(fixture.ASource, sourceWith "missing ()")
                let! checkedSource =
                    FcsBridge().ParseAndCheckFile(
                        { path = fixture.ASource; text = None
                          projectPath = Some fixture.AProject; projectOptions = None })
                Assert.True(checkedSource["hasFullTypeCheckInfo"].GetValue<bool>())
                Assert.Contains(checkedSource["checkDiagnostics"].AsArray(), fun diagnostic ->
                    diagnostic["errorNumber"].GetValue<int>() = 39
                    && diagnostic["message"].GetValue<string>().Contains("missing", StringComparison.Ordinal))
                let! stale = bridge.Find({ request with cursor = Some cursor })
                assertRejected "cursor_stale" true stale
                // A fresh host-equivalent bridge must not advise an endless
                // retry for this unchanged, successfully checked broken target.
                let! repeated = FcsBridge().Find({ request with cursor = Some cursor })
                assertRejected "cursor_stale" true repeated
            finally
                fixture.RestoreSource()
        }

    [<Fact>]
    member _.``stable failed coverage paginates but recovered coverage invalidates identical sites``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            // B has no matching rows: recovery must be detected from the complete
            // coverage ledger even when every canonical site byte remains identical.
            let request =
                { args fixture.Solution with query = "CursorFixture.A.target"; scope = Some "workspace" }
            let failedBridge message =
                FcsBridge(projectEvaluationBeforeLoadOverride = (fun path ->
                    if Path.GetFullPath path = Path.GetFullPath fixture.BProject then
                        Task.FromException(InvalidOperationException(message))
                    else Task.CompletedTask))
            let! first = (failedBridge "Attempt one telemetry").Find(request)
            Assert.Equal(1, first["coverage"].["projectsFailed"].GetValue<int>())
            let cursor = text first "nextCursor"
            let! unchanged = (failedBridge "Different retry message and elapsed time").Find({ request with cursor = Some cursor; maxResults = Some 100 })
            Assert.NotEmpty(unchanged["sites"].AsArray())
            let! recovered = FcsBridge().Find({ request with maxResults = Some 100 })
            Assert.Equal<string array>(rows recovered, Array.append (rows first) (rows unchanged))
            let! stale = FcsBridge().Find({ request with cursor = Some cursor })
            assertRejected "cursor_stale" true stale
        }

    [<Fact>]
    member _.``unknown zero-evidence refresh cannot silently finish a previously positive cursor``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let request = args fixture.AProject
            let! first = FcsBridge().Find(request)
            let cursor = text first "nextCursor"
            let unknownBridge () =
                FcsBridge(projectEvaluationBeforeLoadOverride = (fun _ ->
                    Task.FromException(InvalidOperationException("Controlled stable project failure"))))
            let! unknown = (unknownBridge ()).Find(request)
            Assert.Equal("unknown", text unknown "status")
            Assert.False(unknown["coverage"].["complete"].GetValue<bool>())
            Assert.Empty(unknown["sites"].AsArray())
            Assert.Null(unknown["nextCursor"])
            let! continuation = (unknownBridge ()).Find({ request with cursor = Some cursor })
            assertRejected "cursor_stale" true continuation
        }

    [<Theory>]
    [<InlineData(1)>]
    [<InlineData(205)>]
    member _.``diagnostic refresh including beyond response prefix invalidates unchanged canonical sites``(diagnosticCount: int) : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let originalSource = File.ReadAllText(fixture.ASource)
            let originalProject = File.ReadAllText(fixture.AProject)
            try
                // FCS must see all diagnostics, not stop at a compiler error ceiling.
                File.WriteAllText(fixture.AProject, originalProject.Replace("</PropertyGroup>", "<OtherFlags>--maxerrors:500</OtherFlags></PropertyGroup>", StringComparison.Ordinal))
                let errors =
                    [ for index in 0 .. diagnosticCount - 1 -> $"let diagnostic{index:D3} = missing{index:D3}" ]
                    |> String.concat "\n"
                let source = originalSource + "\n" + errors
                File.WriteAllText(fixture.ASource, source)
                let request = args fixture.AProject
                let expected = expectedFixtureSites fixture.ASource
                Assert.Equal(4, expected.Length)
                let cursorBridge = FcsBridge()
                let! cursor, first =
                    initialCursorWithPositiveEvidence
                        output.WriteLine
                        "diagnostic refresh cursor source"
                        expected.Length
                        (fun () -> cursorBridge.Find(request))
                requireEnvelope
                    (tryValue<int> "projectDiagnosticsTotalCount" first = Some diagnosticCount)
                    "The cursor source did not observe every diagnostic."
                    first
                Assert.Equal(diagnosticCount, first["projectDiagnosticsTotalCount"].GetValue<int>())

                let originalBridge = FcsBridge()
                let! original =
                    collectCompletePositiveFind output.WriteLine "original diagnostic baseline" expected.Length (fun cursor ->
                        originalBridge.Find({ request with maxResults = Some 100; cursor = cursor }))
                assertFixtureSites expected original

                File.WriteAllText(fixture.ASource, source.Replace($"missing{diagnosticCount - 1:D3}", "changedXYZ", StringComparison.Ordinal))
                let changedBridge = FcsBridge()
                let! changed =
                    collectCompletePositiveFind output.WriteLine "changed diagnostic baseline" expected.Length (fun cursor ->
                        changedBridge.Find({ request with maxResults = Some 100; cursor = cursor }))
                Assert.Equal<(string * int * int * int) array>(expected, expectedFixtureSites fixture.ASource)
                assertFixtureSites expected changed

                let comparisonEvidence =
                    String.concat "\n" ([| "Original full find envelopes:" |] |> Array.append original.Envelopes)
                    + "\nChanged full find envelopes:\n"
                    + String.concat "\n" changed.Envelopes
                Assert.True(original.Rows = changed.Rows, comparisonEvidence)
                Assert.Equal<string array>(original.Rows, changed.Rows)
                requireEnvelope
                    (tryValue<int> "projectDiagnosticsTotalCount" changed.FirstPage = Some diagnosticCount)
                    "The changed baseline did not observe every diagnostic."
                    changed.FirstPage
                Assert.Equal(diagnosticCount, changed.FirstPage["projectDiagnosticsTotalCount"].GetValue<int>())
                if diagnosticCount > 200 then
                    requireEnvelope
                        (tryValue<bool> "projectDiagnosticsTruncated" original.FirstPage = Some true)
                        "The original diagnostic projection was expected to be truncated."
                        original.FirstPage
                    Assert.True(original.FirstPage["projectDiagnosticsTruncated"].GetValue<bool>())
                    Assert.True(
                        original.FirstPage["projectDiagnostics"].ToJsonString() = changed.FirstPage["projectDiagnostics"].ToJsonString(),
                        comparisonEvidence)
                    Assert.Equal(original.FirstPage["projectDiagnostics"].ToJsonString(), changed.FirstPage["projectDiagnostics"].ToJsonString())
                else
                    Assert.True(
                        original.FirstPage["projectDiagnostics"].ToJsonString() <> changed.FirstPage["projectDiagnostics"].ToJsonString(),
                        comparisonEvidence)
                    Assert.NotEqual<string>(original.FirstPage["projectDiagnostics"].ToJsonString(), changed.FirstPage["projectDiagnostics"].ToJsonString())
                let! continuation = FcsBridge().Find({ request with cursor = Some cursor })
                assertRejected "cursor_stale" true continuation
            finally
                File.WriteAllText(fixture.ASource, originalSource)
                File.WriteAllText(fixture.AProject, originalProject)
        }

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    member _.``unchanged position continuation with removed identifier or line is stale``(removeLine: bool) : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            try
                File.WriteAllText(fixture.ASource, overloadSource)
                let request =
                    { args fixture.AProject with
                        query = "requested-position"; kind = Some "position"
                        path = Some fixture.ASource; line = Some 7; word = Some "target"; occurrence = Some 0 }
                let! first = FcsBridge().Find(request)
                let cursor = text first "nextCursor"
                let changed =
                    if removeLine then "module CursorReview\nlet value = 1"
                    else overloadSource.Replace("let call = C.target value", "let call = value", StringComparison.Ordinal)
                File.WriteAllText(fixture.ASource, changed)
                let! continuation = FcsBridge().Find({ request with cursor = Some cursor })
                assertRejected "cursor_stale" true continuation
            finally
                fixture.RestoreSource()
        }

type FindCursorLinkedReviewTests(fixture: LinkedSourceFixture) =
    interface IClassFixture<LinkedSourceFixture>

    [<Fact>]
    member _.``every saturated linked row survives alternating page size and response budget exactly once``() : Task =
        task {
            let request =
                { args fixture.Slnx with query = "CrowdedParcel"; kind = Some "field"
                                         scope = Some "workspace"; includeSiteTypes = Some true; timeoutMs = Some 60_000 }
            let normal = FcsBridge()
            let small = FcsBridge(findResponseBudgetCharsOverride = 12_000)
            let! all = normal.Find({ request with maxResults = Some 1000 })
            Assert.Equal("succeeded", text all "status")
            // Even maxResults=1000 is bounded by the unchanged 60k production
            // envelope. Exhaust the baseline too; it is not an unbounded response.
            let expectedNodes = ResizeArray<JsonNode>(all["sites"].AsArray())
            let mutable baselineCursor = if isNull all["nextCursor"] then None else Some(text all "nextCursor")
            while baselineCursor.IsSome do
                let! page = normal.Find({ request with maxResults = Some 1000; cursor = baselineCursor })
                Assert.Equal("succeeded", text page "status")
                Assert.Equal(expectedNodes.Count, page["pageOffset"].GetValue<int>())
                Assert.NotEmpty(page["sites"].AsArray())
                expectedNodes.AddRange(page["sites"].AsArray())
                Assert.True(expectedNodes.Count <= all["totalSites"].GetValue<int>())
                baselineCursor <- if isNull page["nextCursor"] then None else Some(text page "nextCursor")
            let expected = expectedNodes |> Seq.map _.ToJsonString() |> Seq.toArray
            // Re-derive the sites from the physical fixture, not from a stored row
            // count; each literal must appear once despite five project analyses.
            let expectedLines =
                File.ReadAllLines(fixture.SharedSource)
                |> Array.indexed
                |> Array.choose (fun (index, line) ->
                    if line.StartsWith("let crowded", StringComparison.Ordinal) then Some(index + 1) else None)
            let actualLines = expectedNodes |> Seq.map (fun site -> site["range"].["startLine"].GetValue<int>()) |> Seq.toArray
            Assert.Equal<int array>(expectedLines, actualLines)
            for site in expectedNodes do
                Assert.Equal(3, site["siteTypeAlternatives"].AsArray().Count)
                Assert.Equal(1, site["siteTypeAlternativesOmitted"].GetValue<int>())
                Assert.NotNull(site["siteTypeAlternativesOffset"])
                Assert.Equal(3, site["siteTypeAlternativesOffset"].GetValue<int>())
            let collected = ResizeArray<string>()
            let mutable cursor = None
            let mutable pageNumber = 0
            let mutable budgetReduced = false
            while pageNumber = 0 || cursor.IsSome do
                Assert.True(pageNumber < expected.Length, "A continuation must always advance.")
                let pageSize = [| 7; 3; 13 |][pageNumber % 3]
                let bridge = if pageNumber % 2 = 0 then small else normal
                let! page = bridge.Find({ request with maxResults = Some pageSize; cursor = cursor })
                Assert.Equal("succeeded", text page "status")
                Assert.Equal(collected.Count, page["pageOffset"].GetValue<int>())
                Assert.True((rows page).Length > 0)
                budgetReduced <- budgetReduced || page["sitesTruncatedByBudget"].GetValue<bool>()
                collected.AddRange(rows page)
                cursor <- if isNull page["nextCursor"] then None else Some(text page "nextCursor")
                pageNumber <- pageNumber + 1
            Assert.True(budgetReduced, "Must cross the production serializer's budget boundary, not only maxResults.")
            Assert.True(pageNumber > 2)
            Assert.Equal<string array>(expected, collected.ToArray())
            Assert.Equal(expected.Length, collected |> Seq.distinct |> Seq.length)
        }
