# MCP Client Configuration

FsLangMCP is a standard MCP stdio server. This page covers per-client wiring. All clients use the same executable (`fslangmcp`) and the same `set_project` first-call pattern; only the config file location and format differ.

**Prerequisite:** install FsLangMCP first — see [`docs/getting-started.md`](getting-started.md).

## Claude Code

Add with the CLI:

```bash
claude mcp add fslangmcp fslangmcp
```

Or create / edit `.mcp.json` in the project root (committed to the repo so all contributors share it):

```json
{
  "mcpServers": {
    "fslangmcp": { "command": "fslangmcp" }
  }
}
```

To pre-load a project at startup, pass it as an argument:

```json
{
  "mcpServers": {
    "fslangmcp": {
      "command": "fslangmcp",
      "args": ["--project", "/absolute/path/to/App.fsproj"]
    }
  }
}
```

`--project` runs the same project-load/readiness pipeline as the MCP `set_project`
tool before the server starts reading stdio. Startup fails rather than serving with
a half-loaded context. When `--project` is omitted, call `set_project` once at the
start of the session.

## Cursor

Create or edit `.cursor/mcp.json` in the repo root:

```json
{
  "mcpServers": {
    "fslangmcp": { "command": "fslangmcp" }
  }
}
```

With a pre-loaded project:

```json
{
  "mcpServers": {
    "fslangmcp": {
      "command": "fslangmcp",
      "args": ["--project", "/absolute/path/to/App.fsproj"]
    }
  }
}
```

When no `--project` argument is configured, call `set_project` as the first tool
call. Add the tool-discipline rule to `.cursorrules` so Cursor's agent knows to
use `find` and `check` instead of grep — the snippet is in
[`AGENT_INTEGRATION.md`](../AGENT_INTEGRATION.md).

## Codex (OpenAI)

Codex CLI reads MCP server configuration from `~/.codex/config.toml`. Each server is a `[mcp_servers.<name>]` table:

```toml
[mcp_servers.fslangmcp]
command = "fslangmcp"
args    = []
```

> Codex's config schema is evolving — see [Codex's MCP documentation](https://developers.openai.com/codex/mcp) for the current exact key names if the above doesn't match your installed version.

Put the tool-discipline rules in `AGENTS.md` at the repo root (Codex's equivalent of `CLAUDE.md`). The snippet from [`AGENT_INTEGRATION.md`](../AGENT_INTEGRATION.md) applies unchanged.

## GitHub Copilot

GitHub Copilot's MCP support is exposed through the IDE extensions (VS Code, Visual Studio, JetBrains) rather than through a standalone CLI MCP config file. Configuration steps differ by IDE.

For **VS Code with GitHub Copilot Chat** (MCP support requires a recent Copilot extension):

Add to your VS Code workspace settings (`.vscode/mcp.json` or user `settings.json`):

```json
{
  "mcp": {
    "servers": {
      "fslangmcp": {
        "type": "stdio",
        "command": "fslangmcp"
      }
    }
  }
}
```

> The exact key path (`mcp.servers` vs. `mcpServers`, and where the file lives) varies by Copilot extension version — see [GitHub Copilot's MCP documentation](https://docs.github.com/en/copilot) for the current format.

Add tool-discipline instructions to `.github/copilot-instructions.md` (Copilot's project-rules file). The `AGENT_INTEGRATION.md` snippet applies.

## Generic MCP stdio

Any MCP client that supports stdio transport can run FsLangMCP. The minimal server entry is:

```json
{
  "command": "fslangmcp"
}
```

or with explicit args:

```json
{
  "command": "fslangmcp",
  "args": ["--project", "/absolute/path/to/App.fsproj"]
}
```

Place it under whatever key your client uses for MCP server definitions (commonly `mcpServers`, `mcp.servers`, or `servers`).

## First-call pattern — all clients

Unless the server was started with `--project` (or `FSA_PROJECT_PATH`), the first
tool call in every agent session must be `set_project`:

```json
set_project { "projectPath": "/absolute/path/to/App.sln" }
```

The LSP-proxy tools (`textDocument_*`, `fsharp_signature_data`) require project
preload to have completed and `readiness.lsp` to be `true` before they return
data. Their responses are bound to `activeProjectPath` and `sessionGeneration`.

## Parallel agent usage

When multiple agents target the same FsLangMCP instance but different projects, pass `projectPath` explicitly on every FCS tool call. FCS caches project-wide results per resolved `.fsproj`, so agents targeting different projects share no stale caches:

```json
{ "path": "/abs/path/File.fs", "projectPath": "/abs/path/App.fsproj" }
```

FSAC itself still has exactly one active project context. A request for a file or
project outside it returns `context_mismatch`; switching a live FSAC to another
project with `restartLsp=false` returns `restart_required` without changing the
active context.

Concurrency limits:

- `FSLANGMCP_MAX_CONCURRENT_FCS=2`
- LSP tools are always serialized because FSAC owns one mutable workspace.

## Local dev (without global install)

If you're developing FsLangMCP itself or want to run an uninstalled build:

```json
{
  "mcpServers": {
    "fslangmcp": {
      "command": "dotnet",
      "args": [
        "run", "--project", "/path/to/FsLangMcp.fsproj",
        "--", "--project", "/absolute/path/to/App.fsproj"
      ]
    }
  }
}
```

## Runtime options

All clients can pass these args in the `args` array:

| Arg | Purpose |
|-----|---------|
| `--project <path>` / `-p <path>` | Pre-load a project on startup |
| `--fsac-command <cmd>` | Override the `fsautocomplete` executable |
| `--fsac-args "<args>"` | Pass extra args to FSAC |
| `--bootstrap-tools` | Install/downgrade the exact supported global FSAC/ProjInfo/Fantomas versions from the release's embedded manifest |
| `--version` | Print the packaged FsLangMCP version and exit |

Environment variable fallbacks and limits:

| Variable | Default | Purpose |
|----------|---------|---------|
| `FSAC_COMMAND` | `fsautocomplete` | FSAC executable |
| `FSAC_ARGS` | empty | Extra FSAC arguments |
| `FSA_PROJECT_PATH` | unset | Pre-load this project/workspace through the same pipeline as `--project` |
| `FSLANGMCP_MAX_CONCURRENT_FCS` | `2` | Maximum concurrent FCS tool calls |
| `FSLANGMCP_LSP_STARTUP_TIMEOUT_MS` | `60000` | `initialize` / `workspaceLoad` RPC timeout |
| `FSLANGMCP_LSP_REQUEST_TIMEOUT_MS` | `30000` | Live LSP request/notification timeout |
| `FSLANGMCP_PROJ_INFO_TIMEOUT_MS` | `120000` | ProjInfo child-process, evaluated-project, and readiness-probe timeout |
| `FSLANGMCP_BOOTSTRAP_TIMEOUT_MS` | `300000` | Per-command `--bootstrap-tools` timeout |
| `FSLANGMCP_PROCESS_OUTPUT_LIMIT_CHARS` | `4194304` | Maximum retained characters per child stdout/stderr stream; excess is still drained |

LSP concurrency is deliberately fixed at one. Increasing parallelism around a single mutable FSAC workspace can mix document versions or dispose an RPC during `set_project`; the bridge therefore serializes lifecycle, document sync, and invocation internally.
