# Agent Integration Guide

This document describes a recommended workflow for AI coding agents (Claude Code, Cursor, GitHub Copilot CLI, Codex, etc.) that delegate F# work to subagents and use FsLangMCP as the semantic-query layer. It targets the 35-tool v0.17.0 surface and is opinionated — the patterns here came from real production multi-agent runs and have been refined through ~12 subagent sessions and ~700 tool calls against this server.

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
- Call `mcp__fslangmcp__set_project` once per session. Then for the two most
  common questions, reach for the consolidated entry points first:
  - **"Where is X used / defined?"** → `mcp__fslangmcp__find` (one
    multi-project symbol sweep that unions definitions, references,
    record-field set sites, and member-usage sites across every member
    project). The former aliases `workspace_symbol`,
    `fcs_find_symbol`, `fcs_project_symbol_uses`, `fcs_find_member_usages`,
    `fcs_record_field_audit`, `textDocument_references`, and
    `textDocument_definition` were removed in v0.11.0; use `find` instead.
    For exhaustive refactor counts, require both `coverage.complete=true` and
    `resolution.complete=true`; follow `nextCursor` while `truncated=true`.
  - **"Did my edit compile?"** → `mcp__fslangmcp__check` (one
    `clean` / `errors` / `unknown` verdict from a FRESH in-process
    type-check for the current FCS/check profile — no stale-`{}` false-clean).
    A `clean` verdict is not the final Release gate: configuration-specific
    diagnostics such as Release-only FS3511 require
    `dotnet build -c Release --warnaserror` before merge or release.
    The former aliases `workspace_diagnostics`, `fsharp_compile`,
    `fcs_check_file`, `fcs_parse_and_check_file`, and `fcs_validate_snippet`
    were removed in v0.11.0; use `check` instead.
  - Removed aliases are not registered. Update old prompts rather than falling
    back to a legacy tool name.
  - Other entry points as needed: `project_health`, `fcs_project_outline`,
    `fcs_symbol_at_word`.
- `rg` remains correct for non-F# files (`.fsproj`, `paket.dependencies`,
  `Directory.Packages.props`, CI YAML) and textual idiom counts.

### Boundary heuristic

Apply the tool that matches the data shape, not the calendar:

| Data shape                              | Tool                          |
|-----------------------------------------|-------------------------------|
| "Where is X used / defined?" (any symbol) | `find` — one multi-project sweep |
| "Did my edit compile?" (yes/no verdict)   | `check` — fresh in-process verdict |
| Semantic question on compiled F# source | fslangmcp                     |
| Textual scan of .fsproj XML / YAML / md | rg                            |
| NuGet third-party type enumeration      | `fcs_nuget_types` / `fcs_referenced_symbols` (shipped v0.7.0) |
| Unfiled F# snippet                      | `check` with `scope="snippet"` and `snippet` |
| Markdown design docs                    | Direct Read                   |

Three independent agent runs in this project confirmed: design-phase tasks
(research / spec / plan / data-model / analyze) have structurally low
fslangmcp yield because the source-of-truth is markdown, not F# source.
Implementation- and review-phase tasks have high yield.

## F# subagent briefs

Every subagent brief that touches F# code MUST include a "Tool discipline"
section telling the subagent to:

1. **Use `fslangmcp`** for all semantic F# queries. Spell out canonical
   entry points: `mcp__fslangmcp__set_project` once, then `check` for a
   compile verdict after edits, `find` for "where is X used / defined?"
   navigation, `fcs_project_outline` for structure, and `fcs_symbol_at_word`
   for types. Do not use the removed `workspace_symbol`,
   `textDocument_references`, or `workspace_diagnostics` aliases; route those
   requests through `find` / `check`.
2. **Use `rg` only for non-F# files** (`.fsproj`, `.md`, JSON, YAML, idiom
   counts).
3. **Deliver a 5–10 bullet end-of-run UX report on `fslangmcp`** — what
   worked well, what was slow / awkward, what felt missing, anything that
   looked like a bug. An honest "didn't need it for this task type" is a
   valid finding.

When a subagent returns with a concrete feature request, missing-tool
observation, or behaviour bug for `fslangmcp` in its UX report, **post it
as one dedicated bounded issue** within the same turn it surfaced. If an
open issue already tracks that exact behaviour, add the new reproduction
there instead. Do not batch unrelated observations — the maintainer loses
the behavior boundary and we lose the feedback signal.
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
  `mcp__fslangmcp__set_project` ONCE with the repo root, then use `find`
  for "where is X used / defined?", `check` for "did my edit compile?",
  and `fcs_project_outline` / `fcs_symbol_at_word` / `textDocument_codeAction`
  as needed. The removed `workspace_diagnostics`, `workspace_symbol`, and
  `textDocument_references` aliases are not available; use `find` / `check`.
- `rg` is OK for non-F# files (.fsproj, .md, .json, .yml).
- Before treating a `find` result as an exhaustive refactor plan, inspect both
  `coverage.complete` (all requested projects analyzed) and `resolution.complete`
  (the response contains the whole site set). Follow `nextCursor` while
  `truncated=true`; a final cursor page still omits earlier pages by itself.
- Treat `check(clean)` as evidence for the current FCS/check profile, not every
  build configuration. Before merge or release, run
  `dotnet build -c Release --warnaserror` to catch Release-only diagnostics such
  as FS3511.
- For NuGet third-party type enumeration: `fcs_nuget_types` (one package or
  assembly — `packageId` takes either spelling) and `fcs_referenced_symbols`
  (cross-assembly search), both shipped v0.7.0.
- For unresolved-symbol "what `open` do I add?" lookups:
  `fcs_suggest_open` (shipped v0.9.0).
- For record-field work of any kind: `find` with `kind="field"`. It reports FIVE
  distinct site kinds — `field-set-literal` (`{ Field = ... }`),
  `field-set-update` (`{ x with Field = ... }`), `field-set-mutation`
  (`x.Field <- ...`), `field-pattern` (`| { Field = b } ->`), and `field-read` —
  because a field-type change edits each of them differently.
- When you are CHANGING a record field's type, add `includeSiteTypes=true` to
  that call: every field-site row then carries `siteType`, the field's type as
  the current typecheck resolves it at that site, so you can plan each edit from
  the response instead of opening every file. It reports today's type only — it
  never predicts the post-edit type. The loop is:
  `find(kind="field", field="Name", includeSiteTypes=true)` → edit every site →
  `check(scope="project")` for the verdict.

## End-of-run deliverables

- The task output (code, doc, report — task-specific)
- A 5–10 bullet **fslangmcp UX report**: what helped, what was slow, what
  felt missing, anything that looked like a bug. Honest "didn't need it
  for this task type" is valid feedback.
```

## Feedback routing — the standing rule

When a subagent surfaces a reproducible feature request or bug for FsLangMCP,
**open one dedicated bounded issue immediately, in the same turn**. If the exact
behavior already has an open issue, comment there with the new version/repro instead.
Do not route new reports to a numbered catch-all.

**Why immediately:** by the next session, the orchestrator has lost the surrounding context that made the feedback meaningful. The maintainer also benefits from a continuous discussion thread instead of stale issue snapshots opened months apart.

**Why one behavior per issue:** a bounded tracker can carry a repro, acceptance criteria,
implementation, and closure state. A mixed thread cannot be closed honestly: one item gets fixed
while unrelated observations remain, and later agents keep appending to a stale destination.
Low-signal positive/non-actionable notes stay in the originating task report until they become a
concrete behavior; they do not need an issue merely to prove a report was written.

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

## Version drift in long-lived sessions

`fslangmcp` is a long-lived stdio process: your MCP client spawns it once and keeps it running for the life of the session, sometimes longer if the client pools connections across subagents. `dotnet tool update -g FsLangMcp` (or a fresh `dotnet tool install` after a repo checkout) changes what's on disk, not what's already running. A subagent spawned mid-session, or a fresh agent reusing an existing client connection, can silently talk to a stale server for the rest of its run.

Don't assume the on-disk version is the running version. Call `fslangmcp_version` (zero-arg, no project context required) at the start of any version-sensitive task — bug reproduction, regression testing, or confirming a fix landed — and compare it against the version you expect. If they disagree, the fix is a fresh MCP connection (restart the client's server process, or start a new client session), not another `dotnet tool update`.

## Adapting to non-Claude agents

- **Cursor** — put the CLAUDE.md snippet in `.cursorrules`. The subagent-brief template is mostly Claude-specific (Cursor's agent model differs); the Tool discipline + UX report bullets transfer cleanly. The routing rule applies unchanged.
- **GitHub Copilot CLI** — `.github/copilot-instructions.md` is the closest equivalent to CLAUDE.md. The same snippet applies.
- **Codex / OpenAI** — `AGENTS.md` at repo root. Same content.
- **Generic** — anywhere the agent reads project-level rules from a discoverable file, the snippet drops in.

## Reference — what real subagent runs looked like

The patterns in this guide were not invented in a vacuum. The archived historical orchestration
log lives at [Neftedollar/FsLangMCP#100](https://github.com/Neftedollar/FsLangMCP/issues/100) — it
documents the first multi-agent runs and remains useful as history, but it is not a destination for
new reports.

## What this guide is NOT

- It is **not** a tutorial on how to install or configure FsLangMCP — see the main `README.md` for that.
- It is **not** prescriptive about which agent tool you use (Claude, Cursor, Copilot CLI, Codex all work). It is prescriptive about the **discipline** every subagent should follow regardless of orchestrator.
- It is **not** an attempt to replace project-specific `CLAUDE.md`. The snippet is meant to live alongside other project rules.

## See also

- [`docs/troubleshooting.md`](docs/troubleshooting.md) — symptom-keyed
  guide for the common failure modes subagents hit (`not_ready` after
  `set_project`, VS.Threading bind race in subagent contexts, stale-check
  concerns after edits, and `find` zero-match cases).
- [`docs/tool-description-schema.md`](docs/tool-description-schema.md) —
  the 4-slot description schema used by every registered tool. Useful
  when authoring agent rules that route by tool description.

## License

Same license as the rest of FsLangMCP — see [LICENSE](LICENSE).
