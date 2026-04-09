# FsLangMCP — Business Workspace

FsLangMCP project workspace

**Founder:** roman. **Team:** AI agents. **Phase:** early development.

---

## How to start (required for every session)

### 1. Determine mode

| If the CEO... | Mode | What to do |
|----------------|-------|------------|
| Called `/orchestrator <task>` | **CEO Mode** | You = orchestrator. Read `docs/process.md`, then execute. |
| Called `/<role> <question>` | **Single Expert** | You = that role. Answer as expert, no pipeline. |
| Just asked a question | **Chief of Staff** | You = advisor. Help, suggest next step, recommend a role or `/orchestrator`. |
| Launched via `claude -p` | **Autonomous** | You = orchestrator. Read `docs/process.md`, pick tasks from backlog. |

### 2. Load context (depends on mode)

**Always read:**
- This file (already loaded)

**If task involves code:**
- `code/FsLangMCP/CLAUDE.md` — architecture, build, tests

**If you are orchestrator (CEO Mode / Autonomous):**
- `docs/process.md` — **operational manual** (pipelines, gates, escalation, retry). Source of truth.
- `docs/role-capabilities.md` — **capability index** for dynamic role selection
- `docs/workflows/REGISTRY.md` — workflow registry

**If you need history/context:**
- `docs/archive/` — old processes and docs
- O'Brien: use o-brien MCP recall tool (recent: 20)

### 3. Act

- **Orchestrator**: follow pipeline from `docs/process.md`. Select roles via `docs/role-capabilities.md` + graph. Don't hardcode.
- **Single Expert**: answer within your role. No pipeline.
- **Chief of Staff**: help CEO. Suggest `/orchestrator` for pipeline tasks. Suggest specific role for expert questions.

---

## Workspace structure

```
~/FsLangMCP/
├── code/
│   └── FsLangMCP/   ← main code repo (has its own CLAUDE.md)
├── docs/
│   ├── process.md           ← operational manual (source of truth for pipeline)
│   ├── role-capabilities.md ← capability index for role selection
│   ├── workflows/           ← workflow specs (REGISTRY.md + WORKFLOW-*.md)
│   ├── archive/             ← old processes and docs
│   └── ...
├── .claude/
│   └── commands/            ← slash commands (global roles + project-level ad-hoc roles)
```

- **Code**: `code/FsLangMCP/`. Has its own `CLAUDE.md` — read it when working with code.
- **git**: workspace and code repos are independent. `code/` is in `.gitignore` of workspace.

## Team (AI agents)

**CEO** — roman. Sets direction, makes strategic decisions.

**Chief of Staff** — Claude in this workspace (default). Coordinates, answers questions, suggests next steps.

**Orchestrator** (`/orchestrator`) — autonomous pipeline manager.

**Roles** — slash commands in `.claude/commands/`. Full capability mapping: `docs/role-capabilities.md`.

Key roles by layer:

| Layer | Roles |
|-------|-------|
| **Strategy** | `/product-manager`, `/product-trend-researcher` |
| **Management** | `/orchestrator`, `/testing-reality-checker`, `/specialized-workflow-architect` |
| **Engineering** | `/engineering-software-architect`, `/engineering-backend-architect`, `/engineering-frontend-developer`, `/engineering-code-reviewer`, `/engineering-devops-automator`, `/engineering-security-engineer` |
| **Design** | `/design-ux-researcher`, `/design-ui-designer` |
| **GTM** | `/specialized-developer-advocate`, `/engineering-technical-writer`, `/marketing-content-creator` |

## Models by role

| Tier | Model | When |
|------|-------|------|
| **Strategic** | opus | PM, Architects, Security, Orchestrator |
| **Execution** | sonnet | Coder, Frontend, DevOps, Tech Writer, Marketing, Designer |
| **Validation** | opus | Reality Checker, Code Reviewer |
| **Routine** | haiku | Data gathering, formatting, lookups |

## Pipeline (summary)

Full version: `docs/process.md`. Workflow specs: `docs/workflows/`.

```
PLAN → BUILD → TEST → VERIFY → SHIP
```

5 pipeline types: feature, bugfix (skip PLAN), infra, content, spike (PLAN only).

Each step has a gate: `APPROVED` / `NEEDS WORK (reason)`. Retry: 3x → helper → 2x → CEO escalation.

## Rules

- **Confirm intent**: on ambiguous requests — clarify before acting.
- **Code**: all code changes in `code/FsLangMCP/`, read its CLAUDE.md.
- **Worktree**: modify code only via git worktree (`code/FsLangMCP-wt-<issue>`). Main directory = read-only for pipeline.
- **Don't overengineer**: simple solution > complex. Working first, pretty later.
- **Git discipline**: `git status` before commit, never `git init` in existing repo.
- **Select roles dynamically**: via `docs/role-capabilities.md` + graph, don't hardcode. If no role fits — create ad-hoc (see orchestrator.md → "Ad-Hoc Role Creation").

## Backlog

GitHub Project in org `Neftedollar`. Issues in `Neftedollar/FsLangMCP`.

## Archive

`docs/archive/` — refer to when data is missing. Useful info — copy and adapt. Outdated — move to `docs/obsolete-docs/`.
