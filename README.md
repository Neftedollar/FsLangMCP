# FsLangMCP — semantic F# for your AI coding agent

**Stop your agent grepping F#.** Give it the real compiler: cross-project `find`, a trustworthy `check` verdict, types, rename, dead-code — over MCP.

[![CI](https://github.com/Neftedollar/FsLangMCP/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/FsLangMCP/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FsLangMcp.svg)](https://www.nuget.org/packages/FsLangMcp/)
[![Downloads](https://img.shields.io/nuget/dt/FsLangMcp.svg)](https://www.nuget.org/packages/FsLangMcp/)
[![Target](https://img.shields.io/badge/target-net10.0-blue.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

## Why

Text search misses the semantics F# depends on — partial application, aliased `open`s, shadowed bindings, cross-project uses, record-field set-sites, CE custom-operations — and over-matches comments and strings. FsLangMCP resolves symbols via the real compiler (FCS in-process + FSAC LSP), so your agent gets trustworthy answers instead of grep noise.

In a live-execution A/B, shaping the tool surface to agent intent **cut agents' rg/grep-fallback from 56% → 28%** and steps-to-completion from 10.4 → 5.8 (success rate 89% → 100%). Full story: [`docs/why-agents-grep-fsharp.md`](docs/why-agents-grep-fsharp.md).

## Quickstart

**Prerequisites:** .NET SDK 10+ on PATH.

```bash
dotnet tool install -g FsLangMcp
fslangmcp --bootstrap-tools   # one-time: fetches fsautocomplete + ionide.projinfo.tool
```

Add to your MCP client config:

```json
{
  "mcpServers": {
    "fslangmcp": { "command": "fslangmcp" }
  }
}
```

Then call `set_project` with your `.fsproj` or `.sln` path, and start with `find` or `check`.

Full setup: [`docs/getting-started.md`](docs/getting-started.md) · Per-client configs: [`docs/configuration.md`](docs/configuration.md) · Runnable examples: [`examples/`](examples/).

## Changelog

[CHANGELOG.md](CHANGELOG.md)

## What You Get

35 tools in 8 groups. Full reference: [`docs/tools-reference.md`](docs/tools-reference.md).

### Headline tools

| Tool | What it does |
|------|--------------|
| `find` | Multi-project semantic search: definitions, references, record-field set sites, member call-sites, all solution `.fsproj`s. Beats textual `rg` on partial application, aliased opens, and cross-project sites. |
| `check` | One trustworthy verdict (`clean`/`errors`/`unknown`) from a fresh in-process type-check. Never reports a stale-cache false-clean; bare `check()` suffices. |

### Navigate / understand

| Tool | What it does |
|------|--------------|
| `set_project` | Initialize or switch FSAC/LSP context. Accepts `.fsproj`, `.sln`, `.slnx`, or directory. Required before LSP-proxy tools. |
| `project_health` | Fast read-only preflight: options readiness, source files, analyzer setup, test project discovery. No build, no tests. |
| `fcs_project_outline` | Compact project-wide outline over all compile files. |
| `fcs_file_outline` | Per-file outline; `summaryOnly=true` (default) keeps token cost low. |
| `fsharp_project_inspect` | Read-only `.fsproj` inspection: compile order, references, signature/implementation pairing. |
| `fcs_symbol_at_word` | Tolerant symbol lookup by line + word — no exact cursor column needed. |
| `fcs_get_project_options` | Get compiler `OtherOptions` for a `.fsproj` via proj-info. Diagnostic helper. |

### Diagnose / fix

| Tool | What it does |
|------|--------------|
| `fcs_explain_diagnostic` | Plain-language explanation + repair hints for a compiler diagnostic code (e.g. FS0039). |
| `fcs_diagnostic_fixes` | Fetch a file's diagnostics and request code-action fixes, grouped per diagnostic. |
| `fcs_check_compile_order` | Detect when FS0039 is a compile-order problem, not a missing `open`. |
| `fcs_suggest_open` | Given an unresolved symbol name, return ranked `open` directive candidates. |

### Refactor preview

| Tool | What it does |
|------|--------------|
| `fcs_rename_preview` | Preview a semantic rename's full impact (edits grouped by file, with before/after text) without writing. |
| `fcs_refactor_impact` | Blast-radius preview: uses, tests, compile order, public API — all orchestrated in one call. |
| `fcs_make_internal_visible` | Drop `private` from a declaration at a position; returns a workspace edit, writes nothing. |
| `fcs_tests_for_symbol` | List the tests that likely cover a symbol (test-file uses + enclosing test name). |

### Review / cleanup

| Tool | What it does |
|------|--------------|
| `fcs_dead_code` | List likely-unused `private`/`internal` bindings as cleanup candidates. |
| `fcs_review_scan` | Scan source for AST-level review candidates: match wildcards, `mutable`, blocking calls, casts, reflection. |
| `fcs_public_api` | Emit the project's full public API surface in stable order — useful for API-diff snapshots. |
| `fcs_signature_status` | Report `.fsi`-vs-impl drift: members hidden from signature or stale in signature. |
| `fcs_create_file_plan` | Plan where a new `.fs` file belongs in compile order; writes nothing. |

### Analyzers

| Tool | What it does |
|------|--------------|
| `fcs_analyzer_diagnostics` | Report F# analyzer diagnostics (SARIF-backed, grouped by analyzer and severity). |
| `fcs_analyzer_setup_preview` | Plan what to add to enable analyzers in a `.fsproj`; writes nothing. |

### NuGet / types

| Tool | What it does |
|------|--------------|
| `fcs_nuget_types` | Enumerate types in one referenced assembly (exact `SimpleName` match, case-insensitive). |
| `fcs_nuget_members` | Enumerate members of one type from a referenced assembly. |
| `fcs_referenced_symbols` | Substring search across all referenced assemblies (NuGet + framework). |

### Raw LSP proxies

Require `set_project`. Prefer the semantic tools above for agent flows; these are useful for exact-position editor operations and FSAC debugging.

| Tool | What it does |
|------|--------------|
| `textDocument_completion` | Raw LSP completion at an exact position. |
| `textDocument_formatting` | Raw LSP formatting via Fantomas; returns edits, does not write to disk. |
| `textDocument_codeAction` | Raw LSP code-action proxy (with empty diagnostic context). |
| `textDocument_rename` | Raw LSP semantic rename; returns `WorkspaceEdit`. |
| `fcs_signature_help` | Exact-position FCS signature help around a call site. |
| `fsharp_signature_data` | Structured FSAC `fsharp/signatureData` at an exact call-site position. |

### Meta

| Tool | What it does |
|------|--------------|
| `fslangmcp_version` | Returns installed version. Zero-arg. Use when filing UX feedback. |
| `fsharp_runtime_status` | Read-only runtime snapshot: heap sizes, GC counts, FCS cache size, FSAC working set. |

## Example Agent Session

```
1. set_project  {"projectPath": "/abs/path/MyApp.sln"}
   → readiness.lsp=true, loadedProjects=[...], fslangmcpVersion="0.12.1"

2. check  {}
   → verdict="clean"

3. find  {"query": "OrderId", "kind": "definition"}
   → [Domain/Order.fs:12 definition `type OrderId = ...`, ...]

4. fcs_file_outline  {"path": "/abs/path/Domain/Order.fs"}
   → module/type headers, memberCounts by kind

5. fcs_rename_preview  {"path": "/abs/path/Domain/Order.fs", "line": 12, "character": 5, "newName": "OrderIdentifier"}
   → edits grouped by file, totalEdits=7, crossProject=false
```

## Runtime Options

Command-line args:

- `--project <path>` / `-p <path>` — pre-load a project on startup
- `--fsac-command <cmd>` — override the `fsautocomplete` executable
- `--fsac-args "<args>"` — pass extra args to FSAC
- `--bootstrap-tools` — install/update `fsautocomplete` + `ionide.projinfo.tool`

Environment fallbacks: `FSAC_COMMAND`, `FSAC_ARGS`, `FSA_PROJECT_PATH`.

### Garbage collector

FsLangMCP ships with **Workstation GC + Concurrent GC** baked in via `runtimeconfig.template.json`. This is the recommended profile for stdio MCP servers per [FsMcp's runtime tuning guide](https://github.com/Neftedollar/FsMcp/blob/main/docs/runtime-tuning.md): FCS workloads are bursty (load → idle → next request), and Server GC's "don't release until OS pressure" produces alarming RSS growth on dev laptops. Override if needed:

- `DOTNET_gcServer=1` — opt into Server GC (higher throughput, holds memory longer)
- `DOTNET_GCHeapHardLimitPercent=0xA` — cap heap at 10% of RAM (either GC mode)

## MCP Stdio Config

**Installed tool:**

```json
{
  "mcpServers": {
    "fsharp": {
      "command": "fslangmcp",
      "args": ["--project", "/absolute/path/to/App.fsproj"]
    }
  }
}
```

**Local dev (without install):**

```json
{
  "mcpServers": {
    "fsharp": {
      "command": "dotnet",
      "args": [
        "run", "--project", "/path/to/FsLangMcp.fsproj",
        "--", "--project", "/absolute/path/to/App.fsproj"
      ]
    }
  }
}
```

## Parallel Agent Usage

Pass `projectPath` explicitly on every FCS tool call — FCS tools cache project-wide results per resolved `.fsproj`, so agents targeting different projects share no stale caches.

```json
{ "path": "/absolute/path/to/File.fs", "projectPath": "/absolute/path/to/App.fsproj" }
```

Concurrency limits (env-overridable):

- `FSLANGMCP_MAX_CONCURRENT_FCS=2`
- `FSLANGMCP_MAX_CONCURRENT_LSP=1` (LSP tools serialize to protect FSAC workspace state)

## Response Shape

- **Success**: `{"status": "ok", ...fields}` or `{"status": "ok", "result": <payload>}`
- **Not ready**: `{"status": "not_ready", "message": "..."}` — FSAC workspace still loading
- **Invalid args**: `{"status": "invalid_args", "message": "..."}` — required arg blank/missing
- **Error**: MCP protocol error with `{"errorKind": "...", "message": "..."}` payload

LSP positions (`line`, `character`) are **0-based**.

## Development Commands

```bash
just restore
just check    # build + tests
just analyze  # F# analyzers via FSharp.Analyzers.Build
```

Wired analyzers: `Ionide.Analyzers`, `G-Research.FSharp.Analyzers`, `FSharp.Analyzers.Build`.

For local development, restore the local analyzer tool:

```bash
dotnet tool restore
```

## Known Issues

- LSP proxy tools return `{"status": "not_ready"}` if called before `fsautocomplete` finishes loading. `set_project` waits up to 30 seconds — if still `not_ready`, the project may be too large or FSAC may have failed to start.
- FCS tools fall back to script-style inference (`GetProjectOptionsFromScript`) only when no `.fsproj` is found. Diagnostics and symbol data can be incomplete for multi-project solutions in this mode.
- `project_health` is project-focused — it does not inspect whole solutions or resolve ambiguous directories.

## About Tool Dependencies

`dotnet tool` packages cannot automatically install other global tools during installation. `fslangmcp --bootstrap-tools` runs `dotnet tool update -g` (or install if missing) for:

- `fsautocomplete`
- `ionide.projinfo.tool`

To install them manually instead:

```bash
dotnet tool install -g fsautocomplete
dotnet tool install -g ionide.projinfo.tool
```
