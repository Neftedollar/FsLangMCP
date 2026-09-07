# FsLangMcp architecture

A codebase tour for contributors. Aimed at someone who has cloned the repo, run `dotnet build`, and now wants to know **what each file does** and **why the split exists**.

## High-level overview

FsLangMcp has one long-lived stdio MCP host. It bridges an MCP client (Claude Code, Cursor, Codex, Copilot CLI, any tool that speaks the MCP protocol over stdio) to two F# back-ends:

- **`fsautocomplete`** — the same LSP server that powers Ionide. Runs as a child process; we speak LSP JSON-RPC over its stdio.
- **`FSharp.Compiler.Service`** — pulled in as a library and used in-process. Same compiler that `fsautocomplete` itself wraps, but we instantiate our own `FSharpChecker` for tools that need finer control or project-wide queries.

MCP transport is provided by `FsMcp.Server` (a transitive package dependency).

```
  MCP client (Claude/Cursor/Codex)
        │
   stdio (JSON-RPC, MCP framing)
        │
        ▼
  fslangmcp (this server)
        │
        ├── FsAutoCompleteBridge (LspBridge.fs)
        │       │
        │       └── stdio → fsautocomplete child process
        │             (LSP JSON-RPC; one workspace per server)
        │
        └── FcsBridge (FcsBridge.fs)
                │
                ├── cache miss → short-lived project-evaluation helper
                │                 (ProjInfo/MSBuild; bounded JSON back to host)
                │
                └── in-process → FSharp.Compiler.Service
                      (own FSharpChecker, own project-options cache)
```

## File-by-file tour

Sources live at the repo root (single-project layout). Listed in `<Compile Include>` order from `FsLangMcp.fsproj`:

| File | Purpose |
|------|---------|
| `BoundedCache.fs` | Generic thread-safe FIFO-eviction cache used by the semantic caches. |
| `ProcessRunner.fs` | Bounded external-process execution, output capture, cancellation, and process-tree cleanup. |
| `Types.fs` | MCP argument records, error types, render-budget settings, and JSON helpers. |
| `SdkPreflight.fs` | Detects unsatisfied `global.json` SDK pins before ProjInfo/FSAC fail opaquely. |
| `Version.fs` | Exposes the running product version from assembly metadata. |
| `InstallationHealth.fs` | Detects a stale long-lived global-tool process whose version directory/dependencies were replaced on disk. |
| `ProjectEvaluation.fs` | Private versioned evaluated-options protocol and short-lived ProjInfo/MSBuild helper; shared callers retain process ownership through cancellation cleanup. |
| `ProjectFiles.fs` | Shared project/solution discovery, filtering, and evaluated-project data model. |
| `Cursor.fs` | Opaque pagination cursor encoding/decoding. |
| `ProjectInspection.fs` | Implements read-only evaluated `.fsproj` inspection. |
| `ProjectHealth.fs` | Implements project readiness and reverse test-project discovery. |
| `AnalyzerDiagnostics.fs` | Analyzer SARIF ingestion and analyzer-setup previews. |
| `LspBridge.fs` | fsautocomplete child-process lifecycle, LSP JSON-RPC client, diagnostics generations, and readiness state. |
| `MetadataAccessibility.fs` | Reads ECMA-335 method accessibility without loading/executing referenced assemblies. |
| `FcsBridge.fs` | `FSharpChecker`, project-options/results caches, semantic queries, and FCS-backed tool implementations. |
| `Tools.fs` | Shared tool error envelopes, including stale-installation handling. |
| `RuntimeStatus.fs` | Pure builder for heap/GC/thread/process/FCS telemetry and high-thread warnings. |
| `Dispatcher.fs` | Maps consolidated `find`/`check` requests to their FCS/FSAC execution paths. |
| `McpHost.fs` | MCP host configuration and typed-tool schema adaptation. |
| `Program.fs` | CLI/MCP composition root, tool registration, concurrency gates, and active-context wiring. |

## Key design choices

### Two-bridge architecture

FsLangMcp runs both `fsautocomplete` (child process) AND `FSharp.Compiler.Service` (in-process library) simultaneously. They look like duplication, but they answer different question shapes:

- **fsautocomplete** is mature, battle-tested, and shaped for **editor positions** — "complete at line N, column M", "rename the symbol at this cursor", "format this file", "give me raw publishDiagnostics". Reusing it instead of re-implementing those features keeps the surface honest and avoids drifting away from what Ionide users already trust.
- **FCS in-process** is shaped for **agent flows** — "find this symbol's definitions AND references AND source-line context in one call", "audit every construction site of this record field", "validate this snippet against the loaded project's NuGet references", "enumerate types from this referenced assembly". Direct FCS access lets us paginate, group, and structure responses the way agents need without round-tripping through LSP's editor-shaped contracts.

The trade-off is process complexity (we own the FSAC child, bounded evaluation helpers, and a managed `FSharpChecker`) and cache coherence (changes that invalidate one don't automatically invalidate the other — `check` uses a fresh in-process pass by default for that reason). The win is that we get the right surface for both audiences without re-implementing FSAC.

### Project-evaluation isolation

Only project-options cache misses run ProjInfo/MSBuild, in a short-lived instance
of the same FsLangMCP binary. The helper never starts MCP or FSAC. It returns
evaluated ProjInfo data through a private, versioned, size-bounded JSON envelope;
the parent maps that data to FCS options and reconstructs CLR-reference reader
delegates locally. Compile items, import stamps, package/project references and
evaluated properties keep their existing meaning.

The existing per-bridge admission limit remains one evaluation: same-key callers
share their exact flight, while unrelated requests get a bounded busy response.
Cancellation follows aggregate caller liveness, and admission is retained until
the helper process tree has been drained. This is a resource-lifetime boundary,
not a security sandbox for untrusted MSBuild code.

`just project-evaluation-soak` records cold/warm/reload latency and checks real
fingerprint invalidations, FCS lookup/check behavior and parent thread reclamation.
The live three-OS workflow runs the same probe against the installed package.

### GC tuning

`runtimeconfig.template.json` ships with:

```json
{ "configProperties": { "System.GC.Server": false, "System.GC.Concurrent": true } }
```

Workstation GC + Concurrent GC, added in v0.5.1. The motivation: a stdio MCP server is bursty — load project, idle, next request, idle. Server GC commits memory aggressively and only returns it under OS-level pressure, which on dev laptops shows up as alarming RSS growth (gigabytes apparent) without ever triggering a release. Workstation GC returns committed memory promptly while idle.

Operators with throughput-sensitive workloads can opt back into Server GC via `DOTNET_gcServer=1`. The env var wins over `runtimeconfig`, so the override is non-destructive.

### Tool-description schema

Documented in [`tool-description-schema.md`](tool-description-schema.md). The motivation is concrete: routing-prompt tokens are scarce. Across all 35 registered tools, descriptions are part of the system prompt sent to the LLM on every agent call. Descriptions target a uniform 250–400 character window using the current 4-slot schema (What + Prefer/Avoid + Key params + Caveat/Cross-ref); implementation-layer prefixes are intentionally omitted.

The schema's primary discipline is "**should I call this tool right now?**" — answered in ≤400 chars, with explicit "prefer X over Y" callouts where overlap exists. Deep implementation detail moved to [`tools-detailed.md`](tools-detailed.md).

### Error contract

Three tiers, by recoverability:

1. **`{ "status": "invalid_args", "message": "…" }`** — structured envelope from `ArgsValidation.requireNonBlank` (`Types.fs`), used by current handlers with required string arguments. Agent-recoverable: the caller can fix the args and retry.
2. **`{ "status": "not_ready", "message": "…" }`** — workspace still loading; LSP-proxy tools return this if called before `set_project` has finished warming. Time-recoverable: retry after a short wait.
3. **MCP transport error** with `{ "errorKind": "…", "message": "…" }` — genuinely unrecoverable. The handler raised, or the FCS work was aborted (`FcsAborted`).

The contract is documented in README's "Response Shape" section.

## Where to add things

| Adding... | Goes in... |
|-----------|------------|
| A new MCP tool | New args record in `Types.fs`, new method on `FcsBridge` or `FsAutoCompleteBridge`, new `TypedTool.define` in `Program.fs`. Walkthrough in [`CONTRIBUTING.md`](../CONTRIBUTING.md). |
| A new response field on an existing tool | Add to README's "Notable response fields", update the response record's `///` docs, bump the CHANGELOG entry. |
| A new FCS-driven diagnostic check | New `member` on `FcsBridge`, registered in `Program.fs` under `fcsGate`. Add to `docs/tools-detailed.md` if the description needs implementation depth. |
| A new project-inspection capability | `ProjectInspection.fs` (structural — compile order, references) or `ProjectHealth.fs` (health-shaped — readiness, test detection, build artifacts) depending on shape. |
| A new caching layer | Reuse `BoundedCache` from `BoundedCache.fs`. Pick a small `maxSize` — the existing caches hold 50 (project options/symbol uses) and 3 (project results) and that's worked well at scale. |
| A new LSP-proxy passthrough | New method on `FsAutoCompleteBridge` in `LspBridge.fs`, register in `Program.fs` under `lspGate`. Prefer FCS-shaped alternatives where the response would benefit from agent-friendly structuring. |

See also: [`CONTRIBUTING.md`](../CONTRIBUTING.md) for the full walkthrough of adding a tool, [`AGENT_INTEGRATION.md`](../AGENT_INTEGRATION.md) for the integration patterns multi-agent users follow, and [`troubleshooting.md`](troubleshooting.md) for symptom-keyed failure-mode docs.
