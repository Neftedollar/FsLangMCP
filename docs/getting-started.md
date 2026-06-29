# Getting Started with FsLangMCP

FsLangMCP is an MCP stdio server that gives AI coding agents real F# compiler semantics — cross-project `find`, a trustworthy `check` verdict, type inspection, rename preview, dead-code detection, and more — via FCS in-process and an FsAutoComplete LSP child process. The single sentence version: it replaces grep with the actual compiler. For the full motivation, see [`docs/why-agents-grep-fsharp.md`](why-agents-grep-fsharp.md).

## Prerequisites

- .NET SDK 10 or later on PATH (`dotnet --version`)

## Install

```bash
dotnet tool install -g FsLangMcp
fslangmcp --bootstrap-tools   # one-time: installs fsautocomplete + ionide.projinfo.tool
```

`--bootstrap-tools` is required after first install. It runs `dotnet tool update -g` for the two tools FsLangMCP delegates to at runtime. To install them manually instead:

```bash
dotnet tool install -g fsautocomplete
dotnet tool install -g ionide.projinfo.tool
```

## Configure your MCP client

Tell your agent's MCP client where to find the server. The minimal config (works in any client that accepts the standard JSON format):

```json
{
  "mcpServers": {
    "fslangmcp": { "command": "fslangmcp" }
  }
}
```

For per-client instructions — Claude Code, Cursor, Codex, Copilot, generic stdio — see [`docs/configuration.md`](configuration.md).

## The core loop

### Step 1 — initialize once per session

Call `set_project` with the path to your `.fsproj`, `.sln`, `.slnx`, or project directory. You only need to do this once; the project context persists for all subsequent calls.

```json
set_project { "projectPath": "/absolute/path/to/MyApp.sln" }
```

The response includes `readiness` flags (`lsp`, `projectOptions`, `symbolIndex`) and `loadedProjects`. Wait until `readiness.lsp` is `true` before calling LSP-proxy tools.

### Step 2 — use `find` and `check` as your primary entry points

**`find`** answers "where is X used or defined?" across every project in the solution. It resolves definitions, references, record-field set sites, and member call-sites in one sweep — no grep noise from comments or unrelated types:

```json
find { "query": "OrderId", "kind": "definition" }
find { "query": "OrderId" }                        // all sites
find { "query": "Ship", "kind": "members", "member": "Ship" }  // member call-sites
```

**`check`** answers "did my edit compile?" with a fresh in-process type-check. It never reports a stale-cache false-clean:

```json
check {}                           // whole workspace, auto scope
check { "scope": "file", "path": "/abs/path/Domain/Order.fs" }
```

### Step 3 — dig deeper as needed

Once you have a base signal from `find` and `check`, the other 33 tools let you go deeper: understand project structure (`fcs_project_outline`, `fcs_file_outline`), diagnose errors (`fcs_explain_diagnostic`, `fcs_suggest_open`), preview refactors (`fcs_rename_preview`, `fcs_refactor_impact`), scan for cleanup candidates (`fcs_dead_code`, `fcs_review_scan`), and more.

### Minimal session example

```
1. set_project  {"projectPath": "/abs/path/MyApp.sln"}
   → readiness.lsp=true, loadedProjects=[...], fslangmcpVersion="0.12.1"

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

- **Hands-on examples**: [`examples/`](../examples/) — runnable traces for common agent tasks.
- **All 35 tools**: [`docs/tools-reference.md`](tools-reference.md) — full reference grouped by intent.
- **Troubleshooting**: [`docs/troubleshooting.md`](troubleshooting.md) — symptom-keyed remediation guide.
- **Multi-agent patterns**: [`AGENT_INTEGRATION.md`](../AGENT_INTEGRATION.md) — tool-discipline snippets for CLAUDE.md / .cursorrules / AGENTS.md, subagent brief templates, feedback routing.
