module FsLangMcp.Tests.RuntimeStatusTests

open System
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open FsLangMcp.Types
open FsLangMcp.RuntimeStatus

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

// ─── Group A: output shape with default args ──────────────────────────────────

[<Fact>]
let ``buildSnapshot with default args returns status ok`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    Assert.Equal("ok", getStr result "status")

[<Fact>]
let ``buildSnapshot with default args includes process block`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    Assert.NotNull(node result "process")

[<Fact>]
let ``buildSnapshot process block includes managedHeap`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    Assert.NotNull(node proc "managedHeap")

[<Fact>]
let ``buildSnapshot managedHeap has expected numeric fields`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    let heap = node proc "managedHeap"
    let fields = [ "totalBytes"; "gen0Bytes"; "gen1Bytes"; "gen2Bytes"; "lohBytes"; "pohBytes" ]

    for field in fields do
        let value = getIntAt heap field
        Assert.True(value >= 0, $"Field {field} should be non-negative, got {value}")

[<Fact>]
let ``buildSnapshot managedHeap has fragmentation between 0 and 1`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    let heap = node proc "managedHeap"
    let fragNode = node heap "fragmentation"
    let frag: double = fragNode.GetValue()
    Assert.True(frag >= 0.0 && frag <= 1.0, $"fragmentation should be in [0,1], got {frag}")

[<Fact>]
let ``buildSnapshot process block includes gcInfo`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    Assert.NotNull(node proc "gcInfo")

[<Fact>]
let ``buildSnapshot gcInfo has isServerGc field`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    let gcInfo = node proc "gcInfo"
    let isServer = node gcInfo "isServerGc"
    // Just assert the field exists and is a bool
    let _ = isServer.GetValue<bool>()
    ()

[<Fact>]
let ``buildSnapshot gcInfo has totalCollections with gen0 gen1 gen2`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
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
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    let gcInfo = node proc "gcInfo"
    let allocatedNode = node gcInfo "totalAllocated"
    let allocated: int64 = allocatedNode.GetValue()
    Assert.True(allocated >= 0L)

[<Fact>]
let ``buildSnapshot process block includes assemblies by default`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    Assert.NotNull(node proc "assemblies")

[<Fact>]
let ``buildSnapshot assemblies loaded count is positive`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    let assemblies = node proc "assemblies"
    let loaded = getIntAt assemblies "loaded"
    Assert.True(loaded > 0, $"Loaded assembly count should be > 0, got {loaded}")

[<Fact>]
let ``buildSnapshot process block includes pid by default`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    Assert.NotNull(node proc "pid")

[<Fact>]
let ``buildSnapshot pid matches current process`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    let pid = getIntAt proc "pid"
    Assert.Equal(Environment.ProcessId, pid)

[<Fact>]
let ``buildSnapshot includes fcs block by default`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    Assert.NotNull(node result "fcs")

[<Fact>]
let ``buildSnapshot fcs has projectCacheSize matching config`` () =
    let result = buildSnapshot defaultArgs defaultConfig 7 None
    let fcs = node result "fcs"
    let size = getIntAt fcs "projectCacheSize"
    // ProjectCacheSize comes from defaultConfig (3), not the checkerCacheCount (7)
    Assert.Equal(3, size)

[<Fact>]
let ``buildSnapshot fcs checker block reflects config flags`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
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

    let result = buildSnapshot defaultArgs config 0 None
    let fcs = node result "fcs"
    let checker = node fcs "checker"
    Assert.False(getBoolAt checker "keepAssemblyContents")
    Assert.False(getBoolAt checker "keepAllBackgroundResolutions")
    Assert.False(getBoolAt checker "keepAllBackgroundSymbolUses")

[<Fact>]
let ``buildSnapshot fcs incrementalBuilders count is 0 because FCS does not expose it`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let fcs = node result "fcs"
    let builders = node fcs "incrementalBuilders"
    Assert.Equal(0, getIntAt builders "count")

[<Fact>]
let ``buildSnapshot fcs incrementalBuilders approximateBytes is null`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let fcs = node result "fcs"
    let builders = node fcs "incrementalBuilders"
    let approx = node builders "approximateBytes"
    Assert.Null(approx)

[<Fact>]
let ``buildSnapshot includes children array by default`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    Assert.NotNull(node result "children")

// ─── Group B: no-FSAC case → children is empty array ────────────────────────

[<Fact>]
let ``buildSnapshot with no FSAC process returns empty children array`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let children = node result "children" :?> JsonArray
    Assert.Equal(0, children.Count)

[<Fact>]
let ``buildSnapshot with None FSAC process returns empty children array`` () =
    let args = { defaultArgs with includeChildProcesses = Some true }
    let result = buildSnapshot args defaultConfig 0 None
    let children = node result "children" :?> JsonArray
    Assert.Equal(0, children.Count)

// ─── Group C: filter args — each include* flag toggles its block ─────────────

[<Fact>]
let ``includeFcsCacheStats false omits fcs block`` () =
    let args = { defaultArgs with includeFcsCacheStats = Some false }
    let result = buildSnapshot args defaultConfig 0 None
    Assert.Null(node result "fcs")

[<Fact>]
let ``includeFcsCacheStats true includes fcs block`` () =
    let args = { defaultArgs with includeFcsCacheStats = Some true }
    let result = buildSnapshot args defaultConfig 0 None
    Assert.NotNull(node result "fcs")

[<Fact>]
let ``includeAssemblyCounts false omits assemblies from process block`` () =
    let args = { defaultArgs with includeAssemblyCounts = Some false }
    let result = buildSnapshot args defaultConfig 0 None
    let proc = node result "process"
    Assert.Null(node proc "assemblies")

[<Fact>]
let ``includeAssemblyCounts true includes assemblies in process block`` () =
    let args = { defaultArgs with includeAssemblyCounts = Some true }
    let result = buildSnapshot args defaultConfig 0 None
    let proc = node result "process"
    Assert.NotNull(node proc "assemblies")

[<Fact>]
let ``includeChildProcesses false omits children block`` () =
    let args = { defaultArgs with includeChildProcesses = Some false }
    let result = buildSnapshot args defaultConfig 0 None
    Assert.Null(node result "children")

[<Fact>]
let ``includeChildProcesses true includes children block`` () =
    let args = { defaultArgs with includeChildProcesses = Some true }
    let result = buildSnapshot args defaultConfig 0 None
    Assert.NotNull(node result "children")

[<Fact>]
let ``includeProcessIds false omits pid from process block`` () =
    let args = { defaultArgs with includeProcessIds = Some false }
    let result = buildSnapshot args defaultConfig 0 None
    let proc = node result "process"
    Assert.Null(node proc "pid")

[<Fact>]
let ``includeProcessIds true includes pid in process block`` () =
    let args = { defaultArgs with includeProcessIds = Some true }
    let result = buildSnapshot args defaultConfig 0 None
    let proc = node result "process"
    Assert.NotNull(node proc "pid")

// ─── Group D: structural sanity ───────────────────────────────────────────────

[<Fact>]
let ``buildSnapshot returns valid JSON that can be re-serialized`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let json = JsonSerializer.Serialize(result)
    Assert.False(String.IsNullOrEmpty(json))

[<Fact>]
let ``buildSnapshot uptimeSeconds is non-negative`` () =
    let result = buildSnapshot defaultArgs defaultConfig 0 None
    let proc = node result "process"
    let uptime = getIntAt proc "uptimeSeconds"
    Assert.True(uptime >= 0, $"uptimeSeconds should be >= 0, got {uptime}")

[<Fact>]
let ``buildSnapshot projectCacheSize reflects config value`` () =
    let config = { defaultConfig with ProjectCacheSize = 5 }
    let result = buildSnapshot defaultArgs config 0 None
    let fcs = node result "fcs"
    let size = getIntAt fcs "projectCacheSize"
    Assert.Equal(5, size)

[<Fact>]
let ``buildSnapshot all flags false still returns status ok with minimal process block`` () =
    let args =
        { includeFcsCacheStats = Some false
          includeAssemblyCounts = Some false
          includeChildProcesses = Some false
          includeProcessIds = Some false }

    let result = buildSnapshot args defaultConfig 0 None
    Assert.Equal("ok", getStr result "status")
    // managedHeap and gcInfo are always present
    let proc = node result "process"
    Assert.NotNull(node proc "managedHeap")
    Assert.NotNull(node proc "gcInfo")
    // optional blocks should be absent
    Assert.Null(node result "fcs")
    Assert.Null(node result "children")
    Assert.Null(node proc "assemblies")
    Assert.Null(node proc "pid")
