module FsLangMcp.Tests.FindInventoryIntegrationTests

open System
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Xunit
open FsLangMcp.Types
open FsLangMcp.FcsBridge
open FsLangMcp.Program
open FsLangMcp.Tests.FindTests

let private args project query : FindArgs =
    { query = query; kind = Some "symbol"; scope = None; exact = Some true
      ``member`` = None; field = None; path = None; line = None; word = None
      occurrence = None; character = None; contextLines = Some 0
      includeDeclaration = Some true; includeInfo = Some false; includePerProject = Some true
      includeSiteTypes = Some false; projectPath = Some project; maxResults = Some 1000
      timeoutMs = Some 120_000; cursor = None }

let private number (node: JsonNode) (key: string) = node[key].GetValue<int>()
let private text (node: JsonNode) (key: string) = node[key].GetValue<string>()

type InventoryIntegration(fixture: FindFixture) =
    interface IClassFixture<FindFixture>

    [<Fact>]
    member _.``real FCS batch counts equal independent single searches with one sweep per project`` () : Task =
        task {
            Assert.Equal(0, fixture.BuildExitCode)
            let bridge = FcsBridge()
            let queries = [| "TraderRole"; "GrainContract"; "DefinitelyMissingInventorySymbol" |]
            let before = bridge.ProjectUsesStartedCount
            let! inventory = bridge.Find(args fixture.Slnx queries[0], inventoryQueries = queries)
            Assert.True(inventory["countsComplete"].GetValue<bool>(), inventory.ToJsonString())
            Assert.Equal(3L, bridge.ProjectUsesStartedCount - before)
            let ledger = inventory["projectLedger"] :?> JsonArray
            Assert.Equal(3, ledger.Count)
            Assert.Equal(3, number (inventory["cacheState"]["projectUses"]) "misses")
            Assert.Equal("not_applicable_inventory", text inventory "fsacFallbackState")

            for index in 0..queries.Length - 1 do
                let! single = bridge.Find(args fixture.Slnx queries[index])
                Assert.True(single["resultSetComplete"].GetValue<bool>(), single.ToJsonString())
                let row = inventory["queries"][index]
                let sites = single["sites"] :?> JsonArray
                Assert.Equal(sites.Count, number row "uniqueSites")
                Assert.Equal(sites |> Seq.filter (fun site -> text site "kind" = "definition") |> Seq.length, number row "definitions")
                Assert.Equal(sites |> Seq.filter (fun site -> text site "kind" = "reference") |> Seq.length, number row "references")

                for project in row["perProject"].AsArray() do
                    let projectIndex = number project "projectIndex"
                    Assert.Equal(projectIndex, number (ledger[projectIndex]) "projectIndex")
                    Assert.True((ledger[projectIndex]["fsproj"]).GetValue<string>().EndsWith(".fsproj"))

            Assert.Equal(before + 3L, bridge.ProjectUsesStartedCount)
            Assert.True(renderedLength inventory <= FindResponseBudget.MaxSerializedChars)
            Assert.Equal(number inventory "elapsedMs", inventory["phaseTimingsMs"].AsObject() |> Seq.sumBy (fun pair -> pair.Value.GetValue<int>()))
        }

    [<Fact>]
    member _.``missing member cannot retarget ledger indices or turn zero into absence`` () : Task =
        task {
            let bridge = FcsBridge()
            let queries = [| "TraderRole"; "DefinitelyMissingInventorySymbol" |]
            let! result = bridge.Find(args fixture.MissingMemberSlnx queries[0], inventoryQueries = queries)
            Assert.False(result["countsComplete"].GetValue<bool>())
            let ledger = result["projectLedger"] :?> JsonArray
            Assert.Equal(2, ledger.Count)
            Assert.Equal(fixture.DomainFsproj, (ledger[0]["fsproj"]).GetValue<string>())
            Assert.Equal("analyzed", text (ledger[0]) "status")
            Assert.Equal("missing", text (ledger[1]) "status")
            let references = result.["queries"].[0].["perProject"].AsArray()
            Assert.Equal(0, number (references[0]) "projectIndex")
            Assert.True((references[0]["countsComplete"]).GetValue<bool>())
            Assert.Null(result.["queries"].[1].["matched"])
            Assert.Equal("indeterminate", text (result["queries"][1]) "outcome")
        }

    [<Fact>]
    member _.``file inventory filters every counter before declaration selection`` () : Task =
        task {
            let bridge = FcsBridge()
            let request = { args fixture.DomainFsproj "TraderRole" with scope = Some "file"; path = Some fixture.DomainFs; includeDeclaration = Some false }
            let! inventory = bridge.Find(request, inventoryQueries = [| "TraderRole" |])
            let! single = bridge.Find request
            let row = inventory["queries"][0]
            Assert.True(row["countsComplete"].GetValue<bool>(), inventory.ToJsonString())
            Assert.Equal(0, number row "definitions")
            Assert.Equal(single["sites"].AsArray().Count, number row "references")
        }

    [<Fact>]
    member _.``zero budget inventory never emits a misleading count row or starts FCS`` () : Task =
        task {
            let bridge = FcsBridge()
            let request = { args fixture.Slnx "Missing" with timeoutMs = Some 0 }
            let! result = bridge.Find(request, inventoryQueries = [| "Missing" |])
            Assert.True(result["countsOnly"].GetValue<bool>())
            Assert.False(result["countsReturned"].GetValue<bool>())
            Assert.False(result["countsComplete"].GetValue<bool>())
            Assert.Equal(0L, bridge.ProjectUsesStartedCount)
        }

    [<Fact>]
    member _.``inventory budget overflow delivers bounded typed recovery not truncated exact counts`` () : Task =
        task {
            let bridge = FcsBridge()
            let queries = Array.init 50 (fun index -> $"Missing{index}" + String.replicate 2_000 "界")
            queries[0] <- "TraderRole"
            let! result = bridge.Find(args fixture.DomainFsproj queries[0], inventoryQueries = queries)
            Assert.Equal("find_inventory_response_budget", text result "errorKind")
            Assert.False(result["countsReturned"].GetValue<bool>())
            Assert.False(result["resultSetComplete"].GetValue<bool>())
            Assert.Empty(result["queries"].AsArray())
            Assert.Null(result["nextCursor"])
            Assert.Equal("split_queries_or_narrow_scope", text (result["recovery"]) "action")
            Assert.True(renderedLength result <= FindResponseBudget.MaxSerializedChars)
        }

    [<Fact>]
    member _.``slow inventory test classification retains admission until its worker really ends`` () : Task =
        task {
            use gate = new SemaphoreSlim(1, 1)
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let expired = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let deadline = FindRequestDeadline(30_000, semanticExpirySignal = expired.Task)
            let bridge = FcsBridge(findTargetDiscoveryBeforeStepOverride = (fun phase _ ->
                if phase = "inventory-project-classification" then
                    entered.TrySetResult(()) |> ignore
                    release.Task.GetAwaiter().GetResult()))
            let request = args fixture.DomainFsproj "TraderRole"
            let operation =
                runLimitedWithFindDeadlineRetained gate CancellationToken.None deadline (fun shared retain ->
                    bridge.FindWithinDeadline(request, shared, CancellationToken.None, retain, None, inventoryQueries = [| "TraderRole" |]))

            try
                do! entered.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
                expired.TrySetResult(()) |> ignore
                let! result = operation.WaitAsync(TimeSpan.FromSeconds(5.0))
                Assert.False(result["countsReturned"].GetValue<bool>())
                Assert.Equal(0L, bridge.ProjectUsesStartedCount)
                Assert.Equal(0, gate.CurrentCount)
            finally
                release.TrySetResult(()) |> ignore

            let until = Diagnostics.Stopwatch.StartNew()
            while gate.CurrentCount = 0 && until.Elapsed < TimeSpan.FromSeconds(5.0) do
                do! Task.Delay(25)
            Assert.Equal(1, gate.CurrentCount)
        }
