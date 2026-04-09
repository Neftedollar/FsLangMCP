# FsLangMCP (F# + FsMcp + FsAutoComplete + FCS)

[![CI](https://github.com/Neftedollar/FsLangMCP/actions/workflows/ci.yml/badge.svg)](https://github.com/Neftedollar/FsLangMCP/actions/workflows/ci.yml)

An MCP server written in F# that combines:

- `fsautocomplete` (LSP bridge for editor-like features)
- `FSharp.Compiler.Service` (compiler-semantic features for AI workflows)

> Work in progress: APIs and tool shapes may still change.

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
- `set_project` (switch active project/workspace for LSP context)

LSP positions (`line`, `character`) are **0-based**.

### FCS tools (compiler semantics)

- `fcs_parse_and_check_file`
- `fcs_file_symbols`
- `fcs_project_symbol_uses`
- `fcs_type_at_position` — inferred F# type and symbol info at cursor (works without LSP workspace)
- `fcs_signature_help` — method overloads and parameter info at cursor
- `fcs_get_project_options` — get `OtherOptions` for a `.fsproj` via `proj-info`; use the result as `projectOptions` in other FCS tools

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

If `projectOptions` is omitted, the server falls back to script-style inference (`GetProjectOptionsFromScript`), which is less accurate for large multi-project solutions.

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

## Known Issues

- LSP proxy tools return `{"status": "not_ready"}` if called before `fsautocomplete` has finished loading the project. `set_project` waits up to 30 seconds — if you still get `not_ready`, the project may be too large or `fsautocomplete` may have failed to start.
- FCS tools fall back to script-style inference (`GetProjectOptionsFromScript`) when `projectOptions` is not provided. In that mode, diagnostics and symbol data can be incomplete for multi-project solutions.
- `fcs_project_symbol_uses` results are cached per project. Call `set_project` to flush the cache when source files change on disk.

## About Tool Dependencies

`dotnet tool` packages cannot automatically install other global tools during installation.
This project provides:

- `fslangmcp --bootstrap-tools`

It runs `dotnet tool update -g ...` (or install if missing) for:

- `fsautocomplete`
- `ionide.projinfo.tool`
