module FsLangMcp.RuntimeStatus

// [FCS in-process] Read-only diagnostic snapshot of managed-heap state and FCS
// cache footprint for the FsLangMCP server process. No GC collections are
// triggered, no heap walks, no EventPipe sessions.

open System
open System.Diagnostics
open System.Runtime
open FsLangMcp.Types
open System.Text.Json.Nodes

// ─── Child process info ────────────────────────────────────────────────────────

type ChildProcessInfo =
    { Name: string
      Pid: int
      RssBytes: int64 }

// ─── Pure heap-JSON helper (also used by tests for the int64 round-trip) ──────

/// Input record for heapJson. All byte fields are int64 to avoid truncation on
/// >2.147 GB heaps. Exposed internally so tests can exercise it directly.
type internal ManagedHeapInfo =
    { TotalBytes: int64
      Gen0Bytes: int64
      Gen1Bytes: int64
      Gen2Bytes: int64
      LohBytes: int64
      PohBytes: int64
      Fragmentation: double }

/// Converts a ManagedHeapInfo record to a JsonNode without any int truncation.
let internal heapJson (h: ManagedHeapInfo) : JsonNode =
    jobj
        [ "totalBytes", jint64 h.TotalBytes
          "gen0Bytes", jint64 h.Gen0Bytes
          "gen1Bytes", jint64 h.Gen1Bytes
          "gen2Bytes", jint64 h.Gen2Bytes
          "lohBytes", jint64 h.LohBytes
          "pohBytes", jint64 h.PohBytes
          "fragmentation", JsonValue.Create(h.Fragmentation) :> JsonNode ]
    :> JsonNode

// ─── Snapshot functions ────────────────────────────────────────────────────────

/// Reads heap sizing from GCMemoryInfo and GC APIs without triggering a collection.
let private managedHeapJson () : JsonNode =
    // GC.GetGCMemoryInfo() reads a cached snapshot — no collection.
    let info = GC.GetGCMemoryInfo()

    let gen0Bytes =
        if info.GenerationInfo.Length > 0 then
            info.GenerationInfo[0].SizeAfterBytes
        else
            0L

    let gen1Bytes =
        if info.GenerationInfo.Length > 1 then
            info.GenerationInfo[1].SizeAfterBytes
        else
            0L

    let gen2Bytes =
        if info.GenerationInfo.Length > 2 then
            info.GenerationInfo[2].SizeAfterBytes
        else
            0L

    let lohBytes =
        if info.GenerationInfo.Length > 3 then
            info.GenerationInfo[3].SizeAfterBytes
        else
            0L

    let pohBytes =
        if info.GenerationInfo.Length > 4 then
            info.GenerationInfo[4].SizeAfterBytes
        else
            0L

    // GC.GetTotalMemory(false) = false → does NOT trigger collection
    let totalBytes = GC.GetTotalMemory(false)

    let fragmentation =
        if info.HeapSizeBytes > 0L then
            Math.Round(float info.FragmentedBytes / float info.HeapSizeBytes, 4)
        else
            0.0

    heapJson
        { TotalBytes = totalBytes
          Gen0Bytes = gen0Bytes
          Gen1Bytes = gen1Bytes
          Gen2Bytes = gen2Bytes
          LohBytes = lohBytes
          PohBytes = pohBytes
          Fragmentation = fragmentation }

let private gcInfoJson () : JsonNode =
    let isServer = GCSettings.IsServerGC

    // GC.GetGCMemoryInfo reads a snapshot; no collection.
    let info = GC.GetGCMemoryInfo()

    let totalAllocated = GC.GetTotalAllocatedBytes(precise = false)

    // GCMemoryInfo does not expose the trigger cause; report the generation that triggered.
    let lastTrigger =
        try
            $"gen{info.Generation}"
        with _ ->
            "unavailable"

    jobj
        [ "isServerGc", jbool isServer
          "totalCollections",
          jobj
              [ "gen0", jint (GC.CollectionCount(0))
                "gen1", jint (GC.CollectionCount(1))
                "gen2", jint (GC.CollectionCount(2)) ]
          :> JsonNode
          "totalAllocated", JsonValue.Create(totalAllocated) :> JsonNode
          "lastTrigger", jstr lastTrigger ]
    :> JsonNode

let private assemblyCountJson () : JsonNode =
    let assemblies = AppDomain.CurrentDomain.GetAssemblies()
    let count = assemblies.Length

    let lastName =
        if count > 0 then
            assemblies[count - 1].GetName().Name |> Option.ofObj |> Option.defaultValue ""
        else
            ""

    jobj [ "loaded", jint count; "lastLoaded", jstr lastName ] :> JsonNode

let private fcsStatusJson (config: FcsCheckerConfig) : JsonNode =
    // IncrementalBuilder count is not exposed by the public FCS API on net10.0.
    // Count is null because FSharpChecker does not expose live builder count on net10.0.
    // approximateBytes is null for the same reason.
    jobj
        [ "incrementalBuilders",
          jobj
              [ "count", null
                "approximateBytes", null ]
          :> JsonNode
          "projectCacheSize", jint config.ProjectCacheSize
          "checker",
          jobj
              [ "keepAssemblyContents", jbool config.KeepAssemblyContents
                "keepAllBackgroundResolutions", jbool config.KeepAllBackgroundResolutions
                "keepAllBackgroundSymbolUses", jbool config.KeepAllBackgroundSymbolUses ]
          :> JsonNode ]
    :> JsonNode

let private childProcessJson (proc: Process) : JsonNode =
    let name =
        try
            proc.ProcessName
        with _ ->
            "unknown"

    let pid =
        try
            proc.Id
        with _ ->
            0

    let rss =
        try
            proc.WorkingSet64
        with _ ->
            0L

    jobj [ "name", jstr name; "pid", jint pid; "rssBytes", JsonValue.Create(rss) :> JsonNode ] :> JsonNode

// ─── Public entry point ────────────────────────────────────────────────────────

let buildSnapshot
    (args: RuntimeStatusArgs)
    (config: FcsCheckerConfig)
    (fsacProcess: Process option)
    : JsonNode =

    let includeFcs = args.includeFcsCacheStats |> Option.defaultValue true
    let includeAssemblies = args.includeAssemblyCounts |> Option.defaultValue true
    let includeChildren = args.includeChildProcesses |> Option.defaultValue true
    let includePids = args.includeProcessIds |> Option.defaultValue true

    use proc = Process.GetCurrentProcess()

    let uptimeSeconds =
        (DateTimeOffset.UtcNow - DateTimeOffset(proc.StartTime.ToUniversalTime())).TotalSeconds
        |> int

    let processFields =
        [ yield "uptimeSeconds", jint uptimeSeconds
          yield "managedHeap", managedHeapJson ()
          yield "gcInfo", gcInfoJson ()

          if includeAssemblies then
              yield "assemblies", assemblyCountJson ()

          if includePids then
              yield "pid", jint Environment.ProcessId ]

    let processPart: JsonNode = jobj processFields

    let children: JsonNode =
        if includeChildren then
            match fsacProcess with
            | Some p when not p.HasExited ->
                let childJson = childProcessJson p
                JsonArray(childJson)
            | _ -> JsonArray()
        else
            JsonArray()

    let topFields =
        [ yield "status", jstr "ok"
          yield "process", processPart

          if includeFcs then
              yield "fcs", fcsStatusJson config

          if includeChildren then
              yield "children", children ]

    jobj topFields
