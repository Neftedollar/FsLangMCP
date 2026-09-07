module FsLangMcp.Tests.FindCursorReviewTests

open System
open System.IO
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
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

let private assertRejected kind restart (node: JsonNode) =
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

type FindCursorReviewTests(fixture: FindCursorFixture) =
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
                let! first = FcsBridge().Find(request)
                Assert.Equal(diagnosticCount, first["projectDiagnosticsTotalCount"].GetValue<int>())
                let cursor = text first "nextCursor"
                let! original = FcsBridge().Find({ request with maxResults = Some 100 })
                File.WriteAllText(fixture.ASource, source.Replace($"missing{diagnosticCount - 1:D3}", "changedXYZ", StringComparison.Ordinal))
                let! changed = FcsBridge().Find({ request with maxResults = Some 100 })
                Assert.Equal<string array>(rows original, rows changed)
                Assert.Equal(diagnosticCount, changed["projectDiagnosticsTotalCount"].GetValue<int>())
                if diagnosticCount > 200 then
                    Assert.True(original["projectDiagnosticsTruncated"].GetValue<bool>())
                    Assert.Equal(original["projectDiagnostics"].ToJsonString(), changed["projectDiagnostics"].ToJsonString())
                else
                    Assert.NotEqual<string>(original["projectDiagnostics"].ToJsonString(), changed["projectDiagnostics"].ToJsonString())
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
