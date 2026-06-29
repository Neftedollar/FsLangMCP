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

After wiring, call `set_project` once at the start of each session to initialize the workspace context. Subsequent calls to `find`, `check`, and other tools use the loaded project automatically.

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

Call `set_project` as the first tool call in each agent session. Add the tool-discipline rule to `.cursorrules` so Cursor's agent knows to use `find` and `check` instead of grep — the snippet is in [`AGENT_INTEGRATION.md`](../AGENT_INTEGRATION.md).

## Codex (OpenAI)

Codex CLI reads MCP server configuration from `~/.codex/config.toml`. The `mcp_servers` stanza format in Codex's current release is:

```toml
[[mcp_servers]]
name    = "fslangmcp"
command = "fslangmcp"
args    = []
```

> Codex's config schema is evolving — see [Codex's MCP documentation](https://github.com/openai/codex) for the current exact key names if the above doesn't match your installed version.

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

Regardless of client, the first tool call in every agent session must be `set_project`:

```json
set_project { "projectPath": "/absolute/path/to/App.sln" }
```

The LSP-proxy tools (`textDocument_*`, `fsharp_signature_data`) require `set_project` to have completed and `readiness.lsp` to be `true` before they'll respond with data.

## Parallel agent usage

When multiple agents target the same FsLangMCP instance but different projects, pass `projectPath` explicitly on every FCS tool call. FCS caches project-wide results per resolved `.fsproj`, so agents targeting different projects share no stale caches:

```json
{ "path": "/abs/path/File.fs", "projectPath": "/abs/path/App.fsproj" }
```

Concurrency limits (env-overridable):

- `FSLANGMCP_MAX_CONCURRENT_FCS=2`
- `FSLANGMCP_MAX_CONCURRENT_LSP=1` (LSP tools serialize to protect FSAC workspace state)

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
| `--bootstrap-tools` | Install/update `fsautocomplete` + `ionide.projinfo.tool` |

Environment variable fallbacks: `FSAC_COMMAND`, `FSAC_ARGS`, `FSA_PROJECT_PATH`.
