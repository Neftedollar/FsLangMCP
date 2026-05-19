# FsLangMCP (F# + FsMcp + FsAutoComplete + FCS)

[![CI](https://github.com/Neftedollar/FsLangMCP/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/FsLangMCP/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FsLangMcp.svg)](https://www.nuget.org/packages/FsLangMcp/)

FsLangMcp is an MCP server for AI coding agents working on F# projects. It combines `fsautocomplete` (LSP-shaped editor semantics) and `FSharp.Compiler.Service` (in-process compiler semantics) into a single stdio process with 32 tools designed for agent workflows. Responses are paginated, errors are returned as structured `invalid_args` envelopes, `fcs_check_file` invalidates stale caches after edits, `fcs_validate_snippet` compiles arbitrary F# against your project's references, and `fcs_record_field_audit` catches the record-construction sites that textual search misses.

See the [Quickstart](#quickstart) for the 60-second on-ramp.

### Components

- `fsautocomplete` (LSP bridge for editor-like features)
- `FSharp.Compiler.Service` (compiler-semantic features for AI workflows)

> Work in progress: APIs and tool shapes may still change.

## Quickstart

1. **Install** (.NET 10 required):

   ```bash
   dotnet tool install -g FsLangMcp
   fslangmcp --bootstrap-tools   # one-time: fetches fsautocomplete + ionide.projinfo.tool
   ```

2. **Add to your MCP client config** (Claude Code example — adjust for your client):

   ```json
   { "mcpServers": { "fslangmcp": { "command": "fslangmcp" } } }
   ```

3. **Your first call**: from the MCP client, call `set_project` with your `.fsproj` path:

   ```json
   { "projectPath": "/absolute/path/to/YourApp.fsproj" }
   ```

   You'll get back a response with `fslangmcpVersion`, `loadedProjects`, and `readiness` flags. When `readiness.lsp` is `true`, you can call any other tool.

4. **Try a real tool**: call `project_health` for a fast preflight on the project:

   ```json
   { }
   ```

   (No args needed — `set_project` set the default.) The response tells you which compile files exist, which test projects were detected, and whether the LSP and FCS layers are ready.

That's it. From here you can browse the [tool catalog](#what-you-get) for the right tool for your task, or skim [troubleshooting](docs/troubleshooting.md) when something looks off.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for the full release history.

## Response Shape

All tools return a consistent JSON envelope:

- **Success**: `{"status": "ok", "result": <payload>}` (LSP tools) or `{"status": "ok", ...fields}` (FCS tools)
- **Not ready**: `{"status": "not_ready", "message": "..."}` — LSP workspace still loading
- **Invalid args**: `{"status": "invalid_args", "message": "..."}` — required string arg blank/missing on `fcs_record_field_audit` / `fcs_project_symbol_uses` / `fcs_find_member_usages` / `fcs_find_symbol` / `fcs_referenced_symbols` / `fcs_nuget_types`. Standardized in 0.8.2 (#120).
- **Error**: MCP protocol error with `{"errorKind": "...", "message": "..."}` payload

LSP positions (`line`, `character`) are **0-based**.

## What You Get

The `textDocument_*` and `workspace_*` tools are raw LSP/IDE-shaped proxies — useful for exact-position editor operations and FSAC debugging. Prefer the FCS tools and `project_health` for agent-friendly project understanding.

### LSP-proxy tools (FSAC-backed)

All require a prior `set_project`. Tagged `[FSAC]` in tool descriptions.

| Tool | What it does |
|------|--------------|
| `textDocument_completion` | Raw LSP proxy for completion at an exact position. |
| `textDocument_definition` | Raw LSP proxy for go-to-definition at an exact position. |
| `textDocument_references` | Raw LSP proxy for find-references at an exact position. For query-based agent workflows prefer `fcs_project_symbol_uses` / `fcs_find_symbol`. |
| `textDocument_formatting` | Raw LSP formatting proxy via Fantomas. Returns formatted text and edits; does not write to disk. |
| `textDocument_codeAction` | Raw LSP codeAction proxy at an exact position with empty diagnostic context. Useful for debugging FSAC. |
| `textDocument_rename` | Raw LSP semantic rename at an exact position. Returns raw `WorkspaceEdit`. |
| `workspace_symbol` | Quick lookup after `set_project`. IDE-shaped results without source context. |
| `workspace_diagnostics` | Cached LSP `publishDiagnostics` payload, scoped to one file or to the whole workspace. Optional `path`, `fileGlob`, `severity` filters. |
| `fsharp_signature_data` | Structured FSAC signature help via `fsharp/signatureData` at an exact call-site position. |

### FCS in-process tools (compiler semantics)

Tagged `[FCS in-process]` in tool descriptions. Most accept an optional `projectPath` that falls back to the active `set_project`.

| Tool | What it does |
|------|--------------|
| `fcs_parse_and_check_file` | Parse + typecheck one file. Pass `text` for unsaved content. |
| `fcs_check_file` | Cache-invalidating parse + typecheck for one file. Surgically drops cached project-options + project-results entries for THIS project and calls `InvalidateConfiguration` before re-running. Use when `workspace_diagnostics` looks stale right after an `Edit` / `Write`. |
| `fcs_file_symbols` | Raw FCS symbol extraction for one file. `includeAllUses` adds locals / parameters / usages. Prefer `fcs_file_outline` for normal navigation. |
| `fcs_file_outline` | Compact per-file outline filtered to definitions: name, kind, range, signature/type, accessibility, declaration range. |
| `fcs_project_outline` | Compact project-wide outline over filtered compile files. Use `maxFiles` / `maxResultsPerFile` on large projects. |
| `fcs_project_symbol_uses` | Project-wide symbol-use search by symbol name / full name. Cached by resolved project options. |
| `fcs_find_symbol` | Project-wide search with grouped definitions / references and source line context. Better than chaining `workspace_symbol` + `fcs_project_symbol_uses` + shell line reads. Misses record-field-set construction sites — for those, use `fcs_record_field_audit`. |
| `fcs_find_member_usages` | Find all usage sites of a specific `(typeName, memberName)`. FCS-resolved so dotted access, pipeline application, and overload resolution are handled correctly (unlike a textual `rg`). |
| `fcs_record_field_audit` | Find every construction site for a `(typeName, fieldName)` pair — both `{ Field = expr; ... }` literal form and `{ x with Field = expr }` update form. Closes the gap where `fcs_find_symbol` / `textDocument_references` look up the type name and miss field-set uses. |
| `fcs_symbol_at_word` | Tolerant FCS symbol lookup by line + word + occurrence. Prefer over exact-position hover/type queries. |
| `fcs_type_at_position` | Low-level exact-position FCS type/symbol query. Requires `set_project` (or explicit `projectPath` / `projectOptions`). Pass `fuzzy=true` to snap to nearest symbol within ±2 lines / ±5 cols. |
| `fcs_signature_help` | Exact-position FCS signature help around a call site. |
| `fcs_make_internal_visible` | Drops the `private` keyword from a `let` / `module` / `type` / `member` / `val` / `new` / `static` / `abstract` / `override` declaration at a given position. Returns a workspace edit; does NOT write the file. |
| `fcs_validate_snippet` | Compile an arbitrary F# snippet (`.fs` or `.fsi` mode) against the loaded project's references without modifying the project on disk. |
| `fcs_referenced_symbols` | Search across the project's *referenced* assemblies (NuGet + framework) for types whose `DisplayName` / `FullName` contains the query (case-insensitive). Reports assembly, kind, accessibility, `isObsolete`. |
| `fcs_nuget_types` | Enumerate types exported by one referenced assembly, matched by **exact** `SimpleName` (case-insensitive). Does NOT silently fall back to a less-specific assembly. To discover assembly names, use `fcs_referenced_symbols` first. |
| `fcs_get_project_options` | Get `OtherOptions` for a `.fsproj` via `proj-info`; use the result as `projectOptions` in other FCS tools. |
| `fsharp_compile` | FCS project validation. Loads `.fsproj` options through `Ionide.ProjInfo`, then runs `FSharpChecker.ParseAndCheckProject`. Does not run `dotnet build`, emit assemblies, or run tests. |
| `fsharp_project_inspect` | Read-only `.fsproj` inspection: compile order, references, source summary, and signature/implementation pairing. |

### Meta / workflow tools

| Tool | What it does |
|------|--------------|
| `set_project` | `[FSAC]` Initialize or switch the FSAC/LSP project context. Must be called before `textDocument_*` and `workspace_*` tools. Accepts `.fsproj`, `.sln`, `.slnx`, or directory. Waits up to 30s for workspace load and clears FCS caches. |
| `project_health` | `[FCS in-process]` Fast read-only preflight: project options availability, source file readability, analyzer setup, test project discovery (`isTestProject`, `testFrameworks`, `testCount`, `lastBuildSucceeded`, `lastBuildAt`, `binaryOutputPath`), and current LSP readiness. Does not start FSAC, build, or test. |
| `fsharp_runtime_status` | `[FCS in-process]` Read-only observational snapshot: managed-heap sizes by generation/LOH/POH, GC counts, `isServerGC`, assembly load count, FCS checker config + project-results cache size, FSAC child-process working set. Numbers only — no interpretation. |
| `fslangmcp_version` | `[meta]` Zero-arg. Returns the installed FsLangMCP product version and name. Same value is surfaced in `set_project.result.fslangmcpVersion` and `fsharp_runtime_status.fslangmcpVersion`. Use when filing UX feedback. |

### Notable response fields

- **`set_project`** — response includes `fslangmcpVersion` (since 0.8.0, #115), `loadedProjects: string[]` (`.fsproj` paths discovered), and `readiness: { lsp, projectOptions, symbolIndex }` flags (since 0.5.6, #106).
- **`workspace_diagnostics`** — single-file responses include `analyzedAt`; workspace-wide responses include `mostRecentAnalyzedAt` and `analyzedAtByUri` (per-URI `DateTimeOffset` map). Since 0.8.0 (#116). When `fileGlob` is supplied, `mostRecentAnalyzedAt` is scoped to the glob-filtered subset (since 0.8.2, #123).
- **`fcs_find_symbol`** — `projectDiagnostics` is scoped. `projectDiagnosticsScope="matched-files"` (normal) returns diagnostics only for files containing matches, with Info/Hint filtered out by default (set `includeInfo=true` to include). `projectDiagnosticsScope="errors-only-no-matches"` (zero-match case) surfaces error-severity diagnostics from the whole project so callers can detect broken projects. Since 0.8.0 / 0.8.1 (#114).
- **`fsharp_runtime_status`** — response includes top-level `fslangmcpVersion`. Since 0.8.0 (#115).

## Install As Dotnet Tool

**Prerequisites**: .NET SDK 10+ on PATH. `fsautocomplete` and `ionide.projinfo.tool`
are fetched automatically by `--bootstrap-tools` below.

### From NuGet (recommended)

```bash
dotnet tool install -g FsLangMcp
fslangmcp --bootstrap-tools   # one-time: fetches transitive F# tooling (fsautocomplete + ionide.projinfo.tool)
```

Update later with `dotnet tool update -g FsLangMcp`.

### From source (developing FsLangMCP itself)

```bash
dotnet pack -c Release
dotnet tool install -g --add-source ./nupkg FsLangMcp
fslangmcp --bootstrap-tools   # one-time: fetches transitive F# tooling
```

For local development of this repository, restore the local analyzer tool:

```bash
dotnet tool restore
```

After install, command is:

```bash
fslangmcp
```

If you'd rather install the transitive tools yourself instead of using
`--bootstrap-tools`:

```bash
dotnet tool install -g fsautocomplete
dotnet tool install -g ionide.projinfo.tool
```

## Runtime Options

Pass paths as command args (recommended):

- `--project <path-to-fsproj>` or `-p <path-to-fsproj>`
- `--fsac-command <cmd>` (optional override)
- `--fsac-args "<args>"` (optional override)
- `--bootstrap-tools` (install/update `fsautocomplete` and `ionide.projinfo.tool`)

Environment fallbacks still work:

- `FSAC_COMMAND`
- `FSAC_ARGS`
- `FSA_PROJECT_PATH`

### Garbage collector — tuned for stdio servers

FsLangMCP ships with **Workstation GC + Concurrent GC** baked in via `runtimeconfig.template.json`. This is the recommended profile for stdio MCP servers per [FsMcp's runtime tuning guide](https://github.com/Neftedollar/FsMcp/blob/main/docs/runtime-tuning.md): an FCS workload is bursty (load → idle → next request), and Server GC's "don't release until OS pressure" produces alarming RSS growth on dev laptops that won't trigger pressure events.

Operators can override at the env-var level if needed (env always wins over `runtimeconfig`):

- `DOTNET_gcServer=1` — opt back into Server GC (higher throughput, holds memory longer)
- `DOTNET_GCHeapHardLimitPercent=0xA` — cap heap at 10% of RAM (works with either GC mode)

## MCP Stdio Config (Installed Tool)

```json
{
  "mcpServers": {
    "fsharp": {
      "command": "fslangmcp",
      "args": ["--project", "/absolute/path/to/App.fsproj"],
      "env": {
        "FSAC_COMMAND": "fsautocomplete"
      }
    }
  }
}
```

## Parallel Agent Usage

For multiple agents sharing one FsLangMCP server, prefer the FCS tools and pass `projectPath` on every request:

```json
{
  "path": "/absolute/path/to/File.fs",
  "projectPath": "/absolute/path/to/App.fsproj"
}
```

FCS tools resolve compiler options per project and cache project-wide results by the resolved `.fsproj`, so agents working on different projects do not share stale symbol caches. The server also limits expensive work by default:

- `FSLANGMCP_MAX_CONCURRENT_FCS=2`
- `FSLANGMCP_MAX_CONCURRENT_LSP=1`

The LSP-proxy tools use one `fsautocomplete` workspace per server process. They are serialized to avoid corrupting the active document/workspace state, but they are not intended for independent parallel workspaces in the same MCP process.

## Example Agent Session

Start by selecting a project:

```json
{
  "projectPath": "/absolute/path/to/App.fsproj",
  "workspacePath": null,
  "restartLsp": true
}
```

Then ask for a health report:

```json
{
  "projectPath": "/absolute/path/to/App.fsproj",
  "workspacePath": null,
  "scope": null,
  "compileCheck": null
}
```

Typical next calls:

- `fcs_parse_and_check_file` with `projectPath` for one file.
- `fcs_file_outline` for a compact map of a file.
- `fcs_find_symbol` when you need definitions/references plus source context.
- `fcs_symbol_at_word` when you know the line and word but not the exact cursor column.
- `fcs_project_symbol_uses` with `symbolQuery` for project-wide references.
- `workspace_diagnostics` to inspect current FSAC/compiler/analyzer diagnostics.
- `fsharp_compile` for project-wide FCS parse+typecheck validation.

For repeated multi-agent use, pass `projectPath` directly to FCS tools instead of relying on mutable LSP workspace state.

## Development Commands

```bash
just restore
just check
just analyze
```

- `just check` runs build + tests.
- `just analyze` runs F# analyzers through `FSharp.Analyzers.Build`.

The repository currently wires:

- `Ionide.Analyzers`
- `G-Research.FSharp.Analyzers`
- `FSharp.Analyzers.Build`
- local `fsharp-analyzers` tool

## MCP Stdio Config (Local Dev Without Install)

```json
{
  "mcpServers": {
    "fsharp": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/path/to/FsLangMcp.fsproj",
        "--",
        "--project",
        "/absolute/path/to/App.fsproj"
      ],
      "env": {
        "FSAC_COMMAND": "fsautocomplete"
      }
    }
  }
}
```

## How To Get `projectOptions` For FCS Tools

For accurate `.fsproj` / `.sln` / `.slnx` context, use `ionide/proj-info`:

```bash
dotnet tool install -g ionide.projinfo.tool
proj-info --project /absolute/path/to/App.fsproj --fcs --serialize
```

Use `OtherOptions` from the emitted JSON as `projectOptions` in:

- `fcs_parse_and_check_file`
- `fcs_file_symbols`
- `fcs_project_symbol_uses`

If both `projectPath` and `projectOptions` are omitted, the server first tries to auto-discover the nearest `.fsproj`. If no project can be found, it falls back to script-style inference (`GetProjectOptionsFromScript`), which is less accurate for large multi-project solutions.

## `set_project` Tool

Use this MCP tool to switch active project context without restarting the MCP process.

Input shape:

```json
{
  "projectPath": "/absolute/path/to/App.fsproj",
  "workspacePath": null,
  "restartLsp": true
}
```

`restartLsp` defaults to `true` so the new context is applied immediately.

## `project_health` Tool

Use `project_health` before deeper semantic work when you need to know whether FsLangMCP can trust the project state.

Input shape:

```json
{
  "projectPath": "/absolute/path/to/App.fsproj",
  "workspacePath": null,
  "scope": null,
  "compileCheck": null
}
```

Notes:

- v0.3 expects an explicit `.fsproj` path or a directory with exactly one `.fsproj`.
- Missing analyzers are reported as capability information, not as a health failure.
- Compile status is separate from tooling readiness.
- `project_health` does not run build or tests.

## `fsharp_compile` Tool

Use `fsharp_compile` when you want read-only project-wide FCS validation without running `dotnet build`.

Input shape:

```json
{
  "projectPath": "/absolute/path/to/App.fsproj",
  "workspacePath": null,
  "timeoutMs": 60000
}
```

`fsharp_compile` returns a compact status, diagnostic counts, and FCS diagnostics. It uses `FSharpChecker.ParseAndCheckProject`, so it checks parse/typecheck errors across the project, but it does not execute the full MSBuild build pipeline, emit an assembly, or run tests.

## Known Issues

- LSP proxy tools return `{"status": "not_ready"}` if called before `fsautocomplete` has finished loading the project. `set_project` waits up to 30 seconds — if you still get `not_ready`, the project may be too large or `fsautocomplete` may have failed to start.
- `project_health` v0.3 is intentionally project-focused. It does not yet inspect whole solutions or resolve ambiguous directories.
- FCS tools fall back to script-style inference (`GetProjectOptionsFromScript`) only when neither `projectPath` nor an auto-discovered `.fsproj` is available. In that mode, diagnostics and symbol data can be incomplete for multi-project solutions.
- `fcs_project_symbol_uses` results are cached per project. Call `set_project` to flush the cache when source files change on disk.

## About Tool Dependencies

`dotnet tool` packages cannot automatically install other global tools during installation.
This project provides:

- `fslangmcp --bootstrap-tools`

It runs `dotnet tool update -g ...` (or install if missing) for:

- `fsautocomplete`
- `ionide.projinfo.tool`
