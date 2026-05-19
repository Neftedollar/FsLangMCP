module FsLangMcp.Tests.VerifyModuleInitializer

// Verify (snapshot testing) global setup. Configures scrubbers that strip
// volatile fields out of `runtime_status` snapshots so they remain deterministic
// across test runs and across machines.
//
// F# doesn't honor [<ModuleInitializer>] (FS0202). Instead we expose an
// idempotent `init ()` function and require each snapshot test to call it.
// xUnit serializes test execution within a class by default; cross-test calls
// are safe because `VerifierSettings.AddScrubber` is idempotent at the
// behavioural level — the scrubbers we register only do regex replacements,
// so registering the same one twice is a perf cost, not a correctness issue.
// We guard with a `bool ref` to make it actually one-shot.
//
// Each scrubber documents what it protects against. If a snapshot test starts
// flaking on a fresh field (a *.received.* appears on the second run with a
// non-scrubbed numeric or path), add a new scrubber here rather than relaxing
// the assertion in the test.

open System.Text.RegularExpressions
open VerifyTests

let private addRegexScrubber (pattern: string) (replacement: string) : unit =
    let rx = Regex(pattern)
    VerifierSettings.AddScrubber(fun sb ->
        let replaced = rx.Replace(sb.ToString(), replacement)
        sb.Clear().Append(replaced) |> ignore)

let private initialized = ref false
let private gate = obj ()

let init () : unit =
    lock gate (fun () ->
        if not initialized.Value then
            initialized.Value <- true

            // ── Numeric volatile fields ──────────────────────────────────────
            // Heap byte counters and GC counters change on every call (allocations
            // happen between snapshots, GC may have run). Replace numeric values
            // for these named keys with <number>. Matches both ints and decimals.
            let numericKeys =
                [ // managedHeap byte counts (int64)
                  "totalBytes"
                  "gen0Bytes"
                  "gen1Bytes"
                  "gen2Bytes"
                  "lohBytes"
                  "pohBytes"
                  // managedHeap fragmentation ratio (double)
                  "fragmentation"
                  // gcInfo collection counters and allocations
                  "gen0"
                  "gen1"
                  "gen2"
                  "totalAllocated"
                  // process uptime
                  "uptimeSeconds"
                  // assemblies loaded count (varies by JIT progress)
                  "loaded"
                  // child process working set
                  "rssBytes" ]

            for key in numericKeys do
                addRegexScrubber
                    $"\"{Regex.Escape key}\": -?\\d+(\\.\\d+)?"
                    $"\"{key}\": <number>"

            // ── Process IDs ──────────────────────────────────────────────────
            // pid is `Environment.ProcessId` for the test runner — different per run.
            // Child process pids vary the same way.
            addRegexScrubber "\"pid\": \\d+" "\"pid\": <pid>"

            // ── lastTrigger string: "gen<N>" ────────────────────────────────
            // Reports which generation triggered the last GC; varies per process state.
            addRegexScrubber "\"lastTrigger\": \"gen\\d+\"" "\"lastTrigger\": \"<gen>\""

            // ── lastLoaded assembly name ────────────────────────────────────
            // The "last loaded" assembly depends on JIT order and reflection probes
            // performed by the test runner; deterministically nondeterministic.
            addRegexScrubber "\"lastLoaded\": \"[^\"]*\"" "\"lastLoaded\": \"<assembly>\""

            // ── Child process name ──────────────────────────────────────────
            // When a child Process is supplied, its ProcessName depends on whatever
            // dummy process the test happens to use.
            addRegexScrubber "\"name\": \"[^\"]*\"" "\"name\": \"<process>\""

            // ── isServerGc bool ─────────────────────────────────────────────
            // Server GC vs Workstation GC depends on runtimeconfig at the test
            // runner level. Scrub to keep snapshots portable across CI / dev.
            addRegexScrubber "\"isServerGc\": (true|false)" "\"isServerGc\": <bool>"

            // ── fslangmcpVersion ────────────────────────────────────────────
            // Read from AssemblyInformationalVersion — changes every release.
            // Snapshots stay portable across bumps; non-emptiness is asserted by
            // a separate non-snapshot test in RuntimeStatusTests.
            addRegexScrubber "\"fslangmcpVersion\": \"[^\"]*\"" "\"fslangmcpVersion\": \"<version>\"")
