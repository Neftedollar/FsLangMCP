# FsLangMCP (F# + FsMcp + FsAutoComplete + FCS)

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
- `workspace_symbol`
- `workspace_diagnostics` (cache of latest `publishDiagnostics`)
- `set_project` (switch active project/workspace for LSP context)

LSP positions (`line`, `character`) are **0-based**.

### FCS tools (compiler semantics)

- `fcs_parse_and_check_file`
- `fcs_file_symbols`
- `fcs_project_symbol_uses`

## Prerequisites

- .NET SDK 10+
- `fsautocomplete` installed

Install `fsautocomplete`:

```bash
dotnet tool install -g fsautocomplete
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
        "/Users/roman/FsLangMCP/FsLangMcp.fsproj",
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

- LSP proxy tools (`textDocument_hover`, `textDocument_definition`, `textDocument_completion`, `textDocument_references`) may return an error like:
  `"Couldn't find <file> in LoadedProjects..."` if `fsautocomplete` has not loaded the target project yet.
  This usually means the workspace/project context is not fully ready.
- FCS tools fall back to script-style inference (`GetProjectOptionsFromScript`) when `projectOptions` is not provided.
  In that mode, diagnostics and symbol data can be incomplete for multi-project solutions.

## About Tool Dependencies

`dotnet tool` packages cannot automatically install other global tools during installation.
This project provides:

- `fslangmcp --bootstrap-tools`

It runs `dotnet tool update -g ...` (or install if missing) for:

- `fsautocomplete`
- `ionide.projinfo.tool`
