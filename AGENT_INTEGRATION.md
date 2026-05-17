# Agent Integration Guide

This document describes a recommended workflow for AI coding agents (Claude Code, Cursor, GitHub Copilot CLI, Codex, etc.) that delegate F# work to subagents and use FsLangMCP as the semantic-query layer. It is opinionated — the patterns here came from real production multi-agent runs and have been refined through ~12 subagent sessions and ~700 tool calls against this server.

## Why this guide exists

A persistent challenge with multi-agent F# workflows is consistent tool discipline across subagents. Without explicit rules:

- Subagents fall back to text search (`rg` / `grep`) on `.fs` / `.fsi` files, missing partial application, shadowed bindings, aliased opens, comment/string noise.
- Subagents that DO use FsLangMCP rarely return structured feedback on what worked or hurt, so the server's UX evolution starves of real-world signal.
- Feature requests and behaviour bugs surface inside individual agent transcripts and disappear when the session ends.

This guide packages the integration patterns that solve these.

## CLAUDE.md / project-rules snippet

Drop this into your project's `CLAUDE.md` (Claude Code), your `.cursorrules` (Cursor), or equivalent agent-rules file. Adapt the wording to your tool's idioms.

```markdown
## F# work

- For semantic queries on `.fs` / `.fsi` / `.fsx` source — symbols, references,
  definitions, types, signatures, diagnostics, project structure — always use
  the `fslangmcp` MCP server. Never use `rg` / `grep` (text search misses
  partial application, shadowing, aliased opens, and produces noise from
  comments / strings).
- Call `mcp__fslangmcp__set_project` once per session, then use as needed:
  `project_health`, `fcs_project_outline`, `workspace_symbol`,
  `fcs_project_symbol_uses`, `textDocument_definition`,
  `fcs_type_at_position`, `workspace_diagnostics`.
- `rg` remains correct for non-F# files (`.fsproj`, `paket.dependencies`,
  `Directory.Packages.props`, CI YAML) and textual idiom counts.

### Boundary heuristic

Apply the tool that matches the data shape, not the calendar:

| Data shape                              | Tool                          |
|-----------------------------------------|-------------------------------|
| Semantic question on compiled F# source | fslangmcp                     |
| Textual scan of .fsproj XML / YAML / md | rg                            |
| NuGet third-party type enumeration      | `dotnet fsi` + System.Reflection |
| Unfiled draft `.fsi` sketches in specs/ | Direct Read (until parse_fsi_sketch lands) |
| Markdown design docs                    | Direct Read                   |

Three independent agent runs in this project confirmed: design-phase tasks
(research / spec / plan / data-model / analyze) have structurally low
fslangmcp yield because the source-of-truth is markdown, not F# source.
Implementation- and review-phase tasks have high yield.

## F# subagent briefs

Every subagent brief that touches F# code MUST include a "Tool discipline"
section telling the subagent to:

1. **Use `fslangmcp`** for all semantic F# queries. Spell out canonical
   entry points: `mcp__fslangmcp__set_project` once, then
   `workspace_diagnostics` after edits, `fcs_project_outline` for structure,
   `workspace_symbol` / `textDocument_references` for navigation,
   `fcs_type_at_position` for types.
2. **Use `rg` only for non-F# files** (`.fsproj`, `.md`, JSON, YAML, idiom
   counts).
3. **Deliver a 5–10 bullet end-of-run UX report on `fslangmcp`** — what
   worked well, what was slow / awkward, what felt missing, anything that
   looked like a bug. An honest "didn't need it for this task type" is a
   valid finding.

When a subagent returns with a concrete feature request, missing-tool
observation, or behaviour bug for `fslangmcp` in its UX report, **post it
as a follow-up comment on the open FsLangMCP tracking issue** within the
same turn it surfaced. Do not batch across sessions — the maintainer
loses context and we lose the feedback signal.
```

The exact wording is replaceable; the structure (Tool discipline + UX report + routing) is what matters.

## Subagent brief template

A minimal F# subagent brief skeleton. Embed your task-specific content where indicated; keep the Tool discipline and UX report bullets as a fixed footer.

```
## Task
<concrete task description: what to implement, what files, what tests>

## Repo conventions
<project-specific: warnings-as-errors, no Co-Authored-By, etc.>

## Tool discipline — non-negotiable

- For F# semantic queries (`.fs` / `.fsi`): use `fslangmcp` MCP. Call
  `mcp__fslangmcp__set_project` ONCE with the repo root, then use
  `workspace_diagnostics`, `fcs_project_outline`, `workspace_symbol`,
  `textDocument_references`, `fcs_type_at_position`,
  `textDocument_codeAction` as needed.
- `rg` is OK for non-F# files (.fsproj, .md, .json, .yml).
- Reflection (`dotnet fsi` + `System.Reflection`) is correct for
  third-party NuGet type enumeration; fslangmcp doesn't cover that yet.

## End-of-run deliverables

- The task output (code, doc, report — task-specific)
- A 5–10 bullet **fslangmcp UX report**: what helped, what was slow, what
  felt missing, anything that looked like a bug. Honest "didn't need it
  for this task type" is valid feedback.
```

## Feedback routing — the standing rule

When a subagent surfaces a feature request or bug for FsLangMCP, **post it as a comment on the project's open tracking issue immediately, in the same turn**. The catch-all tracking issue is currently:

> [Neftedollar/FsLangMCP#100 — UX feedback from multi-agent F# codebase](https://github.com/Neftedollar/FsLangMCP/issues/100)

If a future tracking issue replaces #100, that issue's URL goes in this guide and in the agent's CLAUDE.md.

**Why immediately:** by the next session, the orchestrator has lost the surrounding context that made the feedback meaningful. The maintainer also benefits from a continuous discussion thread instead of stale issue snapshots opened months apart.

**Why one tracking issue, not one issue per request:** maintainer thread fatigue is real. A single well-organized thread with grouped follow-up comments is easier to triage than 15 separate issues with overlapping themes.

## Memory-growth observation pattern

Long multi-agent sessions can trigger noticeable RSS growth on FsLangMCP processes (observed: +250% on one PID across 6 subagent runs in one session). To support the maintainer with telemetry, periodically sample RSS during long runs:

```bash
# In a background shell (e.g. via run-in-background flag):
while true; do
  ts=$(date +%H:%M:%S)
  line="[$ts]"
  for pid in $(pgrep -f $(which fslangmcp)); do
    rss=$(ps -o rss= -p $pid 2>/dev/null | tr -d ' ')
    [ -n "$rss" ] && line="$line PID=$pid $((rss/1024))MB"
  done
  echo "$line" >> /tmp/fslangmcp-mem.log
  sleep 60
done
```

Read the log file at session close, include the delta in the routing comment if growth is non-trivial. This is how the asymmetric-per-project growth pattern was discovered (one PID accumulated state, sibling PIDs stayed flat — suggests per-project cache that needs periodic compaction).

## Adapting to non-Claude agents

- **Cursor** — put the CLAUDE.md snippet in `.cursorrules`. The subagent-brief template is mostly Claude-specific (Cursor's agent model differs); the Tool discipline + UX report bullets transfer cleanly. The routing rule applies unchanged.
- **GitHub Copilot CLI** — `.github/copilot-instructions.md` is the closest equivalent to CLAUDE.md. The same snippet applies.
- **Codex / OpenAI** — `AGENTS.md` at repo root. Same content.
- **Generic** — anywhere the agent reads project-level rules from a discoverable file, the snippet drops in.

## Reference — what real subagent runs looked like

The patterns in this guide were not invented in a vacuum. The orchestration log lives at [Neftedollar/FsLangMCP#100](https://github.com/Neftedollar/FsLangMCP/issues/100) — the original issue body documents the first six subagent runs, and follow-up comments compound additional observations from subsequent sessions. Worth a read before adopting the patterns to see what kinds of feedback the workflow generates.

## What this guide is NOT

- It is **not** a tutorial on how to install or configure FsLangMCP — see the main `README.md` for that.
- It is **not** prescriptive about which agent tool you use (Claude, Cursor, Copilot CLI, Codex all work). It is prescriptive about the **discipline** every subagent should follow regardless of orchestrator.
- It is **not** an attempt to replace project-specific `CLAUDE.md`. The snippet is meant to live alongside other project rules.

## License

Same license as the rest of FsLangMCP — see [LICENSE](LICENSE).
