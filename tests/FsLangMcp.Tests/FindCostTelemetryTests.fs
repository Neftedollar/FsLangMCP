module FsLangMcp.Tests.FindCostTelemetryTests

open System
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.Cursor
open FsLangMcp.FcsBridge
open FsLangMcp.Tests.FindTests

let private phases =
    [| "admission"; "positionResolution"; "targetDiscovery"; "projectOptions"
       "snapshot"; "fcsSweep"; "classification"; "fsacFallback"
       "responseConstruction"; "unattributed" |]

let private number (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private state (node: JsonNode) (key: string) = node[key].GetValue<string>()

let private assertAccounting (response: JsonNode) =
    let timings = response["phaseTimingsMs"].AsObject()
    Assert.Equal(phases.Length, timings.Count)
    for phase in phases do Assert.True(number timings phase >= 0, phase)
    Assert.Equal(number response "elapsedMs", phases |> Array.sumBy (number timings))
    Assert.True(renderedLength response <= FindResponseBudget.MaxSerializedChars)
    for cache in [ "projectOptions"; "projectUses" ] do
        let entry = response["cacheState"][cache]
        Assert.True(number entry "hits" >= 0)
        Assert.True(number entry "misses" >= 0)
        Assert.True(number entry "incomplete" >= 0)

let private args project : FindArgs =
    { query = "TraderRole"; kind = Some "symbol"; scope = None; exact = Some true
      ``member`` = None; field = None; path = None; line = None; word = None
      occurrence = None; character = None; contextLines = Some 0
      includeDeclaration = Some true; includeInfo = Some false
      includePerProject = Some true; includeSiteTypes = Some false
      projectPath = Some project; maxResults = Some 80; timeoutMs = Some 120_000
      cursor = None }

[<Fact>]
let ``exclusive phase intervals and unattributed remainder exactly partition sampled elapsed time`` () =
    let mutable clock = 0
    let telemetry = FindCostTelemetry(fun () -> clock)
    // Independent clock increments make accidental overlapping accumulation observable.
    for index in 1 .. phases.Length - 1 do
        clock <- clock + index
        telemetry.SetPhase(phases[index])
    clock <- clock + phases.Length
    let response = telemetry.Attach(jobj [] :> JsonNode)

    assertAccounting response
    for index in 0 .. phases.Length - 1 do
        Assert.Equal(index + 1, number (response["phaseTimingsMs"]) (phases[index]))

[<Fact>]
let ``cache observations distinguish unfinished lookups from caches never reached`` () =
    let telemetry = FindCostTelemetry(fun () -> 0)
    telemetry.BeginCacheObservation("projectOptions")
    telemetry.RecordCacheObservation("projectOptions", true)
    telemetry.BeginCacheObservation("projectOptions")
    let response = telemetry.Attach(jobj [] :> JsonNode)

    Assert.Equal("incomplete", state (response["cacheState"]["projectOptions"]) "state")
    Assert.Equal(1, number (response["cacheState"]["projectOptions"]) "hits")
    Assert.Equal(1, number (response["cacheState"]["projectOptions"]) "incomplete")
    Assert.Equal("not_observed", state (response["cacheState"]["projectUses"]) "state")

[<Fact>]
let ``oversized early response replacement is decorated before its final size guard`` () =
    let deadline = FindRequestDeadline(30_000)
    deadline.BeginResponseConstruction()
    let response = deadline.GuardResponse(jobj [ "message", jstr (String.replicate 70_000 "x") ])

    Assert.Equal("find_response_exceeds_budget", state response "errorCode")
    assertAccounting response

[<Theory>]
[<InlineData("deadline")>]
[<InlineData("invalid_args")>]
[<InlineData("invalid_cursor")>]
let ``early return envelopes always contain the complete fixed telemetry shape`` (scenario: string) : Task =
    task {
        let baseline = args "/not-loaded/Telemetry.fsproj"
        let request =
            match scenario with
            | "deadline" -> { baseline with timeoutMs = Some 0 }
            | "invalid_args" -> { baseline with query = "" }
            | "invalid_cursor" -> { baseline with cursor = Some "not-a-cursor" }
            | unexpected -> invalidArg (nameof scenario) unexpected
        let! response = FcsBridge().Find(request)

        assertAccounting response
        Assert.Equal("not_observed", state (response["cacheState"]["projectOptions"]) "state")
        Assert.Equal("not_observed", state (response["cacheState"]["projectUses"]) "state")
    }

type FindCostTelemetryTests(fixture: FindFixture) =
    interface IClassFixture<FindFixture>

    [<Fact>]
    member _.``real cold and warm cache decisions survive exclusion of zero-match project rows`` () : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let bridge = FcsBridge()
            let request = { args fixture.DomainFsproj with query = "DefinitelyNoSuchTelemetrySymbol" }
            let! cold = bridge.Find(request)
            let coldLoads = bridge.ProjectOptionsLoadCount
            let coldSweeps = bridge.ProjectUsesStartedCount
            let! warm = bridge.Find(request)

            // Independent worker counters prove that the alleged hits really avoided
            // a second load/sweep; response counters alone would only test self-reporting.
            Assert.Equal(1L, coldLoads)
            Assert.Equal(1, int coldSweeps)
            Assert.Equal(coldLoads, bridge.ProjectOptionsLoadCount)
            Assert.Equal(coldSweeps, bridge.ProjectUsesStartedCount)
            for response in [ cold; warm ] do
                assertAccounting response
                Assert.Equal("succeeded", state response "status")
                Assert.Equal(1, number response "projectsAnalyzed")
                Assert.Empty(response["perProject"].AsArray())
            for cache in [ "projectOptions"; "projectUses" ] do
                Assert.Equal(1, number (cold["cacheState"][cache]) "misses")
                Assert.Equal(0, number (cold["cacheState"][cache]) "hits")
                Assert.Equal(1, number (warm["cacheState"][cache]) "hits")
                Assert.Equal(0, number (warm["cacheState"][cache]) "misses")
                Assert.Equal("observed", state (warm["cacheState"][cache]) "state")
        }

    [<Fact>]
    member _.``partial missing-member result retains observed cache evidence`` () : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let! response = FcsBridge().Find(args fixture.MissingMemberSlnx)

            assertAccounting response
            Assert.Equal("partial", state response "status")
            Assert.Equal(1, number response "projectsMissing")
            Assert.Equal(1, number (response["cacheState"]["projectOptions"]) "misses")
            Assert.Equal(1, number (response["cacheState"]["projectUses"]) "misses")
        }

    [<Fact>]
    member _.``project-options timeout reports incomplete not a fabricated cache miss`` () : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let expired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let deadline = FindRequestDeadline(30_000, semanticExpirySignal = expired.Task)
            let bridge =
                FcsBridge(projectEvaluationBeforeLoadOverride = fun _ ->
                    expired.TrySetResult(()) |> ignore
                    release.Task :> Task)
            let retained = ResizeArray<Task>()
            let! response =
                bridge.FindWithinDeadline(args fixture.DomainFsproj, deadline, CancellationToken.None, retained.Add, None)
            release.TrySetResult(()) |> ignore
            // The deadline owns only caller waiting; drain retained worker faults here.
            for worker in retained do
                try do! worker.WaitAsync(TimeSpan.FromSeconds 10.)
                with :? TimeoutException -> ()

            assertAccounting response
            Assert.Equal(1, number response "projectsTimedOut")
            Assert.Equal("incomplete", state (response["cacheState"]["projectOptions"]) "state")
            Assert.Equal(1, number (response["cacheState"]["projectOptions"]) "incomplete")
            Assert.Equal(0, number (response["cacheState"]["projectOptions"]) "misses")
            Assert.Equal("not_observed", state (response["cacheState"]["projectUses"]) "state")
        }

    [<Fact>]
    member _.``FSAC wait and response construction are attributed to separate phases`` () : Task =
        task {
            Assert.True(fixture.BuildExitCode = 0, fixture.BuildLog)
            let bridge = FcsBridge(findResponseConstructionBeforeStartOverride = fun () -> Task.Delay 30)
            let request = { args fixture.DomainFsproj with query = "DefinitelyNoSuchTelemetrySymbol" }
            let! response =
                bridge.Find(request, fsacProbe = fun _ -> task {
                    do! Task.Delay 30
                    return FindFsacProbeResult.Available 0
                })

            assertAccounting response
            Assert.Equal("succeeded", state response "status")
            Assert.True(number (response["phaseTimingsMs"]) "fsacFallback" >= 20)
            Assert.True(number (response["phaseTimingsMs"]) "responseConstruction" >= 20)
        }
