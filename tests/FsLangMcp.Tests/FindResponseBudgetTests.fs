module FsLangMcp.Tests.FindResponseBudgetTests

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Cursor
open FsLangMcp.FcsBridge
open FsLangMcp.Tools
open FsLangMcp.Types

let private dotnetHost =
    Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
    |> Option.ofObj
    |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Option.defaultValue "dotnet"

let private jsonArrayPrefix (nodes: JsonNode array) count =
    nodes
    |> Array.take count
    |> Array.map (fun node -> node.DeepClone())
    |> JsonArray
    :> JsonNode

let private repeatedNodes key count chars =
    Array.init count (fun index ->
        jobj
            [ "index", jint index
              key, jstr (String.replicate chars "界") ]
        :> JsonNode)

let private assertBoundedFinalRecovery (result: JsonNode) =
    Assert.Equal("aborted", result["status"].GetValue<string>())
    Assert.Equal("indeterminate", result["outcome"].GetValue<string>())
    Assert.Equal("blocked", result["deliveryStatus"].GetValue<string>())
    Assert.Equal("find_response_exceeds_budget", result["errorCode"].GetValue<string>())
    Assert.True(result["retryable"].GetValue<bool>())
    Assert.True(result["responseTruncatedByBudget"].GetValue<bool>())
    Assert.Equal(FindResponseBudget.MaxSerializedChars, result["responseBudgetChars"].GetValue<int>())
    Assert.Equal(FindResponseBudget.SizeUnit, result["responseSizeUnit"].GetValue<string>())
    Assert.Equal(0, result["cursorAdvancedBy"].GetValue<int>())
    Assert.Null(result["nextCursor"])
    let recovery = result["recovery"]
    Assert.False(recovery["reuseOriginalCursor"].GetValue<bool>())
    Assert.Equal((renderToken result).Length, renderedLength result)
    Assert.True(renderedLength result <= FindResponseBudget.MaxSerializedChars)
    Assert.Same(result, FindResponseBudget.guardFinalResponse result)

type FindBudgetFixture() =
    let runId = Guid.NewGuid().ToString("N")
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_budget_{runId}")
    let sourcePath = Path.Combine(root, "Budget.fs")
    let projectPath = Path.Combine(root, "Budget.fsproj")
    let solutionPath = Path.Combine(root, "Budget.slnx")

    let longComment = String.replicate 1_800 "🙂界"

    let source =
        [ "module FindBudget.Fixture"
          ""
          "let target = 1"
          ""
          "let partial value ="
          "    match value with"
          "    | 0 -> target" ]
        @ [ for index in 0..31 -> $"let value{index:D2} = target + {index} // {longComment}" ]
        |> String.concat "\n"

    do
        Directory.CreateDirectory(root) |> ignore
        File.WriteAllText(sourcePath, source)

        File.WriteAllText(
            projectPath,
            String.concat
                Environment.NewLine
                [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
                  "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                  "  <ItemGroup><Compile Include=\"Budget.fs\" /></ItemGroup>"
                  "</Project>" ]
        )

        File.WriteAllText(
            solutionPath,
            String.concat
                Environment.NewLine
                [ "<Solution>"
                  "  <Project Path=\"Budget.fsproj\" />"
                  "  <Project Path=\"Missing.fsproj\" />"
                  "</Solution>" ]
        )

    let buildInfo =
        let startInfo =
            ProcessStartInfo(
                dotnetHost,
                $"build \"{projectPath}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
            )

        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.UseShellExecute <- false
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] <- "1"
        startInfo.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] <- "1"
        use child = Process.Start(startInfo)
        let stdout = child.StandardOutput.ReadToEnd()
        let stderr = child.StandardError.ReadToEnd()
        child.WaitForExit()
        child.ExitCode, stdout + stderr

    member _.SourcePath = sourcePath
    member _.ProjectPath = projectPath
    member _.SolutionPath = solutionPath
    member _.BuildExitCode = fst buildInfo
    member _.BuildLog = snd buildInfo

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists root then
                Directory.Delete(root, true)

let private findArgs (fixture: FindBudgetFixture) cursor =
    { query = "target"
      kind = Some "symbol"
      scope = Some "project"
      exact = Some true
      ``member`` = None
      field = None
      path = Some fixture.SourcePath
      line = None
      word = None
      occurrence = None
      character = None
      contextLines = Some Int32.MaxValue
      includeDeclaration = Some true
      includeInfo = Some true
      includePerProject = Some true
      includeSiteTypes = Some false
      projectPath = Some fixture.ProjectPath
      maxResults = Some 1000
      timeoutMs = None
      cursor = cursor }

let private tryValue<'T> (key: string) (page: JsonNode) : 'T option =
    match page with
    | :? JsonObject as fields ->
        match fields[key] with
        | :? JsonValue as value ->
            match value.TryGetValue<'T>() with
            | true, result -> Some result
            | false, _ -> None
        | _ -> None
    | _ -> None

let private isInitialResponseTimeout (page: JsonNode) =
    match page with
    | :? JsonObject as fields ->
        let emptySites =
            match fields["sites"] with
            | :? JsonArray as sites -> sites.Count = 0
            | _ -> false

        (tryValue<string> "status" page = Some "partial"
         || tryValue<string> "status" page = Some "unknown")
        && tryValue<string> "errorKind" page = Some "find_response_timeout"
        && tryValue<bool> "retryable" page = Some true
        && tryValue<bool> "paginationRestartRequired" page = Some true
        && tryValue<bool> "retrySameCursor" page = Some false
        && emptySites
        && tryValue<int> "pageOffset" page = Some 0
        && tryValue<int> "returnedSiteCount" page = Some 0
        && tryValue<int> "cursorAdvancedBy" page = Some 0
        && tryValue<bool> "resultSetComplete" page = Some false
        && tryValue<string> "deliveryStatus" page = Some "partial"
        && tryValue<bool> "truncated" page = Some true
        && tryValue<bool> "totalEstimateIsLowerBound" page = Some true
        && tryValue<string> "paginationIncompleteReason" page = Some "deadline_incomplete"
        && fields.ContainsKey("nextCursor")
        && isNull fields["nextCursor"]
        && isNull fields["errorCode"]
    | _ -> false

let private tryInitialCursor (page: JsonNode) =
    match page with
    | :? JsonObject as fields
        when isNull fields["errorKind"]
             && isNull fields["errorCode"]
             && (tryValue<string> "status" page = Some "succeeded"
                 || tryValue<string> "status" page = Some "partial")
             && tryValue<int> "pageOffset" page = Some 0
             && tryValue<int> "returnedSiteCount" page = Some 1
             && tryValue<int> "cursorAdvancedBy" page = Some 1 ->
        match fields["sites"], tryValue<string> "nextCursor" page with
        | (:? JsonArray as sites), Some cursor when sites.Count = 1 && not (String.IsNullOrWhiteSpace cursor) ->
            match tryDecodeFind cursor with
            | Ok payload when payload.Offset = 1 -> Some cursor
            | _ -> None
        | _ -> None
    | _ -> None

// Only the ordinary one-site setup may make one additional request. The caller
// closes over identical initial arguments; continuation/overflow calls never use this helper.
let private initialCursorWithOneRetry (writeDiagnostic: string -> unit) (request: unit -> Task<JsonNode>) =
    task {
        let evidence = ResizeArray<string>()

        let record message =
            evidence.Add message
            writeDiagnostic message

        let attempt number =
            task {
                try
                    let! page = request ()
                    record $"initial-page attempt={number}: {renderToken page}"
                    return page
                with error ->
                    record $"initial-page attempt={number} threw: {error}"
                    return raise error
            }

        let fail () =
            let responses = String.concat "\n" evidence
            raise (Xunit.Sdk.XunitException($"Expected a one-site initial page with an offset-one cursor.\n{responses}"))

        let! first = attempt 1

        match tryInitialCursor first with
        | Some cursor -> return cursor
        | None when isInitialResponseTimeout first ->
            let! second = attempt 2

            match tryInitialCursor second with
            | Some cursor -> return cursor
            | None -> return fail ()
        | None -> return fail ()
    }

let private scriptedInitialTimeout status =
    jobj
        [ "status", jstr status
          "errorKind", jstr "find_response_timeout"
          "retryable", jbool true
          "paginationRestartRequired", jbool true
          "retrySameCursor", jbool false
          "sites", JsonArray() :> JsonNode
          "pageOffset", jint 0
          "returnedSiteCount", jint 0
          "cursorAdvancedBy", jint 0
          "resultSetComplete", jbool false
          "deliveryStatus", jstr "partial"
          "truncated", jbool true
          "totalEstimateIsLowerBound", jbool true
          "paginationIncompleteReason", jstr "deadline_incomplete"
          "nextCursor", null ]
    :> JsonNode

let private scriptedInitialPage status =
    jobj
        [ "status", jstr status
          "pageOffset", jint 0
          "returnedSiteCount", jint 1
          "cursorAdvancedBy", jint 1
          "sites", JsonArray(jobj [] :> JsonNode) :> JsonNode
          "nextCursor", jstr (encodeFindV2 1 (textIdentityV2 "query") (textIdentityV2 "snapshot")) ]
    :> JsonNode

let private isContinuationValidationIncomplete cursor offset (page: JsonNode) =
    match cursor, page with
    | Some token, (:? JsonObject as fields) ->
        let unchangedProgress key expected =
            not (fields.ContainsKey key) || tryValue<int> key page = Some expected

        let compatibleFlag key expected =
            not (fields.ContainsKey key) || tryValue<bool> key page = Some expected

        let compatibleText key expected =
            not (fields.ContainsKey key) || tryValue<string> key page = Some expected

        let validInput =
            match tryDecodeFind token with
            | Ok payload -> payload.Offset = offset
            | Error _ -> false

        let emptySites =
            match fields["sites"] with
            | :? JsonArray as sites -> sites.Count = 0
            | _ -> false

        validInput
        && tryValue<string> "status" page = Some "invalid_cursor"
        && tryValue<string> "errorKind" page = Some "cursor_validation_incomplete"
        && tryValue<bool> "retryable" page = Some true
        && tryValue<bool> "paginationRestartRequired" page = Some false
        && tryValue<bool> "retrySameCursor" page = Some true
        && fields.ContainsKey("nextCursor")
        && isNull fields["nextCursor"]
        && isNull fields["errorCode"]
        && emptySites
        && unchangedProgress "pageOffset" offset
        && unchangedProgress "returnedSiteCount" 0
        && unchangedProgress "cursorAdvancedBy" 0
        && compatibleFlag "resultSetComplete" false
        && compatibleText "deliveryStatus" "partial"
        && compatibleFlag "truncated" true
        && compatibleFlag "totalEstimateIsLowerBound" true
        && compatibleText "paginationIncompleteReason" "deadline_incomplete"
    | _ -> false

// The closure reuses the same bridge and immutable arguments. Neither failed
// attempt enters the caller's site/offset ledger, and there is no restart path.
let private traversalPageWithOneRetry writeDiagnostic cursor offset (request: unit -> Task<JsonNode>) =
    task {
        let succeeded (page: JsonNode) =
            match page with
            | :? JsonObject as fields ->
                tryValue<string> "status" page = Some "succeeded"
                && isNull fields["errorKind"]
                && isNull fields["errorCode"]
            | _ -> false

        let! first = request ()
        if succeeded first then
            return first
        else
            writeDiagnostic $"traversal attempt=1: {renderToken first}"
            if not (isContinuationValidationIncomplete cursor offset first) then
                return raise (Xunit.Sdk.XunitException($"Unexpected traversal response: {renderToken first}"))
            else
                let! second = request ()
                writeDiagnostic $"traversal attempt=2: {renderToken second}"
                if not (succeeded second) then
                    return raise (Xunit.Sdk.XunitException($"Continuation retry failed. First: {renderToken first}\nSecond: {renderToken second}"))
                else
                    return second
    }

let private scriptedValidationIncomplete () =
    jobj
        [ "status", jstr "invalid_cursor"
          "errorKind", jstr "cursor_validation_incomplete"
          "retryable", jbool true
          "paginationRestartRequired", jbool false
          "retrySameCursor", jbool true
          "sites", JsonArray() :> JsonNode
          "nextCursor", null ]
    :> JsonNode

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``traversal helper keeps the same token and ledger and records recovered expiry`` includeMetadata : Task =
    task {
        let token = encodeFindV2 3 (textIdentityV2 "query") (textIdentityV2 "snapshot")
        let first = scriptedValidationIncomplete ()
        if includeMetadata then
            first["pageOffset"] <- jint 3
            first["returnedSiteCount"] <- jint 0
            first["cursorAdvancedBy"] <- jint 0
            first["resultSetComplete"] <- jbool false
            first["deliveryStatus"] <- jstr "partial"
            first["truncated"] <- jbool true
            first["totalEstimateIsLowerBound"] <- jbool true
            first["paginationIncompleteReason"] <- jstr "deadline_incomplete"
        let second = jobj [ "status", jstr "succeeded" ] :> JsonNode
        let diagnostics = ResizeArray<string>()
        let ledger = Set.ofList [ "site-0"; "site-1"; "site-2" ]
        let requests = ResizeArray<string option * int * Set<string>>()
        let request () =
            requests.Add(Some token, 3, ledger)
            Task.FromResult(if requests.Count = 1 then first else second)

        let! page = traversalPageWithOneRetry diagnostics.Add (Some token) 3 request
        Assert.Same(second, page)
        Assert.Equal(2, requests.Count)
        Assert.Equal(requests[0], requests[1])
        Assert.Equal(3, ledger.Count)
        Assert.Equal(2, diagnostics.Count)
        Assert.Contains(renderToken first, diagnostics[0])
        Assert.Contains(renderToken second, diagnostics[1])

        requests.Clear()
        let! direct = traversalPageWithOneRetry ignore (Some token) 3 (fun () ->
            requests.Add(Some token, 3, ledger)
            Task.FromResult second)
        Assert.Same(second, direct)
        Assert.Single(requests) |> ignore
    }

[<Fact>]
let ``traversal helper rejects malformed contradictory and non-retryable envelopes immediately`` () : Task =
    task {
        let token = encodeFindV2 3 (textIdentityV2 "query") (textIdentityV2 "snapshot")
        let rejected = ResizeArray<JsonNode>()
        rejected.Add null
        for key in [ "status"; "errorKind"; "retryable"; "paginationRestartRequired"; "retrySameCursor"; "sites"; "nextCursor" ] do
            let missing = scriptedValidationIncomplete () :?> JsonObject
            missing.Remove(key) |> ignore
            rejected.Add missing
            let malformed = scriptedValidationIncomplete ()
            malformed[key] <- jint 1
            rejected.Add malformed
        for key, value in
            [ "status", jstr "succeeded"
              "status", jstr "partial"
              "errorKind", jstr "cursor_stale"
              "errorKind", jstr "cursor_query_mismatch"
              "errorKind", jstr "cursor_malformed"
              "errorKind", jstr "cursor_out_of_range"
              "errorKind", jstr "cursor_version_unsupported"
              "errorKind", jstr "cursor_tool_mismatch"
              "errorCode", jstr "find_site_exceeds_response_budget"
              "paginationRestartRequired", jbool true
              "retrySameCursor", jbool false
              "retryable", jbool false
              "sites", JsonArray(jobj [] :> JsonNode) :> JsonNode
              "nextCursor", jstr token
              "pageOffset", jint 0
              "returnedSiteCount", jint 1
              "cursorAdvancedBy", jint 1
              "pageOffset", jstr "3"
              "returnedSiteCount", null
              "cursorAdvancedBy", jstr "0"
              "resultSetComplete", jbool true
              "deliveryStatus", jstr "complete"
              "truncated", jbool false
              "totalEstimateIsLowerBound", jbool false
              "paginationIncompleteReason", jstr "complete"
              "resultSetComplete", jstr "false"
              "deliveryStatus", jbool false
              "truncated", jstr "true"
              "totalEstimateIsLowerBound", jstr "true"
              "paginationIncompleteReason", jbool false ] do
            let page = scriptedValidationIncomplete ()
            page[key] <- value
            rejected.Add page
        // Optional means absent is legal; supplied null or wrong types are not.
        for key in
            [ "resultSetComplete"; "deliveryStatus"; "truncated"; "totalEstimateIsLowerBound"; "paginationIncompleteReason" ] do
            for value in [ null; JsonObject() :> JsonNode; JsonArray() :> JsonNode; jint 1 ] do
                let page = scriptedValidationIncomplete ()
                page[key] <- value
                rejected.Add page
        for page in rejected do
            let mutable calls = 0
            let! error = Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () ->
                traversalPageWithOneRetry ignore (Some token) 3 (fun () ->
                    calls <- calls + 1
                    Task.FromResult(if calls = 1 then page else jobj [ "status", jstr "succeeded" ] :> JsonNode)) :> Task)
            Assert.Equal(1, calls)
            Assert.Contains(renderToken page, error.Message)
        Assert.False(isContinuationValidationIncomplete None 3 (scriptedValidationIncomplete ()))
        Assert.False(isContinuationValidationIncomplete (Some token) 4 (scriptedValidationIncomplete ()))
    }

[<Fact>]
let ``traversal helper stops after one retry including repeated expiry`` () : Task =
    task {
        let token = encodeFindV2 3 (textIdentityV2 "query") (textIdentityV2 "snapshot")
        for kind in [ "cursor_validation_incomplete"; "cursor_stale"; "cursor_query_mismatch"; "cursor_malformed" ] do
            let mutable calls = 0
            let diagnostics = ResizeArray<string>()
            let! error = Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () ->
                traversalPageWithOneRetry diagnostics.Add (Some token) 3 (fun () ->
                    calls <- calls + 1
                    let page = scriptedValidationIncomplete ()
                    if calls = 2 then page["errorKind"] <- jstr kind
                    Task.FromResult page) :> Task)
            Assert.Equal(2, calls)
            Assert.Equal(2, diagnostics.Count)
            Assert.Contains("First:", error.Message)
            Assert.Contains("Second:", error.Message)
            Assert.Contains(kind, error.Message)
    }

[<Theory>]
[<InlineData("succeeded")>]
[<InlineData("partial")>]
let ``initial cursor helper accepts a valid first page without retry`` status : Task =
    task {
        let page = scriptedInitialPage status
        let diagnostics = ResizeArray<string>()
        let mutable calls = 0
        let request () =
            calls <- calls + 1
            Task.FromResult page

        let! cursor = initialCursorWithOneRetry diagnostics.Add request
        Assert.Equal(page["nextCursor"].GetValue<string>(), cursor)
        Assert.Equal(1, calls)
        Assert.Single(diagnostics) |> ignore
    }

[<Theory>]
[<InlineData("partial")>]
[<InlineData("unknown")>]
let ``initial cursor helper retries the exact timeout once and records both pages after recovery`` status : Task =
    task {
        let first = scriptedInitialTimeout status
        let second = scriptedInitialPage "partial"
        let diagnostics = ResizeArray<string>()
        let mutable calls = 0
        let request () =
            calls <- calls + 1
            Task.FromResult(if calls = 1 then first else second)

        let! cursor = initialCursorWithOneRetry diagnostics.Add request
        Assert.Equal(second["nextCursor"].GetValue<string>(), cursor)
        Assert.Equal(2, calls)
        Assert.Equal(2, diagnostics.Count)
        Assert.Contains(renderToken first, diagnostics[0])
        Assert.Contains(renderToken second, diagnostics[1])
    }

[<Fact>]
let ``initial cursor helper stops after a second timeout and preserves both attempts`` () : Task =
    task {
        let mutable calls = 0
        let request () =
            calls <- calls + 1
            Task.FromResult(scriptedInitialTimeout "partial")

        let! error =
            Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () -> initialCursorWithOneRetry ignore request :> Task)

        Assert.Equal(2, calls)
        Assert.Contains("attempt=1", error.Message)
        Assert.Contains("attempt=2", error.Message)
    }

[<Fact>]
let ``initial cursor helper rejects malformed unexpected and cursorless responses without retry`` () : Task =
    task {
        let rejected = ResizeArray<JsonNode>()
        rejected.Add null
        rejected.Add(JsonArray())

        // Missing, null, and wrong-type fields must not accidentally satisfy the retry contract.
        for key in
            [ "status"; "errorKind"; "retryable"; "paginationRestartRequired"; "retrySameCursor"; "nextCursor"
              "sites"; "pageOffset"; "returnedSiteCount"; "cursorAdvancedBy"; "resultSetComplete"
              "deliveryStatus"; "truncated"; "totalEstimateIsLowerBound"; "paginationIncompleteReason" ] do
            let missing = scriptedInitialTimeout "partial" :?> JsonObject
            missing.Remove(key) |> ignore
            rejected.Add missing
            let wrongType = scriptedInitialTimeout "partial"
            wrongType[key] <- JsonObject()
            rejected.Add wrongType
            if key <> "nextCursor" then
                let nullField = scriptedInitialTimeout "partial"
                nullField[key] <- null
                rejected.Add nullField

        for key, value in
            [ "status", jstr "succeeded"
              "errorKind", jstr "find_timeout"
              "errorKind", jstr "cursor_validation_incomplete"
              "errorKind", jstr "cursor_stale"
              "errorKind", jstr "cursor_query_mismatch"
              "errorKind", jstr "cursor_malformed"
              "errorCode", jstr "find_site_exceeds_response_budget"
              "retryable", jbool false
              "paginationRestartRequired", jbool false
              "retrySameCursor", jbool true
              "sites", JsonArray(jobj [] :> JsonNode) :> JsonNode
              "pageOffset", jint 99
              "returnedSiteCount", jint 99
              "cursorAdvancedBy", jint 99
              "cursorAdvancedBy", jstr "0"
              "resultSetComplete", jbool true
              "deliveryStatus", jstr "complete"
              "truncated", jbool false
              "totalEstimateIsLowerBound", jbool false
              "paginationIncompleteReason", jstr "complete"
              "nextCursor", jstr "unexpected" ] do
            let page = scriptedInitialTimeout "partial"
            page[key] <- value
            rejected.Add page

        for key, value in
            [ "nextCursor", null
              "nextCursor", jstr ""
              "nextCursor", jstr "malformed"
              "nextCursor", jstr (encodeFindV2 2 (textIdentityV2 "query") (textIdentityV2 "snapshot"))
              "pageOffset", jint 1
              "returnedSiteCount", jint 0
              "cursorAdvancedBy", jint 0
              "sites", JsonArray() :> JsonNode
              "errorKind", jstr "find_response_timeout" ] do
            let page = scriptedInitialPage "succeeded"
            page[key] <- value
            rejected.Add page

        for page in rejected do
            let mutable calls = 0
            let request () =
                calls <- calls + 1
                Task.FromResult(if calls = 1 then page else scriptedInitialPage "succeeded")

            let! error =
                Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () -> initialCursorWithOneRetry ignore request :> Task)

            Assert.Equal(1, calls)
            Assert.Contains("attempt=1", error.Message)
    }

[<Theory>]
[<InlineData(1)>]
[<InlineData(2)>]
let ``initial cursor helper never retries exceptions`` failingAttempt : Task =
    task {
        let expected = InvalidOperationException("scripted request failure")
        let diagnostics = ResizeArray<string>()
        let mutable calls = 0
        let request () =
            calls <- calls + 1
            if calls = failingAttempt then
                Task.FromException<JsonNode> expected
            else
                Task.FromResult(scriptedInitialTimeout "partial")

        let! actual =
            Assert.ThrowsAsync<InvalidOperationException>(fun () -> initialCursorWithOneRetry diagnostics.Add request :> Task)

        Assert.Same(expected, actual)
        Assert.Equal(failingAttempt, calls)
        Assert.Equal(failingAttempt, diagnostics.Count)
        Assert.Contains("scripted request failure", diagnostics[diagnostics.Count - 1])
    }

let private fixtureEvidence (fixture: FindBudgetFixture) =
    let readEvidence read path =
        try
            if File.Exists path then read path else "<missing>"
        with error -> $"<unavailable: {error.Message}>"

    let paths =
        [ fixture.SourcePath; fixture.ProjectPath; fixture.SolutionPath
          Path.Combine(Path.GetDirectoryName(fixture.ProjectPath), "Missing.fsproj") ]

    let files =
        paths
        |> List.map (fun path ->
            let hash =
                readEvidence (fun file -> File.ReadAllBytes file |> SHA256.HashData |> Convert.ToHexString) path
            $"{path}: {hash}")
        |> String.concat "\n"

    let project = readEvidence File.ReadAllText fixture.ProjectPath
    let solution = readEvidence File.ReadAllText fixture.SolutionPath
    $"{files}\nproject={project}\nsolution={solution}"

let private runtimeEvidence () =
    let sdk =
        try
            let info = ProcessStartInfo(dotnetHost, "--version")
            info.UseShellExecute <- false
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            use child = Process.Start(info)
            if child.WaitForExit(5_000) then child.StandardOutput.ReadToEnd().Trim()
            else
                child.Kill(true)
                "<SDK diagnostic timed out>"
        with error -> $"<SDK diagnostic unavailable: {error.Message}>"

    let multiplier = Environment.GetEnvironmentVariable("FSLANGMCP_TEST_TIME_MULTIPLIER")
    let instrumented =
        typeof<FcsBridge>.Assembly.GetTypes()
        |> Array.exists (fun t -> t.FullName.Contains("Coverlet", StringComparison.OrdinalIgnoreCase))
    $"SDK={sdk}; runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; multiplier={multiplier}; coverletInstrumented={instrumented}"

[<Fact>]
let ``initial timeout rejects simultaneous delivered sites and progress before a healthy fallback`` () : Task =
    task {
        let malformed = scriptedInitialTimeout "partial"
        malformed["sites"] <- JsonArray(jobj [] :> JsonNode)
        for key in [ "pageOffset"; "returnedSiteCount"; "cursorAdvancedBy" ] do
            malformed[key] <- jint 99
        let mutable calls = 0
        let! _ = Assert.ThrowsAsync<Xunit.Sdk.XunitException>(fun () ->
            initialCursorWithOneRetry ignore (fun () ->
                calls <- calls + 1
                Task.FromResult(if calls = 1 then malformed else scriptedInitialPage "succeeded")) :> Task)
        Assert.Equal(1, calls)
    }

type RecoveryDiagnosticRetentionTests(output: Xunit.Abstractions.ITestOutputHelper) =
    [<Theory>]
    [<InlineData("initial")>]
    [<InlineData("continuation")>]
    member _.``successful deterministic recovery retains both typed attempts for TRX`` route : Task =
        task {
            // Passing-test output is retained by the TRX logger, not necessarily
            // streamed by the console logger. CI retains these reports on success too.
            let write (message: string) = output.WriteLine(message)
            let mutable calls = 0
            if route = "initial" then
                let! _ = initialCursorWithOneRetry write (fun () ->
                    calls <- calls + 1
                    Task.FromResult(if calls = 1 then scriptedInitialTimeout "partial" else scriptedInitialPage "succeeded"))
                ()
            else
                let token = encodeFindV2 3 (textIdentityV2 "query") (textIdentityV2 "snapshot")
                let recovered = scriptedInitialPage "succeeded"
                recovered["pageOffset"] <- jint 3
                recovered["nextCursor"] <- jstr (encodeFindV2 4 (textIdentityV2 "query") (textIdentityV2 "snapshot"))
                let! page = traversalPageWithOneRetry write (Some token) 3 (fun () ->
                    calls <- calls + 1
                    Task.FromResult(if calls = 1 then scriptedValidationIncomplete () else recovered))
                Assert.Same(recovered, page)
            Assert.Equal(2, calls)
        }

[<Fact>]
let ``bounded source slices stay valid Unicode and preserve UTF-16 source offsets`` () =
    let source = String.replicate 300 "🙂" + "target" + String.replicate 400 "界"
    let targetStart = source.IndexOf("target", StringComparison.Ordinal)
    let snippet = FindResponseBudget.boundedSnippet targetStart (targetStart + "target".Length) source

    Assert.True(snippet.Truncated)
    Assert.True(snippet.Text.Length <= FindResponseBudget.MaxSnippetChars)
    Assert.Contains("target", snippet.Text)

    Assert.Equal(
        source.Substring(snippet.SourceStartColumn, snippet.SourceEndColumn - snippet.SourceStartColumn),
        snippet.Text
    )

    Assert.Equal(source.Length, snippet.SourceLength)
    Assert.False(snippet.Text.Length > 0 && Char.IsLowSurrogate(snippet.Text[0]))
    Assert.False(snippet.Text.Length > 0 && Char.IsHighSurrogate(snippet.Text[snippet.Text.Length - 1]))

[<Fact>]
let ``planner measures sites diagnostics and per-project metadata with the production serializer`` () =
    let sites = repeatedNodes "site" 2 1_000
    let diagnostics = repeatedNodes "diagnostic" 2 32_000
    let projects = repeatedNodes "project" 2 32_000

    let build siteCount diagnosticCount projectCount =
        jobj
            [ "status", jstr "succeeded"
              "sites", jsonArrayPrefix sites siteCount
              "projectDiagnostics", jsonArrayPrefix diagnostics diagnosticCount
              "perProject", jsonArrayPrefix projects projectCount ]
        :> JsonNode

    match
        FindResponseBudget.planResponse
            FindResponseBudget.MaxSerializedChars
            sites.Length
            diagnostics.Length
            projects.Length
            build
    with
    | FindResponseBudget.FitPlan.Fits(siteCount, diagnosticCount, projectCount) ->
        Assert.Equal(sites.Length, siteCount)
        Assert.True(diagnosticCount < diagnostics.Length, "oversized diagnostics must participate in the ceiling")
        Assert.True(projectCount < projects.Length, "oversized per-project metadata must participate in the ceiling")
        let response = build siteCount diagnosticCount projectCount
        Assert.Equal((renderToken response).Length, renderedLength response)
        Assert.True(renderedLength response <= FindResponseBudget.MaxSerializedChars)
    | result -> Assert.Fail($"expected a fitting bounded plan, got {result}")

[<Fact>]
let ``diagnostic prefix is capped before expensive rows are materialized`` () =
    let rows = [| 0..9_999 |]
    let mutable mapped = 0

    let totalCount, materialized =
        FindResponseBudget.materializeCappedPrefix 200 (fun row ->
            mapped <- mapped + 1
            row * 2) rows

    Assert.Equal(rows.Length, totalCount)
    Assert.Equal(200, materialized.Length)
    Assert.Equal(200, mapped)
    Assert.Equal(398, materialized[199])

[<Fact>]
let ``planner worst-case metadata trimming probe count is bounded`` () =
    let mutable probes = 0

    let build siteCount diagnosticCount projectCount =
        probes <- probes + 1
        let metadataCount = diagnosticCount + projectCount

        jobj
            [ "sites", JsonArray(Array.init siteCount (fun _ -> jstr "site")) :> JsonNode
              "metadataCount", jint metadataCount
              "metadata",
              jstr (
                  if metadataCount = 0 then
                      ""
                  else
                      String.replicate 600 "x"
              ) ]
        :> JsonNode

    let result = FindResponseBudget.planResponse 500 1 200 1_000 build

    match result with
    | FindResponseBudget.FitPlan.Fits(1, 0, 0) -> ()
    | other -> Assert.Fail($"expected all optional metadata to be trimmed, got {other}")

    // The pre-fix loop made 2,403 full production-serialization probes here: two for every
    // removed metadata row plus a final site check. Binary prefix search has a deterministic
    // logarithmic bound for these counts (1 initial + 7 diagnostics + 10 projects + 1 site).
    Assert.True(probes <= 19, $"expected at most 19 serialized probes, got {probes}")

    probes <- 0

    match FindResponseBudget.planResponse 500 1_000 200 1_000 build with
    | FindResponseBudget.FitPlan.Fits(deliveredSites, 0, 0) ->
        Assert.InRange(deliveredSites, 1, 999)
    | other -> Assert.Fail($"expected a bounded site prefix after metadata trimming, got {other}")

    // maxResults is capped at 1,000. Adding its logarithmic site-prefix search keeps the
    // complete production-shaped worst case below 30 full serializations.
    Assert.True(probes <= 30, $"expected at most 30 serialized probes, got {probes}")

[<Fact>]
let ``logarithmic planner matches the former linear prefix policy across serialized thresholds`` () =
    let sites = repeatedNodes "site" 5 140
    let diagnostics = repeatedNodes "diagnostic" 4 170
    let projects = repeatedNodes "project" 3 190

    let build siteCount diagnosticCount projectCount =
        let truncated =
            siteCount < sites.Length
            || diagnosticCount < diagnostics.Length
            || projectCount < projects.Length

        jobj
            [ "sites", jsonArrayPrefix sites siteCount
              "projectDiagnostics", jsonArrayPrefix diagnostics diagnosticCount
              "perProject", jsonArrayPrefix projects projectCount
              "responseTruncatedByBudget", jbool truncated
              "responseSizeHint",
              (if truncated then
                   jstr (String.replicate 250 "h")
               else
                   null) ]
        :> JsonNode

    let bruteForce budget =
        let fits siteCount diagnosticCount projectCount =
            renderedLength (build siteCount diagnosticCount projectCount) <= budget

        let minimumSites = 1
        let mutable diagnosticCount = diagnostics.Length
        let mutable projectCount = projects.Length
        let mutable minimumFits = fits minimumSites diagnosticCount projectCount

        while not minimumFits && diagnosticCount > 0 do
            diagnosticCount <- diagnosticCount - 1
            minimumFits <- fits minimumSites diagnosticCount projectCount

        while not minimumFits && projectCount > 0 do
            projectCount <- projectCount - 1
            minimumFits <- fits minimumSites diagnosticCount projectCount

        if not minimumFits then
            if fits 0 diagnosticCount projectCount then
                FindResponseBudget.FitPlan.FirstSiteOverflow
            else
                FindResponseBudget.FitPlan.FixedMetadataOverflow
        else
            let mutable deliveredSites = sites.Length

            while deliveredSites > minimumSites && not (fits deliveredSites diagnosticCount projectCount) do
                deliveredSites <- deliveredSites - 1

            FindResponseBudget.FitPlan.Fits(deliveredSites, diagnosticCount, projectCount)

    for budget in 100..37..4_000 do
        let expected = bruteForce budget

        let actual =
            FindResponseBudget.planResponse budget sites.Length diagnostics.Length projects.Length build

        Assert.Equal(expected, actual)

[<Fact>]
let ``planner returns a typed first-site overflow instead of a zero-progress page`` () =
    let build siteCount _ _ =
        jobj
            [ "status", jstr "succeeded"
              "sites",
              JsonArray(
                  if siteCount = 0 then
                      [||]
                  else
                      [| jstr (String.replicate (FindResponseBudget.MaxSerializedChars + 1_000) "x") |]
              )
              :> JsonNode ]
        :> JsonNode

    let result =
        FindResponseBudget.planResponse FindResponseBudget.MaxSerializedChars 1 0 0 build

    match result with
    | FindResponseBudget.FitPlan.FirstSiteOverflow -> ()
    | other -> Assert.Fail($"expected FirstSiteOverflow, got {other}")

[<Fact>]
let ``final guard preserves bounded deadline results and can bound oversized deadline details`` () =
    let boundedTimeout =
        jobj
            [ "status", jstr "timeout"
              "errorKind", jstr "fcs_admission_timeout"
              "message", jstr "FCS admission timed out before protected work started."
              "retryable", jbool true ]
        :> JsonNode

    Assert.Same(boundedTimeout, FindResponseBudget.guardFinalResponse boundedTimeout)

    let oversizedTimeout =
        jobj
            [ "status", jstr "timeout"
              "errorKind", jstr "find_deadline"
              "message",
              jstr (String.replicate (FindResponseBudget.MaxSerializedChars + 1_000) "timeout-detail") ]
        :> JsonNode

    oversizedTimeout
    |> FindResponseBudget.guardFinalResponse
    |> assertBoundedFinalRecovery

type FindResponseBudgetIntegrationTests(fixture: FindBudgetFixture, output: Xunit.Abstractions.ITestOutputHelper) =
    interface IClassFixture<FindBudgetFixture>

    [<Fact>]
    member _.``real initial response expiry satisfies the narrow setup retry contract`` () : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let bridge = FcsBridge(findResponseDeadlineSignalOverride = (fun () -> Task.CompletedTask))
            let! page = bridge.Find({ findArgs fixture None with maxResults = Some 1 })
            output.WriteLine(renderToken page)
            Assert.True(isInitialResponseTimeout page, renderToken page)
            Assert.True((tryInitialCursor page).IsNone)
        }

    [<Fact>]
    member _.``oversized invalid input is replaced by bounded final recovery`` () : Task =
        task {
            let bridge = FcsBridge()

            let oversizedKind =
                String.replicate (FindResponseBudget.MaxSerializedChars + 1_000) "k"

            let! result =
                bridge.Find(
                    { findArgs fixture None with
                        kind = Some oversizedKind }
                )

            assertBoundedFinalRecovery result
        }

    [<Fact>]
    member _.``oversized project-resolution error is replaced by bounded final recovery`` () : Task =
        task {
            let bridge = FcsBridge()

            // FindCore's non-validation project-resolution error includes both the
            // resolved project and requested workspace paths. Keep each path below the
            // Windows long-path limit while making their combined production JSON exceed
            // the hard ceiling; no file-system object needs to be created.
            let componentLength = FindResponseBudget.MaxSerializedChars / 2 + 256

            let missingProjectPath =
                Path.Combine(Path.GetTempPath(), String.replicate componentLength "p" + ".fsproj")

            Assert.True(missingProjectPath.Length < 32_767)
            Assert.True(missingProjectPath.Length * 2 > FindResponseBudget.MaxSerializedChars)

            let! result =
                bridge.Find(
                    { findArgs fixture None with
                        scope = Some "project"
                        projectPath = Some missingProjectPath }
                )

            Assert.Equal(0L, bridge.ProjectEvaluationStartedCount)
            assertBoundedFinalRecovery result
        }

    [<Fact>]
    member _.``long Unicode context with huge limits stays bounded and cursor traversal has no skips`` () : Task =
        task {
            Assert.True(
                fixture.BuildExitCode = 0,
                $"Budget fixture build failed ({fixture.BuildExitCode}):\n{fixture.BuildLog}"
            )

            let bridge = FcsBridge()
            let sourceLines = File.ReadAllLines(fixture.SourcePath)
            let mutable cursor = None
            let mutable pageCount = 0
            let mutable totalSites = -1
            let mutable deliveredCount = 0
            let mutable identities = Set.empty<string>
            let mutable sawTruncatedLine = false
            let mutable sawBudgetClose = false
            let beforeTraversal = fixtureEvidence fixture
            let runtime = lazy (runtimeEvidence ())
            let elapsed = Stopwatch.StartNew()
            let mutable inputCursor = None
            let mutable inputOffset = 0
            let mutable attempt = 0
            let mutable previousPage: JsonNode = null
            let mutable currentPage: JsonNode = null
            let mutable completed = false

            let diagnostic message =
                let decoded = inputCursor |> Option.map tryDecodeFind
                output.WriteLine(
                    $"{message}\nlogicalPage={pageCount}; attempt={attempt}; inputOffset={inputOffset}; deliveredCount={deliveredCount}; uniqueSites={identities.Count}; elapsedMs={elapsed.ElapsedMilliseconds}\n"
                    + $"inputCursor={inputCursor}; decoded={decoded}\npreviousPage={renderToken previousPage}\ncurrentPage={renderToken currentPage}\n"
                    + $"fixture before:\n{beforeTraversal}\nnow:\n{fixtureEvidence fixture}\n{runtime.Value}")

            use _captureFailure =
                { new IDisposable with
                    member _.Dispose() =
                        if not completed then
                            try diagnostic "Traversal failed; no ledger reset or restart was performed."
                            with error -> output.WriteLine($"Failure diagnostics unavailable: {error}") }

            while cursor.IsSome || pageCount = 0 do
                Assert.True(pageCount < 100, "find cursor traversal must terminate")
                inputCursor <- cursor
                inputOffset <- deliveredCount
                attempt <- 0
                currentPage <- null
                let args = findArgs fixture inputCursor
                let originalIdentities = identities
                let request () =
                    task {
                        attempt <- attempt + 1
                        let! response = bridge.Find(args)
                        currentPage <- response
                        Assert.Equal(inputOffset, deliveredCount)
                        Assert.Equal<Set<string>>(originalIdentities, identities)
                        Assert.Equal(inputCursor, cursor)
                        return response
                    }

                let! page = traversalPageWithOneRetry diagnostic inputCursor inputOffset request
                pageCount <- pageCount + 1

                Assert.Equal("succeeded", page["status"].GetValue<string>())
                Assert.Equal(FindResponseBudget.MaxSerializedChars, page["responseBudgetChars"].GetValue<int>())
                Assert.Equal(FindResponseBudget.SizeUnit, page["responseSizeUnit"].GetValue<string>())
                Assert.Equal(deliveredCount, page["pageOffset"].GetValue<int>())
                Assert.Equal(1000, page["pageSize"].GetValue<int>())
                Assert.True(renderToken(page).Length <= FindResponseBudget.MaxSerializedChars)

                if totalSites < 0 then
                    totalSites <- page["totalSites"].GetValue<int>()
                    Assert.True(totalSites > 20, $"fixture should expose many sites, got {totalSites}")

                let sites = page["sites"] :?> JsonArray
                Assert.NotEmpty(sites)
                Assert.Equal(sites.Count, page["returnedSiteCount"].GetValue<int>())
                Assert.Equal(sites.Count, page["cursorAdvancedBy"].GetValue<int>())
                sawBudgetClose <- sawBudgetClose || page["sitesTruncatedByBudget"].GetValue<bool>()

                for site in sites do
                    let range = site["range"]
                    let startLine = range["startLine"].GetValue<int>()
                    let startColumn = range["startColumn"].GetValue<int>()
                    let endLine = range["endLine"].GetValue<int>()
                    let endColumn = range["endColumn"].GetValue<int>()
                    Assert.Equal(startLine, endLine)
                    let sourceLine = sourceLines[startLine - 1]
                    Assert.Equal("target", sourceLine.Substring(startColumn, endColumn - startColumn))

                    let snippetStart = site["lineTextSourceStartColumn"].GetValue<int>()
                    let snippetEnd = site["lineTextSourceEndColumn"].GetValue<int>()
                    let snippet = site["lineText"].GetValue<string>()
                    Assert.Equal(sourceLine.Length, site["lineTextSourceLength"].GetValue<int>())
                    Assert.Equal(sourceLine.Substring(snippetStart, snippetEnd - snippetStart), snippet)
                    Assert.True(snippet.Length <= FindResponseBudget.MaxSnippetChars)
                    sawTruncatedLine <- sawTruncatedLine || site["lineTextTruncated"].GetValue<bool>()

                    Assert.Equal(Int32.MaxValue, site["contextLinesRequested"].GetValue<int>())
                    Assert.Equal(FindResponseBudget.MaxContextLines, site["contextLinesApplied"].GetValue<int>())
                    Assert.True(site["contextLinesTruncated"].GetValue<bool>())

                    let before = site["before"] :?> JsonArray
                    let after = site["after"] :?> JsonArray
                    Assert.True(before.Count <= FindResponseBudget.MaxContextLines)
                    Assert.True(after.Count <= FindResponseBudget.MaxContextLines)

                    for context in Seq.append before after do
                        let contextLine = context["line"].GetValue<int>()
                        let contextSource = sourceLines[contextLine - 1]
                        let contextStart = context["sourceStartColumn"].GetValue<int>()
                        let contextEnd = context["sourceEndColumn"].GetValue<int>()
                        let contextText = context["text"].GetValue<string>()
                        Assert.Equal(contextSource.Length, context["sourceLength"].GetValue<int>())
                        Assert.Equal(contextSource.Substring(contextStart, contextEnd - contextStart), contextText)
                        Assert.True(contextText.Length <= FindResponseBudget.MaxSnippetChars)

                    let file = site["file"].GetValue<string>()
                    let identity = $"{file}:{startLine}:{startColumn}:{endLine}:{endColumn}"

                    Assert.DoesNotContain(identity, identities)
                    identities <- identities.Add identity

                deliveredCount <- deliveredCount + sites.Count

                cursor <-
                    match page["nextCursor"] with
                    | null -> None
                    | node ->
                        let next = node.GetValue<string>()

                        match tryDecodeFind next with
                        | Ok payload -> Assert.Equal(deliveredCount, payload.Offset)
                        | Error error -> Assert.Fail($"find emitted an invalid cursor: {error.Message}")

                        Some next

                previousPage <- page

            Assert.True(pageCount > 1, "the response budget should force multiple pages")
            Assert.True(sawBudgetClose, "at least one page must close on serialized size")
            Assert.True(sawTruncatedLine, "long source lines must expose truncation metadata")
            Assert.Equal(totalSites, deliveredCount)
            Assert.Equal(totalSites, identities.Count)
            completed <- true
        }

    [<Fact>]
    member _.``first-site overflow returns bounded typed recovery with no non-advancing cursor`` () : Task =
        task {
            Assert.True(
                fixture.BuildExitCode = 0,
                $"Budget fixture build failed ({fixture.BuildExitCode}):\n{fixture.BuildLog}"
            )

            let tinyBudget = 6_000
            let bridge = FcsBridge(findResponseBudgetCharsOverride = tinyBudget)

            let! result =
                bridge.Find(
                    { findArgs fixture None with
                        maxResults = Some 1
                        scope = Some "workspace"
                        projectPath = Some fixture.SolutionPath }
                )

            Assert.Equal("aborted", result["status"].GetValue<string>())
            Assert.Equal("blocked", result["deliveryStatus"].GetValue<string>())
            Assert.Equal("find_site_exceeds_response_budget", result["errorCode"].GetValue<string>())
            Assert.Equal(0, result["returnedSiteCount"].GetValue<int>())
            Assert.Equal(0, result["cursorAdvancedBy"].GetValue<int>())
            Assert.Null(result["nextCursor"])
            Assert.True(result["retryable"].GetValue<bool>())
            Assert.True(result["projectDiagnosticsTotalCount"].GetValue<int>() > 0)
            Assert.Equal(0, result["projectDiagnosticsReturnedCount"].GetValue<int>())
            Assert.True(result["projectDiagnosticsTruncated"].GetValue<bool>())
            Assert.True(result["projectDiagnosticsTruncatedByBudget"].GetValue<bool>())
            Assert.NotNull(result["coverage"])
            Assert.NotNull(result["resolution"])
            let resultResolution = result["resolution"]
            Assert.Equal(1, result["projectsSwept"].GetValue<int>())
            Assert.Equal(2, result["projectsRequested"].GetValue<int>())
            Assert.Equal(1, result["projectsAnalyzed"].GetValue<int>())
            Assert.Equal(1, result["projectsMissing"].GetValue<int>())
            Assert.Equal(1, resultResolution["projectsSwept"].GetValue<int>())
            Assert.Equal(2, resultResolution["projectsRequested"].GetValue<int>())
            Assert.Equal(1, resultResolution["projectsMissing"].GetValue<int>())
            Assert.Equal("project", resultResolution["scopeResolved"].GetValue<string>())
            let recovery = result["recovery"]
            Assert.False(recovery["reuseOriginalCursor"].GetValue<bool>())

            Assert.Equal(
                "never_for_budget_failure",
                recovery["reuseOriginalCursorCondition"].GetValue<string>()
            )

            let sameCursorRetry = recovery["sameCursorRetry"]
            Assert.False(sameCursorRetry["allowed"].GetValue<bool>())
            Assert.True(sameCursorRetry["requiresUnchangedResultIdentity"].GetValue<bool>())
            let changedIdentityRetry = recovery["changedIdentityRetry"]
            Assert.Equal("restart_without_cursor", changedIdentityRetry["action"].GetValue<string>())
            Assert.True(changedIdentityRetry["requiredBeforeChangingResultIdentity"].GetValue<bool>())
            Assert.False(changedIdentityRetry["reuseOriginalCursor"].GetValue<bool>())
            Assert.True(renderToken(result).Length <= tinyBudget)
        }

    [<Fact>]
    member _.``continuation overflow rejects ineffective same cursor retry`` () : Task =
        task {
            Assert.True(
                fixture.BuildExitCode = 0,
                $"Budget fixture build failed ({fixture.BuildExitCode}):\n{fixture.BuildLog}"
            )

            let tinyBudget = 6_000
            let bridge = FcsBridge(findResponseBudgetCharsOverride = tinyBudget)
            let initialArgs =
                { findArgs fixture None with
                    maxResults = Some 1
                    scope = Some "workspace"
                    projectPath = Some fixture.SolutionPath }

            let initialBridge = FcsBridge()
            let beforeSetup = fixtureEvidence fixture
            let setupDiagnostic message =
                output.WriteLine($"{message}\nsetup fixture before:\n{beforeSetup}\nnow:\n{fixtureEvidence fixture}")

            let! continuation = initialCursorWithOneRetry setupDiagnostic (fun () -> initialBridge.Find(initialArgs))

            let! result =
                bridge.Find(
                    { findArgs fixture (Some continuation) with
                        maxResults = Some 1000
                        scope = Some "workspace"
                        projectPath = Some fixture.SolutionPath }
                )

            Assert.Equal("find_site_exceeds_response_budget", result["errorCode"].GetValue<string>())
            Assert.Equal(1, result["pageOffset"].GetValue<int>())
            Assert.Equal(0, result["cursorAdvancedBy"].GetValue<int>())
            Assert.Null(result["nextCursor"])

            let recovery = result["recovery"]
            Assert.Equal("restart_without_cursor", recovery["action"].GetValue<string>())
            Assert.False(recovery["reuseOriginalCursor"].GetValue<bool>())
            Assert.Equal("never_for_budget_failure", recovery["reuseOriginalCursorCondition"].GetValue<string>())
            let sameCursorRetry = recovery["sameCursorRetry"]
            Assert.False(sameCursorRetry["allowed"].GetValue<bool>())
            Assert.True(sameCursorRetry["requiresUnchangedResultIdentity"].GetValue<bool>())
            Assert.False(sameCursorRetry["reuseOriginalCursor"].GetValue<bool>())
            let allowedChangedInputs = sameCursorRetry["allowedChangedInputs"] :?> JsonArray
            Assert.Equal(1, allowedChangedInputs.Count)
            Assert.Equal("maxResults", allowedChangedInputs[0].GetValue<string>())

            let changedIdentityRetry = recovery["changedIdentityRetry"]
            Assert.Equal("restart_without_cursor", changedIdentityRetry["action"].GetValue<string>())
            Assert.True(changedIdentityRetry["requiredBeforeChangingResultIdentity"].GetValue<bool>())
            Assert.False(changedIdentityRetry["reuseOriginalCursor"].GetValue<bool>())
            Assert.True(renderToken(result).Length <= tinyBudget)

            let! alreadyMinimal =
                bridge.Find(
                    { findArgs fixture (Some continuation) with
                        maxResults = Some 1
                        scope = Some "workspace"
                        projectPath = Some fixture.SolutionPath }
                )

            let minimalRecovery = alreadyMinimal["recovery"]
            let minimalSameCursorRetry = minimalRecovery["sameCursorRetry"]
            Assert.False(minimalRecovery["reuseOriginalCursor"].GetValue<bool>())
            Assert.False(minimalSameCursorRetry["allowed"].GetValue<bool>())
        }
