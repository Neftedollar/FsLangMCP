# FsLangMCP (F# + FsMcp + FsAutoComplete + FCS)

[![CI](https://github.com/Neftedollar/FsLangMCP/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/FsLangMCP/actions/workflows/ci.yml)

An MCP server written in F# that combines:

- `fsautocomplete` (LSP bridge for editor-like features)
- `FSharp.Compiler.Service` (compiler-semantic features for AI workflows)

> Work in progress: APIs and tool shapes may still change.

## Response Shape

All tools return a consistent JSON envelope:

- **Success**: `{"status": "ok", "result": <payload>}` (LSP tools) or `{"status": "ok", ...fields}` (FCS tools)
- **Not ready**: `{"status": "not_ready", "message": "..."}` — LSP workspace still loading
- **Error**: MCP protocol error with `{"errorKind": "...", "message": "..."}` payload

## What You Get

### LSP-proxy tools (via `fsautocomplete`)

- `textDocument_completion`
- `textDocument_hover`
- `textDocument_definition`
- `textDocument_references`
- `textDocument_formatting` — format F# file via Fantomas (via fsautocomplete)
- `textDocument_codeAction` — available code actions / quick fixes at cursor
- `textDocument_rename` — rename a symbol across the project
- `workspace_symbol`
- `workspace_diagnostics` (cache of latest `publishDiagnostics`)
- `fsharp_compile` — structured validation via FSAC `fsharp/compile`
- `set_project` (switch active project/workspace for LSP context)

LSP positions (`line`, `character`) are **0-based**.

The `textDocument_*` and `workspace_*` tools are raw LSP/IDE-shaped proxies. They are useful for exact-position editor operations and FSAC debugging. Prefer the FCS tools and `project_health` for agent-friendly project understanding.

### FCS tools (compiler semantics)

- `fcs_parse_and_check_file`
- `fcs_file_symbols`
- `fcs_project_symbol_uses`
- `fcs_type_at_position` — inferred F# type and symbol info at cursor (works without LSP workspace)
- `fcs_signature_help` — method overloads and parameter info at cursor
- `fcs_get_project_options` — get `OtherOptions` for a `.fsproj` via `proj-info`; use the result as `projectOptions` in other FCS tools

### Project preflight

- `project_health` — fast read-only project preflight. Reports project options availability, source file readability, analyzer setup, test project discovery, and current LSP readiness. It does not start/switch FSAC, run compile, or run tests.

## Prerequisites

- .NET SDK 10+
- `fsautocomplete` installed
- `ionide.projinfo.tool` (required for `fcs_get_project_options`)

Install `fsautocomplete`:

```bash
dotnet tool install -g fsautocomplete
```

Install `ionide.projinfo.tool`:

```bash
dotnet tool install -g ionide.projinfo.tool
```

For local development of this repository, restore the local analyzer tool:

```bash
dotnet tool restore
```

## Install As Dotnet Tool

From this repo:

```bash
dotnet pack -c Release
dotnet tool install -g --add-source ./nupkg FsLangMcp
fslangmcp --bootstrap-tools
```

After install, command is:

```bash
fslangmcp
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
- `fcs_project_symbol_uses` with `symbolQuery` for project-wide references.
- `workspace_diagnostics` to inspect current FSAC/compiler/analyzer diagnostics.
- `fsharp_compile` for structured compile validation through FSAC.

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

Use `fsharp_compile` after `set_project` when you want FSAC-backed structured compile validation.

Input shape:

```json
{
  "projectPath": "/absolute/path/to/App.fsproj",
  "workspacePath": null,
  "timeoutMs": 60000
}
```

`fsharp_compile` returns a compact status plus the raw FSAC result. It does not run tests.

## Known Issues

- LSP proxy tools return `{"status": "not_ready"}` if called before `fsautocomplete` has finished loading the project. `set_project` waits up to 30 seconds — if you still get `not_ready`, the project may be too large or `fsautocomplete` may have failed to start.
- `fsharp_compile` depends on the loaded FSAC workspace and the currently installed FSAC custom endpoint behavior.
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
