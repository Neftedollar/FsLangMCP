module FsLangMcp.Tests.RuntimeStatusTests

// ─── Snapshot-test pattern (Verify.Xunit) ────────────────────────────────────
//
// Several tests below use Verify (snapshot / approval testing) instead of a
// hand-walked tree of `Assert.NotNull(node …)` calls. Each snapshot covers the
// full shape of the `runtime_status` JSON payload (or a focused slice of it),
// which catches regressions in any field — including ones the test author did
// not anticipate.
//
// How snapshots work
//   • Each `Verifier.Verify(...)` call produces a `*.received.txt` (or .json)
//     and diffs it against the matching `*.verified.txt`/`*.verified.json`.
//   • Volatile fields (PIDs, byte counts, GC counters, uptime, …) are scrubbed
//     by `VerifyModuleInitializer.fs` so they don't cause false diffs.
//
// Approving a new / changed snapshot
//   1. Run the failing test once. Verify writes `<TestName>.received.<ext>`
//      next to the source file.
//   2. Inspect the diff — is the change intentional? If yes:
//        mv <TestName>.received.<ext> <TestName>.verified.<ext>
//      and commit the .verified.* file.
//   3. If a *new* field is volatile, add a scrubber to VerifyModuleInitializer
//      rather than baking the changing value into the snapshot.
//
// Adding a snapshot test
//   • Make the test return `Task` and call `Verifier.Verify(jsonString,
//     extension = "json").ToTask()`. xUnit will await the task.
//   • For sub-trees, snapshot just the slice you care about so the snapshot
//     stays focused (see `…fcs slice…` etc. below).

open System
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open VerifyXunit
open FsLangMcp.Types
open FsLangMcp.RuntimeStatus

// ─── int64 > Int32.MaxValue round-trip ────────────────────────────────────────

[<Fact>]
let ``heapJson round-trips int64 values larger than Int32.MaxValue`` () =
    // 5 GB — well above Int32.MaxValue (≈2.147 GB). Before the fix, the cast
    // `int totalBytes` would have silently truncated this to a wrong/negative value.
    let fiveGb = 5_000_000_000L
    let info =
        { TotalBytes = fiveGb
          Gen0Bytes   = fiveGb + 1L
          Gen1Bytes   = fiveGb + 2L
          Gen2Bytes   = fiveGb + 3L
          LohBytes    = fiveGb + 4L
          PohBytes    = fiveGb + 5L
          Fragmentation = 0.1234 }
    let node = heapJson info
    Assert.Equal(fiveGb,     node["totalBytes"].GetValue<int64>())
    Assert.Equal(fiveGb + 1L, node["gen0Bytes"].GetValue<int64>())
    Assert.Equal(fiveGb + 2L, node["gen1Bytes"].GetValue<int64>())
    Assert.Equal(fiveGb + 3L, node["gen2Bytes"].GetValue<int64>())
    Assert.Equal(fiveGb + 4L, node["lohBytes"].GetValue<int64>())
    Assert.Equal(fiveGb + 5L, node["pohBytes"].GetValue<int64>())

// ─── Test helpers ─────────────────────────────────────────────────────────────

let private defaultArgs: RuntimeStatusArgs =
    { includeFcsCacheStats = None
      includeAssemblyCounts = None
      includeChildProcesses = None
      includeProcessIds = None }

let private defaultConfig: FcsCheckerConfig =
    { KeepAssemblyContents = true
      KeepAllBackgroundResolutions = true
      KeepAllBackgroundSymbolUses = true
      ProjectCacheSize = 3 }

let private node (parent: JsonNode) (key: string) : JsonNode = parent[key]

let private getStr (parent: JsonNode) (key: string) : string =
    parent[key].GetValue<string>()

let private getIntAt (parent: JsonNode) (key: string) : int =
    parent[key].GetValue<int>()

let private getBoolAt (parent: JsonNode) (key: string) : bool =
    parent[key].GetValue<bool>()

let private prettyJson (node: JsonNode) : string =
    let opts = JsonSerializerOptions(WriteIndented = true)
    node.ToJsonString(opts)

// ─── Group A: output shape with default args ──────────────────────────────────

[<Fact>]
let ``buildSnapshot with default args returns status ok`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    Assert.Equal("ok", getStr result "status")

// Snapshot test: covers the entire default-args shape including process,
// managedHeap, gcInfo, assemblies, pid, fcs, children. Replaces seven
// hand-written `Assert.NotNull(node …)` "presence" tests.
[<Fact>]
let ``buildSnapshot with default args matches snapshot`` () : Task =
    let result = buildSnapshot defaultArgs defaultConfig None
    VerifyModuleInitializer.init ()
    Verifier.Verify(prettyJson result, extension = "json").ToTask() :> Task

[<Fact>]
let ``buildSnapshot managedHeap has expected numeric fields`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let proc = node result "process"
    let heap = node proc "managedHeap"
    let fields = [ "totalBytes"; "gen0Bytes"; "gen1Bytes"; "gen2Bytes"; "lohBytes"; "pohBytes" ]

    for field in fields do
        // Heap byte fields are int64 — use GetValue<int64> to avoid truncation
        let value = heap[field].GetValue<int64>()
        Assert.True(value >= 0L, $"Field {field} should be non-negative, got {value}")

[<Fact>]
let ``buildSnapshot managedHeap has fragmentation between 0 and 1`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let proc = node result "process"
    let heap = node proc "managedHeap"
    let fragNode = node heap "fragmentation"
    let frag: double = fragNode.GetValue()
    Assert.True(frag >= 0.0 && frag <= 1.0, $"fragmentation should be in [0,1], got {frag}")

[<Fact>]
let ``buildSnapshot gcInfo has isServerGc field`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let proc = node result "process"
    let gcInfo = node proc "gcInfo"
    let isServer = node gcInfo "isServerGc"
    // Just assert the field exists and is a bool
    let _ = isServer.GetValue<bool>()
    ()

[<Fact>]
let ``buildSnapshot gcInfo has totalCollections with gen0 gen1 gen2`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let proc = node result "process"
    let gcInfo = node proc "gcInfo"
    let cols = node gcInfo "totalCollections"
    let gen0 = getIntAt cols "gen0"
    let gen1 = getIntAt cols "gen1"
    let gen2 = getIntAt cols "gen2"
    Assert.True(gen0 >= 0)
    Assert.True(gen1 >= 0)
    Assert.True(gen2 >= 0)

[<Fact>]
let ``buildSnapshot gcInfo totalAllocated is non-negative`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let proc = node result "process"
    let gcInfo = node proc "gcInfo"
    let allocatedNode = node gcInfo "totalAllocated"
    let allocated: int64 = allocatedNode.GetValue()
    Assert.True(allocated >= 0L)

[<Fact>]
let ``buildSnapshot assemblies loaded count is positive`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let proc = node result "process"
    let assemblies = node proc "assemblies"
    let loaded = getIntAt assemblies "loaded"
    Assert.True(loaded > 0, $"Loaded assembly count should be > 0, got {loaded}")

[<Fact>]
let ``buildSnapshot pid matches current process`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let proc = node result "process"
    let pid = getIntAt proc "pid"
    Assert.Equal(Environment.ProcessId, pid)

[<Fact>]
let ``buildSnapshot fcs has projectCacheSize matching config`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let fcs = node result "fcs"
    let size = getIntAt fcs "projectCacheSize"
    Assert.Equal(3, size)

[<Fact>]
let ``buildSnapshot fcs checker block reflects config flags`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let fcs = node result "fcs"
    let checker = node fcs "checker"
    Assert.True(getBoolAt checker "keepAssemblyContents")
    Assert.True(getBoolAt checker "keepAllBackgroundResolutions")
    Assert.True(getBoolAt checker "keepAllBackgroundSymbolUses")

[<Fact>]
let ``buildSnapshot fcs checker false flags are reflected`` () =
    let config =
        { defaultConfig with
            KeepAssemblyContents = false
            KeepAllBackgroundResolutions = false
            KeepAllBackgroundSymbolUses = false }

    let result = buildSnapshot defaultArgs config None
    let fcs = node result "fcs"
    let checker = node fcs "checker"
    Assert.False(getBoolAt checker "keepAssemblyContents")
    Assert.False(getBoolAt checker "keepAllBackgroundResolutions")
    Assert.False(getBoolAt checker "keepAllBackgroundSymbolUses")

[<Fact>]
let ``buildSnapshot fcs incrementalBuilders count is null because FCS does not expose it`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let fcs = node result "fcs"
    let builders = node fcs "incrementalBuilders"
    // FSharpChecker does not expose live builder count on net10.0 — emit null, not a misleading 0
    Assert.Null(node builders "count")

[<Fact>]
let ``buildSnapshot fcs incrementalBuilders approximateBytes is null`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let fcs = node result "fcs"
    let builders = node fcs "incrementalBuilders"
    let approx = node builders "approximateBytes"
    Assert.Null(approx)

// ─── Group B: no-FSAC case → children is empty array ────────────────────────

[<Fact>]
let ``buildSnapshot with no FSAC process returns empty children array`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let children = node result "children" :?> JsonArray
    Assert.Equal(0, children.Count)

[<Fact>]
let ``buildSnapshot with None FSAC process returns empty children array`` () =
    let args = { defaultArgs with includeChildProcesses = Some true }
    let result = buildSnapshot args defaultConfig None
    let children = node result "children" :?> JsonArray
    Assert.Equal(0, children.Count)

// ─── Group C: filter args — each include* flag toggles its block ─────────────

[<Fact>]
let ``includeFcsCacheStats false omits fcs block`` () =
    let args = { defaultArgs with includeFcsCacheStats = Some false }
    let result = buildSnapshot args defaultConfig None
    Assert.Null(node result "fcs")

// Snapshot test: replaces `Assert.NotNull(node result "fcs")` with a structural
// assertion of the entire fcs slice (incrementalBuilders, projectCacheSize,
// checker flags). Catches regressions in any fcs-block field.
[<Fact>]
let ``includeFcsCacheStats true matches fcs slice snapshot`` () : Task =
    let args = { defaultArgs with includeFcsCacheStats = Some true }
    let result = buildSnapshot args defaultConfig None
    let fcs = node result "fcs"
    VerifyModuleInitializer.init ()
    Verifier.Verify(prettyJson fcs, extension = "json").ToTask() :> Task

[<Fact>]
let ``includeAssemblyCounts false omits assemblies from process block`` () =
    let args = { defaultArgs with includeAssemblyCounts = Some false }
    let result = buildSnapshot args defaultConfig None
    let proc = node result "process"
    Assert.Null(node proc "assemblies")

// Snapshot test: replaces `Assert.NotNull(node proc "assemblies")` with a
// structural assertion of the assemblies slice.
[<Fact>]
let ``includeAssemblyCounts true matches assemblies slice snapshot`` () : Task =
    let args = { defaultArgs with includeAssemblyCounts = Some true }
    let result = buildSnapshot args defaultConfig None
    let proc = node result "process"
    let assemblies = node proc "assemblies"
    VerifyModuleInitializer.init ()
    Verifier.Verify(prettyJson assemblies, extension = "json").ToTask() :> Task

[<Fact>]
let ``includeChildProcesses false omits children block`` () =
    let args = { defaultArgs with includeChildProcesses = Some false }
    let result = buildSnapshot args defaultConfig None
    Assert.Null(node result "children")

// Snapshot test: replaces `Assert.NotNull(node result "children")` with a
// structural assertion of the children array. With no FSAC process supplied,
// the array is empty — but the snapshot is still meaningful: it asserts the
// array shape (not a string, not null).
[<Fact>]
let ``includeChildProcesses true matches children slice snapshot`` () : Task =
    let args = { defaultArgs with includeChildProcesses = Some true }
    let result = buildSnapshot args defaultConfig None
    let children = node result "children"
    VerifyModuleInitializer.init ()
    Verifier.Verify(prettyJson children, extension = "json").ToTask() :> Task

[<Fact>]
let ``includeProcessIds false omits pid from process block`` () =
    let args = { defaultArgs with includeProcessIds = Some false }
    let result = buildSnapshot args defaultConfig None
    let proc = node result "process"
    Assert.Null(node proc "pid")

// Snapshot test: replaces `Assert.NotNull(node proc "pid")` with a structural
// snapshot of the process block including the pid (scrubbed to <pid>).
[<Fact>]
let ``includeProcessIds true matches process slice snapshot`` () : Task =
    let args = { defaultArgs with includeProcessIds = Some true }
    let result = buildSnapshot args defaultConfig None
    let proc = node result "process"
    VerifyModuleInitializer.init ()
    Verifier.Verify(prettyJson proc, extension = "json").ToTask() :> Task

// ─── Group D: structural sanity ───────────────────────────────────────────────

[<Fact>]
let ``buildSnapshot returns valid JSON that can be re-serialized`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let json = JsonSerializer.Serialize(result)
    Assert.False(String.IsNullOrEmpty(json))

[<Fact>]
let ``buildSnapshot uptimeSeconds is non-negative`` () =
    let result = buildSnapshot defaultArgs defaultConfig None
    let proc = node result "process"
    let uptime = getIntAt proc "uptimeSeconds"
    Assert.True(uptime >= 0, $"uptimeSeconds should be >= 0, got {uptime}")

[<Fact>]
let ``buildSnapshot projectCacheSize reflects config value`` () =
    let config = { defaultConfig with ProjectCacheSize = 5 }
    let result = buildSnapshot defaultArgs config None
    let fcs = node result "fcs"
    let size = getIntAt fcs "projectCacheSize"
    Assert.Equal(5, size)

// Snapshot test for the all-flags-false minimal case. Replaces two
// `Assert.NotNull(node proc …)` calls (managedHeap / gcInfo) with a structural
// snapshot of the entire result. Companion `Assert.Null` checks below remain
// — they assert *absence* of optional blocks, which the snapshot also covers,
// but the explicit asserts document the contract for readers.
[<Fact>]
let ``buildSnapshot all flags false matches minimal snapshot`` () : Task =
    let args =
        { includeFcsCacheStats = Some false
          includeAssemblyCounts = Some false
          includeChildProcesses = Some false
          includeProcessIds = Some false }

    let result = buildSnapshot args defaultConfig None
    Assert.Equal("ok", getStr result "status")
    // optional blocks should be absent
    Assert.Null(node result "fcs")
    Assert.Null(node result "children")
    let proc = node result "process"
    Assert.Null(node proc "assemblies")
    Assert.Null(node proc "pid")
    // managedHeap and gcInfo are always present — snapshot pins their shape
    VerifyModuleInitializer.init ()
    Verifier.Verify(prettyJson result, extension = "json").ToTask() :> Task
