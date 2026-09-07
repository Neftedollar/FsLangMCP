---
title: Getting started
description: Install FsLangMCP, connect an MCP client, and run the core set_project → find → check loop.
---

FsLangMCP is an MCP stdio server that gives AI coding agents real F# compiler semantics — cross-project `find`, a trustworthy `check` verdict, type inspection, rename preview, dead-code detection, and more — via FCS in-process and an FsAutoComplete LSP child process. The single sentence version: it replaces grep with the actual compiler. For the full motivation, see [Why your AI agent shouldn't grep F#](why-agents-grep-fsharp.md).

## Prerequisites

- .NET SDK 10 or later on PATH (`dotnet --version`)

## Install

```bash
dotnet tool install -g FsLangMcp
fslangmcp --bootstrap-tools
```

FsLangMCP delegates LSP, formatting, and project evaluation to external tools.
Bootstrap reads the exact FSAC, Fantomas, and ProjInfo pins embedded in the
installed FsLangMCP release, installs or downgrades to them, and creates the
Fantomas command alias required by FSAC. It never selects an unreviewed latest
version.

## Configure your MCP client

Tell your agent's MCP client where to find the server. The minimal config (works in any client that accepts the standard JSON format):

```json
{
  "mcpServers": {
    "fslangmcp": { "command": "fslangmcp" }
  }
}
```

For per-client instructions — Claude Code, Cursor, Codex, Copilot, generic stdio — see [MCP client configuration](configuration.md).

## The core loop

### Step 1 — initialize once per session

Call `set_project` with the path to your `.fsproj`, `.sln`, `.slnx`, or project directory. A directory is searched recursively when it has no top-level workspace file: one nested project/solution is selected, while multiple candidates return `ambiguous_workspace` so you can choose explicitly. You only need to do this once; the project context persists for all subsequent calls.

```text
set_project { "projectPath": "/absolute/path/to/MyApp.sln" }
```

The response includes `readiness` flags (`lsp`, `projectOptions`, `symbolIndex`) and `loadedProjects`. Wait until `readiness.lsp` is `true` before calling LSP-proxy tools. If `symbolIndex` is `false`, follow `symbolIndexState` and the actionable `symbolIndexHint`; `check` and `find`'s own FCS sweep remain available while the FSAC index warms. Only `find`'s zero-hit fallback rides on the index — it reports `fsacFallbackState` when it could not run.

### Step 2 — use `find` and `check` as your primary entry points

**`find`** answers "where is X used or defined?" across every project in the solution. It resolves definitions, references, record-field set sites, and member call-sites in one sweep — no grep noise from comments or unrelated types:

```text
find { "query": "OrderId", "kind": "definition" }
find { "query": "OrderId" }                        // all sites
find { "query": "Ship", "kind": "members", "member": "Ship" }  // member call-sites
```

For an exhaustive refactor count, require both `coverage.complete=true` (all requested projects
were analyzed) and `resolution.complete=true` (this response contains the whole site set). Follow
`nextCursor` while `truncated=true`; a final cursor page is not exhaustive by itself because it
omits earlier pages.

**`check`** answers "did my edit compile?" with a fresh in-process type-check. It never reports a stale-cache false-clean:

```text
check {}                           // whole workspace, auto scope
check { "scope": "file", "path": "/abs/path/Domain/Order.fs" }
```

`clean` is scoped to the current FCS/check profile. It is not proof that every build
configuration is clean: optimized Release compilation can surface configuration-specific
diagnostics such as FS3511. Before merge or release, run
`dotnet build -c Release --warnaserror`.

### Step 3 — dig deeper as needed

Once you have a base signal from `find` and `check`, the other 33 tools let you go deeper: understand project structure (`fcs_project_outline`, `fcs_file_outline`), diagnose errors (`fcs_explain_diagnostic`, `fcs_suggest_open`), preview refactors (`fcs_rename_preview`, `fcs_refactor_impact`), scan for cleanup candidates (`fcs_dead_code`, `fcs_review_scan`), and more.

### Minimal session example

```
1. set_project  {"projectPath": "/abs/path/MyApp.sln"}
   → readiness.lsp=true, loadedProjects=[...], fslangmcpVersion="0.18.0"

2. check  {}
   → verdict="clean"

3. find  {"query": "OrderId", "kind": "definition"}
   → [Domain/Order.fs:12 definition `type OrderId = ...`]

4. fcs_file_outline  {"path": "/abs/path/Domain/Order.fs"}
   → module/type headers, memberCounts by kind

5. fcs_rename_preview  {"path": "/abs/path/Domain/Order.fs", "line": 12, "character": 5, "newName": "OrderIdentifier"}
   → edits grouped by file, totalEdits=7, crossProject=false
```

## Next steps

- **Hands-on examples**: [the quickstart repository](https://github.com/Neftedollar/FsLangMCP/tree/main/examples) — runnable traces for common agent tasks.
- **All 35 tools**: [Tools reference](tools-reference.md) — the complete surface grouped by intent.
- **Troubleshooting**: [Troubleshooting](troubleshooting.md) — symptom-keyed remediation.
- **Multi-agent patterns**: [Agent integration guide](https://github.com/Neftedollar/FsLangMCP/blob/main/AGENT_INTEGRATION.md) — tool-discipline snippets, subagent briefs, and feedback routing.
