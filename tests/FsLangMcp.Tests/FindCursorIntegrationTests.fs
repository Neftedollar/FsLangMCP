module FsLangMcp.Tests.FindCursorIntegrationTests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open FsLangMcp.Cursor
open FsLangMcp.FcsBridge
open FsLangMcp.Types

let private dotnetHost =
    Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
    |> Option.ofObj
    |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Option.defaultValue "dotnet"

let private sourceA =
    [ "module CursorFixture.A"
      "let target = 1"
      "let a1 = target + 1 // alpha"
      "let a2 = target + 2"
      "let a3 = target + 3" ]
    |> String.concat Environment.NewLine

let private sourceB =
    [ "module CursorFixture.B"
      "let target = 10"
      "let b1 = target + 1"
      "let b2 = target + 2" ]
    |> String.concat Environment.NewLine

let private project fileName =
    String.concat
        Environment.NewLine
        [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
          "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
          $"  <ItemGroup><Compile Include=\"{fileName}\" /></ItemGroup>"
          "</Project>" ]

let private runBuild target =
    let startInfo =
        ProcessStartInfo(
            dotnetHost,
            $"build \"{target}\" -c Debug -m:1 -nologo --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false"
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

type FindCursorFixture() =
    let root = Path.Combine(Path.GetTempPath(), $"fslangmcp_find_cursor_{Guid.NewGuid():N}")
    let aDirectory = Path.Combine(root, "A")
    let bDirectory = Path.Combine(root, "B")
    let aProject = Path.Combine(aDirectory, "A.fsproj")
    let bProject = Path.Combine(bDirectory, "B.fsproj")
    let aSource = Path.Combine(aDirectory, "A.fs")
    let bSource = Path.Combine(bDirectory, "B.fs")
    let solution = Path.Combine(root, "CursorFixture.slnx")
    let solutionText =
        String.concat
            Environment.NewLine
            [ "<Solution>"
              "  <Project Path=\"A/A.fsproj\" />"
              "  <Project Path=\"B/B.fsproj\" />"
              "</Solution>" ]

    do
        Directory.CreateDirectory(aDirectory) |> ignore
        Directory.CreateDirectory(bDirectory) |> ignore
        File.WriteAllText(aProject, project "A.fs")
        File.WriteAllText(bProject, project "B.fs")
        File.WriteAllText(aSource, sourceA)
        File.WriteAllText(bSource, sourceB)

        File.WriteAllText(solution, solutionText)

    let buildInfo = runBuild solution

    member _.Root = root
    member _.AProject = aProject
    member _.BProject = bProject
    member _.ASource = aSource
    member _.Solution = solution
    member _.BuildExitCode = fst buildInfo
    member _.BuildLog = snd buildInfo

    member _.RestoreSource() = File.WriteAllText(aSource, sourceA)
    member _.RestoreSolution() = File.WriteAllText(solution, solutionText)

    interface IDisposable with
        member _.Dispose() =
            if Directory.Exists(root) then
                Directory.Delete(root, true)

let private findArgs projectPath =
    { query = "target"
      kind = Some "symbol"
      scope = Some "workspace"
      exact = Some true
      ``member`` = None
      field = None
      path = None
      line = None
      word = None
      occurrence = None
      character = None
      contextLines = Some 0
      includeDeclaration = Some true
      includeInfo = Some false
      includePerProject = Some false
      includeSiteTypes = Some false
      projectPath = Some projectPath
      maxResults = Some 1
      timeoutMs = Some 30_000
      cursor = None }

let private stringValue (node: JsonNode) (key: string) = node[key].GetValue<string>()
let private boolValue (node: JsonNode) (key: string) = node[key].GetValue<bool>()

let private sites (response: JsonNode) =
    response["sites"] :?> JsonArray
    |> Seq.map _.ToJsonString()
    |> Seq.toArray

type FindCursorIntegrationTests(fixture: FindCursorFixture) =
    interface IClassFixture<FindCursorFixture>

    [<Fact>]
    member _.``v2 traversal is page-size invariant and survives a fresh bridge with explicit project context``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let bridge = FcsBridge()
            let baseArgs = findArgs fixture.Solution
            let! first = bridge.Find(baseArgs)
            let cursor = stringValue first "nextCursor"

            match tryDecodeFind cursor with
            | Error error -> Assert.Fail(error.Message)
            | Ok decoded -> Assert.Equal(1, decoded.Offset)

            let! complete = bridge.Find({ baseArgs with maxResults = Some 100 })
            let expected = sites complete
            Assert.True(expected.Length > 3)
            Assert.Equal(expected[0], (sites first)[0])

            let collected = ResizeArray<string>()
            collected.AddRange(sites first)
            let mutable next = Some cursor
            let mutable pageSize = 2

            while next.IsSome do
                // A new bridge proves there is no retained per-cursor result state.
                let freshBridge = FcsBridge()

                let! page =
                    freshBridge.Find(
                        { baseArgs with
                            maxResults = Some pageSize
                            timeoutMs = Some 60_000
                            cursor = next }
                    )

                Assert.Equal("succeeded", stringValue page "status")
                collected.AddRange(sites page)
                next <-
                    match page["nextCursor"] with
                    | null -> None
                    | value -> Some(value.GetValue<string>())
                pageSize <- if pageSize = 2 then 3 else 2

            Assert.Equal<string array>(expected, collected.ToArray())
            Assert.Equal(expected.Length, collected |> Seq.distinct |> Seq.length)
        }

    [<Fact>]
    member _.``changed request is a query mismatch while changed bounded source content is stale``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            fixture.RestoreSource()
            let bridge = FcsBridge()
            let baseArgs = findArgs fixture.Solution
            let! first = bridge.Find(baseArgs)
            let cursor = stringValue first "nextCursor"

            let! changedQuery = bridge.Find({ baseArgs with query = "other"; cursor = Some cursor })
            Assert.Equal("invalid_cursor", stringValue changedQuery "status")
            Assert.Equal("cursor_query_mismatch", stringValue changedQuery "errorKind")
            Assert.True(boolValue changedQuery "paginationRestartRequired")
            Assert.False(boolValue changedQuery "retrySameCursor")
            Assert.Empty(changedQuery["sites"] :?> JsonArray)

            try
                // Same line count, source length, semantic use count, and target ranges;
                // only bounded line content changes.
                File.WriteAllText(fixture.ASource, sourceA.Replace("alpha", "omega", StringComparison.Ordinal))
                let! stale = bridge.Find({ baseArgs with maxResults = Some 5; cursor = Some cursor })
                Assert.Equal("invalid_cursor", stringValue stale "status")
                Assert.Equal("cursor_stale", stringValue stale "errorKind")
                Assert.True(boolValue stale "paginationRestartRequired")
                Assert.False(boolValue stale "retrySameCursor")
                Assert.Empty(stale["sites"] :?> JsonArray)
            finally
                fixture.RestoreSource()
        }

    [<Fact>]
    member _.``insertion and deletion before the prior offset both make the cursor stale``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            fixture.RestoreSource()
            let bridge = FcsBridge()
            let baseArgs = findArgs fixture.Solution

            try
                let! beforeInsertion = bridge.Find(baseArgs)
                let insertionCursor = stringValue beforeInsertion "nextCursor"

                File.WriteAllText(
                    fixture.ASource,
                    sourceA.Replace("let a1", "let inserted = target - 1" + Environment.NewLine + "let a1", StringComparison.Ordinal)
                )

                let! inserted = bridge.Find({ baseArgs with cursor = Some insertionCursor })
                Assert.Equal("cursor_stale", stringValue inserted "errorKind")

                fixture.RestoreSource()
                let! beforeDeletion = bridge.Find(baseArgs)
                let deletionCursor = stringValue beforeDeletion "nextCursor"

                File.WriteAllText(
                    fixture.ASource,
                    sourceA.Replace("let a1 = target + 1 // alpha" + Environment.NewLine, "", StringComparison.Ordinal)
                )

                let! deleted = bridge.Find({ baseArgs with cursor = Some deletionCursor })
                Assert.Equal("cursor_stale", stringValue deleted "errorKind")
            finally
                fixture.RestoreSource()
        }

    [<Fact>]
    member _.``legacy and out-of-range cursors require restart and missing context stays a normal request error``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            fixture.RestoreSource()
            let bridge = FcsBridge()
            let baseArgs = findArgs fixture.Solution

            let! legacy = bridge.Find({ baseArgs with cursor = Some(encode 1) })
            Assert.Equal("cursor_version_unsupported", stringValue legacy "errorKind")

            let! first = bridge.Find(baseArgs)
            let cursor = stringValue first "nextCursor"
            let decoded =
                match tryDecodeFind cursor with
                | Ok value -> value
                | Error error -> raise (InvalidOperationException(error.Message))

            let beyond = encodeFindV2 Int32.MaxValue decoded.Query decoded.Snapshot
            let! outOfRange = bridge.Find({ baseArgs with cursor = Some beyond })
            Assert.Equal("cursor_out_of_range", stringValue outOfRange "errorKind")

            let! missingContext =
                FcsBridge().Find(
                    { baseArgs with
                        projectPath = None
                        path = None
                        cursor = Some cursor }
                )

            Assert.Equal("invalid_args", stringValue missingContext "status")
            Assert.Null(missingContext["errorKind"])
        }

    [<Fact>]
    member _.``every legacy cursor consumer rejects a tagged find cursor``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let bridge = FcsBridge()
            let hash = String.replicate 43 "A"
            let taggedFindCursor = encodeFindV2 1 hash hash

            let assertMismatchResponse label (response: JsonNode) =
                Assert.Equal("invalid_args", stringValue response "status")

                Assert.Contains(
                    "cursor_tool_mismatch",
                    stringValue response "message",
                    StringComparison.Ordinal
                )

                Assert.True(response["nextCursor"] = null, $"{label} must not mint a cursor")

            let! projectUsesError =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    bridge.ProjectSymbolUses(
                        { path = fixture.ASource
                          text = None
                          projectPath = Some fixture.AProject
                          projectOptions = None
                          symbolQuery = "target"
                          exact = Some true
                          maxResults = Some 1
                          cursor = Some taggedFindCursor }
                    )
                    :> Task)

            Assert.Contains("cursor_tool_mismatch", projectUsesError.Message, StringComparison.Ordinal)

            let! memberUsesError =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    bridge.FindMemberUsages(
                        { typeName = "CursorFixture.A"
                          memberName = "target"
                          path = Some fixture.ASource
                          text = None
                          projectPath = Some fixture.AProject
                          projectOptions = None
                          exact = Some true
                          maxResults = Some 1
                          cursor = Some taggedFindCursor }
                    )
                    :> Task)

            Assert.Contains("cursor_tool_mismatch", memberUsesError.Message, StringComparison.Ordinal)

            let! recordFields =
                bridge.RecordFieldAudit(
                    { typeName = "MissingRecord"
                      fieldName = "MissingField"
                      path = Some fixture.ASource
                      text = None
                      projectPath = Some fixture.AProject
                      projectOptions = None
                      maxResults = Some 1
                      cursor = Some taggedFindCursor }
                )

            assertMismatchResponse "fcs_record_field_audit" recordFields

            let! findSymbolError =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    bridge.FindSymbol(
                        { path = fixture.ASource
                          text = None
                          projectPath = Some fixture.AProject
                          projectOptions = None
                          symbolQuery = "target"
                          exact = Some true
                          maxResults = Some 1
                          contextLines = Some 0
                          includeDeclaration = Some true
                          includeInfo = Some false
                          cursor = Some taggedFindCursor }
                    )
                    :> Task)

            Assert.Contains("cursor_tool_mismatch", findSymbolError.Message, StringComparison.Ordinal)

            let! testsForSymbol =
                bridge.TestsForSymbol(
                    { symbolQuery = "target"
                      exact = Some true
                      path = None
                      text = None
                      projectPath = Some fixture.Solution
                      maxResults = Some 1
                      timeoutMs = Some 30_000
                      cursor = Some taggedFindCursor }
                )

            assertMismatchResponse "fcs_tests_for_symbol" testsForSymbol

            let! outlineError =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    bridge.ProjectOutline(
                        { projectPath = Some fixture.AProject
                          workspacePath = None
                          includePrivate = None
                          includeTests = None
                          includeGeneratedFiles = None
                          maxFiles = Some 1
                          maxResultsPerFile = Some 1
                          summaryOnly = Some true
                          cursor = Some taggedFindCursor
                          filter = None
                          nameContains = None
                          timeoutMs = Some 30_000 }
                    )
                    :> Task)

            Assert.Contains("cursor_tool_mismatch", outlineError.Message, StringComparison.Ordinal)

            let! referenced =
                bridge.ReferencedSymbols(
                    { query = "String"
                      projectPath = Some fixture.AProject
                      includeNonPublic = Some false
                      maxResults = Some 1
                      cursor = Some taggedFindCursor }
                )

            assertMismatchResponse "fcs_referenced_symbols" referenced

            let! nugetTypes =
                bridge.NugetTypes(
                    { packageId = "System.Runtime"
                      projectPath = Some fixture.AProject
                      includeNonPublic = Some false
                      maxResults = Some 1
                      cursor = Some taggedFindCursor }
                )

            assertMismatchResponse "fcs_nuget_types" nugetTypes

            let! nugetMembers =
                bridge.NugetMembers(
                    { packageId = "System.Runtime"
                      typeName = "String"
                      projectPath = Some fixture.AProject
                      includeNonPublic = Some false
                      maxResults = Some 1
                      cursor = Some taggedFindCursor }
                )

            assertMismatchResponse "fcs_nuget_members" nugetMembers

            let! publicApi =
                bridge.PublicApi(
                    { projectPath = Some fixture.AProject
                      includeInternal = Some false
                      namespaceFilter = None
                      maxResults = Some 1
                      cursor = Some taggedFindCursor }
                )

            assertMismatchResponse "fcs_public_api" publicApi
        }

    [<Fact>]
    member _.``solution member loss and gain both make an unchanged-request cursor stale``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            fixture.RestoreSource()
            fixture.RestoreSolution()
            let baseArgs = findArgs fixture.Solution
            let onlyA =
                String.concat
                    Environment.NewLine
                    [ "<Solution>"
                      "  <Project Path=\"A/A.fsproj\" />"
                      "</Solution>" ]

            try
                let! beforeLoss = FcsBridge().Find(baseArgs)
                let lossCursor = stringValue beforeLoss "nextCursor"
                File.WriteAllText(fixture.Solution, onlyA)

                let! lost = FcsBridge().Find({ baseArgs with cursor = Some lossCursor })
                Assert.Equal("cursor_stale", stringValue lost "errorKind")
                Assert.Empty(lost["sites"] :?> JsonArray)

                let! beforeGain = FcsBridge().Find(baseArgs)
                let gainCursor = stringValue beforeGain "nextCursor"
                fixture.RestoreSolution()

                let! gained = FcsBridge().Find({ baseArgs with cursor = Some gainCursor })
                Assert.Equal("cursor_stale", stringValue gained "errorKind")
                Assert.Empty(gained["sites"] :?> JsonArray)

                let! beforeReorder = FcsBridge().Find(baseArgs)
                let reorderCursor = stringValue beforeReorder "nextCursor"

                File.WriteAllText(
                    fixture.Solution,
                    String.concat
                        Environment.NewLine
                        [ "<Solution>"
                          "  <Project Path=\"B/B.fsproj\" />"
                          "  <Project Path=\"A/A.fsproj\" />"
                          "</Solution>" ]
                )

                let! reordered = FcsBridge().Find({ baseArgs with cursor = Some reorderCursor })
                Assert.Equal("cursor_stale", stringValue reordered "errorKind")
                Assert.Empty(reordered["sites"] :?> JsonArray)
            finally
                fixture.RestoreSolution()
        }

    [<Fact>]
    member _.``newly available evidence makes a cursor over stable missing coverage stale``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            fixture.RestoreSource()
            fixture.RestoreSolution()
            let cDirectory = Path.Combine(fixture.Root, "C")
            let cProject = Path.Combine(cDirectory, "C.fsproj")
            let cSource = Path.Combine(cDirectory, "C.fs")

            let withMissingC =
                String.concat
                    Environment.NewLine
                    [ "<Solution>"
                      "  <Project Path=\"A/A.fsproj\" />"
                      "  <Project Path=\"B/B.fsproj\" />"
                      "  <Project Path=\"C/C.fsproj\" />"
                      "</Solution>" ]

            try
                File.WriteAllText(fixture.Solution, withMissingC)
                let baseArgs = findArgs fixture.Solution
                let! missing = FcsBridge().Find(baseArgs)
                Assert.Equal(1, missing["projectsMissing"].GetValue<int>())
                let cursor = stringValue missing "nextCursor"

                Directory.CreateDirectory(cDirectory) |> ignore
                File.WriteAllText(cProject, project "C.fs")

                File.WriteAllText(
                    cSource,
                    String.concat
                        Environment.NewLine
                        [ "module CursorFixture.C"
                          "let target = 20"
                          "let c1 = target + 1" ]
                )

                let buildExitCode, buildLog = runBuild cProject
                Assert.True((buildExitCode = 0), buildLog)

                let! available = FcsBridge().Find({ baseArgs with cursor = Some cursor })
                Assert.Equal("cursor_stale", stringValue available "errorKind")
                Assert.Empty(available["sites"] :?> JsonArray)
            finally
                fixture.RestoreSolution()
        }

    [<Fact>]
    member _.``controlled between-project mutation never delivers a mixed continuation page``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            fixture.RestoreSource()
            fixture.RestoreSolution()
            let baseArgs = findArgs fixture.Solution
            let! first = FcsBridge().Find(baseArgs)
            let cursor = stringValue first "nextCursor"
            let mutable mutated = false

            try
                let mutatingBridge =
                    FcsBridge(
                        projectEvaluationBeforeLoadOverride =
                            (fun path ->
                                if
                                    not mutated
                                    && String.Equals(
                                        Path.GetFullPath(path),
                                        Path.GetFullPath(fixture.BProject),
                                        StringComparison.Ordinal
                                    )
                                then
                                    mutated <- true

                                    File.WriteAllText(
                                        fixture.ASource,
                                        sourceA.Replace("alpha", "omega", StringComparison.Ordinal)
                                    )

                                Task.CompletedTask)
                    )

                let! continuation = mutatingBridge.Find({ baseArgs with cursor = Some cursor })
                Assert.True(mutated, "The test must mutate A while the B project is being admitted.")
                Assert.Equal("invalid_cursor", stringValue continuation "status")
                Assert.Equal("cursor_stale", stringValue continuation "errorKind")
                Assert.Empty(continuation["sites"] :?> JsonArray)
            finally
                fixture.RestoreSource()
        }

    [<Fact>]
    member _.``changing the symbol at an unchanged requested position makes the cursor stale``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            fixture.RestoreSource()

            let positionArgs =
                { findArgs fixture.Solution with
                    query = "symbol-at-position"
                    kind = Some "position"
                    path = Some fixture.ASource
                    line = Some 2
                    character = Some 10
                    word = None
                    occurrence = None }

            try
                let! first = FcsBridge().Find(positionArgs)
                let cursor = stringValue first "nextCursor"
                File.WriteAllText(fixture.ASource, sourceA.Replace("target", "otherx", StringComparison.Ordinal))

                let! changed = FcsBridge().Find({ positionArgs with cursor = Some cursor })
                Assert.Equal("invalid_cursor", stringValue changed "status")
                Assert.Equal("cursor_stale", stringValue changed "errorKind")
                Assert.Empty(changed["sites"] :?> JsonArray)
            finally
                fixture.RestoreSource()
        }

    [<Fact>]
    member _.``cold position continuation timeout permits retrying the same cursor``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            fixture.RestoreSource()

            let positionArgs =
                { findArgs fixture.Solution with
                    query = "symbol-at-position"
                    kind = Some "position"
                    path = Some fixture.ASource
                    line = Some 2
                    character = Some 10
                    word = None
                    occurrence = None }

            let! first = FcsBridge().Find(positionArgs)
            let cursor = stringValue first "nextCursor"
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let timedBridge =
                FcsBridge(
                    findPositionResolutionBeforeComputeOverride =
                        (fun () ->
                            task {
                                entered.TrySetResult(()) |> ignore
                                do! Task.Delay(50)
                            }
                            :> Task),
                    findDeadlineSignalOverride = (fun () -> entered.Task :> Task)
                )

            let! timed = timedBridge.Find({ positionArgs with cursor = Some cursor })
            Assert.Equal("invalid_cursor", stringValue timed "status")
            Assert.Equal("cursor_validation_incomplete", stringValue timed "errorKind")
            Assert.False(boolValue timed "paginationRestartRequired")
            Assert.True(boolValue timed "retrySameCursor")
            Assert.Empty(timed["sites"] :?> JsonArray)

            let! retried =
                FcsBridge().Find(
                    { positionArgs with
                        maxResults = Some 3
                        timeoutMs = Some 60_000
                        cursor = Some cursor }
                )

            Assert.Equal("succeeded", stringValue retried "status")
            Assert.Equal(1, retried["pageOffset"].GetValue<int>())
            Assert.NotEmpty(retried["sites"] :?> JsonArray)
        }

    [<Fact>]
    member _.``continuation timeout returns no sites and permits same-cursor retry``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            fixture.RestoreSource()
            let baseArgs = findArgs fixture.Solution
            let! first = FcsBridge().Find(baseArgs)
            let cursor = stringValue first "nextCursor"

            let timedBridge =
                FcsBridge(findResponseDeadlineSignalOverride = (fun () -> Task.CompletedTask))

            let! timed = timedBridge.Find({ baseArgs with cursor = Some cursor })
            Assert.Equal("invalid_cursor", stringValue timed "status")
            Assert.Equal("cursor_validation_incomplete", stringValue timed "errorKind")
            Assert.False(boolValue timed "paginationRestartRequired")
            Assert.True(boolValue timed "retrySameCursor")
            Assert.Null(timed["nextCursor"])
            Assert.Empty(timed["sites"] :?> JsonArray)

            let! retried =
                FcsBridge().Find(
                    { baseArgs with
                        maxResults = Some 3
                        timeoutMs = Some 60_000
                        cursor = Some cursor }
                )

            Assert.Equal("succeeded", stringValue retried "status")
            Assert.Equal(1, retried["pageOffset"].GetValue<int>())
            Assert.NotEmpty(retried["sites"] :?> JsonArray)
        }

    [<Fact>]
    member _.``deadline-partial initial page is useful but cursorless and requires restart``() : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            fixture.RestoreSource()
            let expired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let bridge =
                FcsBridge(
                    projectEvaluationBeforeLoadOverride =
                        (fun path ->
                            if String.Equals(Path.GetFullPath(path), Path.GetFullPath(fixture.BProject), StringComparison.Ordinal) then
                                expired.TrySetResult(()) |> ignore

                            Task.CompletedTask),
                    findDeadlineSignalOverride = (fun () -> expired.Task :> Task)
                )

            let! partial = bridge.Find({ findArgs fixture.Solution with maxResults = Some 2 })
            Assert.Equal("partial", stringValue partial "status")
            Assert.Equal("partial", stringValue partial "deliveryStatus")
            Assert.False(boolValue partial["coverage"] "complete")
            Assert.False(boolValue partial["resolution"] "complete")
            Assert.True(boolValue partial "truncated")
            Assert.Null(partial["nextCursor"])
            Assert.True(boolValue partial "paginationRestartRequired")
            Assert.True(boolValue partial "totalEstimateIsLowerBound")
            Assert.Equal("deadline_incomplete", stringValue partial "paginationIncompleteReason")
            Assert.NotEmpty(partial["sites"] :?> JsonArray)

            let! complete =
                FcsBridge().Find({ findArgs fixture.Solution with maxResults = Some 100; timeoutMs = Some 60_000 })

            Assert.Equal("succeeded", stringValue complete "status")
            Assert.True(boolValue complete["coverage"] "complete")
            Assert.True(complete["totalSites"].GetValue<int>() > partial["totalSites"].GetValue<int>())
        }

[<Fact>]
let ``canonical query materializes defaults normalizes paths and excludes page and deadline budgets`` () =
    let root = Path.Combine(Path.GetTempPath(), "cursor-query-root")
    let normalizedProject = Path.Combine(root, "Project.fsproj")

    let omitted =
        { findArgs normalizedProject with
            kind = None
            scope = None
            exact = None
            contextLines = None
            includeDeclaration = None
            includeInfo = None
            includePerProject = None
            includeSiteTypes = None }

    let explicitDefaults =
        { omitted with
            kind = Some "auto"
            scope = Some "auto"
            exact = Some true
            contextLines = Some 0
            includeDeclaration = Some true
            includeInfo = Some false
            includePerProject = Some true
            includeSiteTypes = Some false
            maxResults = Some 999
            timeoutMs = Some 1
            cursor = Some "ignored" }

    let identity args =
        FindCursorContract.queryModel args args.query "auto" "auto" true 0 true false true false
        |> findQueryIdentityV2

    Assert.Equal(identity omitted, identity explicitDefaults)

    let nonCanonicalPath = Path.Combine(root, "nested", "..", "Project.fsproj")
    Assert.Equal(identity { omitted with projectPath = Some nonCanonicalPath }, identity omitted)

    let baseline = identity omitted

    let changed =
        [ identity { omitted with query = "other" }
          FindCursorContract.queryModel omitted omitted.query "field" "auto" true 0 true false true false
          |> findQueryIdentityV2
          FindCursorContract.queryModel omitted omitted.query "auto" "project" true 0 true false true false
          |> findQueryIdentityV2
          identity { omitted with projectPath = Some(Path.Combine(root, "Other.fsproj")) }
          identity { omitted with path = Some(Path.Combine(root, "A.fs")) }
          FindCursorContract.queryModel omitted omitted.query "auto" "auto" false 0 true false true false
          |> findQueryIdentityV2
          identity { omitted with ``member`` = Some "Run" }
          identity { omitted with field = Some "Value" }
          FindCursorContract.queryModel omitted omitted.query "auto" "auto" true 1 true false true false
          |> findQueryIdentityV2
          FindCursorContract.queryModel omitted omitted.query "auto" "auto" true 0 false false true false
          |> findQueryIdentityV2
          FindCursorContract.queryModel omitted omitted.query "auto" "auto" true 0 true true true false
          |> findQueryIdentityV2
          FindCursorContract.queryModel omitted omitted.query "auto" "auto" true 0 true false false false
          |> findQueryIdentityV2
          FindCursorContract.queryModel omitted omitted.query "auto" "auto" true 0 true false true true
          |> findQueryIdentityV2 ]

    changed
    |> List.iter (fun value -> Assert.False(String.Equals(baseline, value, StringComparison.Ordinal)))

    let positionArgs =
        { omitted with
            kind = Some "position"
            path = Some(Path.Combine(root, "A.fs"))
            line = Some 4
            character = Some 8
            word = Some "target"
            occurrence = Some 0 }

    let positionIdentity args =
        FindCursorContract.queryModel args args.query "position" "auto" true 0 true false true false
        |> findQueryIdentityV2

    let positionBaseline = positionIdentity positionArgs

    [ positionIdentity { positionArgs with line = Some 5 }
      positionIdentity { positionArgs with character = Some 9 }
      positionIdentity { positionArgs with word = Some "other" }
      positionIdentity { positionArgs with occurrence = Some 1 } ]
    |> List.iter (fun value ->
        Assert.False(String.Equals(positionBaseline, value, StringComparison.Ordinal)))
