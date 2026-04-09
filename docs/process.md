# Operational Process

> **Process v1**. Source of truth: AGE graph `fslangmcp-ops` + workflow specs.

## Source of truth

| What | Where |
|------|-------|
| Pipelines, Steps, Roles | AGE graph `fslangmcp-ops` (Pipeline→HAS_STEP→Step→PERFORMED_BY→Role) |
| Modules, code dependencies | AGE graph `fslangmcp-ops` (Module→DEPENDS_ON→Module, Module→BELONGS_TO→Repo) |
| Security findings, Code insights | AGE graph `fslangmcp-ops` (SecurityFinding, CodeInsight nodes) |
| Role capabilities (extended) | `docs/role-capabilities.md` |
| Workflow specs | `docs/workflows/REGISTRY.md` → individual WORKFLOW-*.md |
| Issues, dependencies | GitHub Project + graph (issue→DEPENDS_ON) |
| Coordination | O'Brien (active-work, bugs, suggestions) |

**Pipeline**: PLAN → BUILD → TEST → VERIFY → SHIP (5 steps, 5 pipelines: feature, bugfix, infra, content, spike).

## How agents use the graph

Graph `fslangmcp-ops` is both process definition and **project knowledge base**. Each pipeline step is enriched with graph context:

| Step | What to query from graph | Why |
|------|-------------------------|-----|
| **PLAN** | `issue DEPENDS_ON`, `Module` by affected files | Understand scope, dependencies, architecture |
| **BUILD** | `Module.key_classes`, `Module DEPENDS_ON`, `CodeInsight` by files | Developer knows key classes, debt, patterns |
| **VERIFY** | `SecurityFinding` by affected files | Reviewer sees known security issues in this area |
| **SHIP** | `Module BELONGS_TO Repo` | Determine which repo to merge/deploy |
| **Role selection** | `Role HELPS Role`, `Step PERFORMED_BY Role` | Who to assign, who to call as helper |

**After task completion** — update graph: new modules, security findings, code insights, issue status.

---

## Three entry points

| Mode | Launch | Description |
|------|--------|-------------|
| CEO Mode | `/orchestrator <task>` | CEO gives a specific task |
| Single Expert | `/<role> <question>` | Direct role call, no pipeline |
| Autonomous | `claude -p "/orchestrator ..." --max-turns 50` | Headless, from backlog |

---

## Models by role

| Tier | Model | Roles |
|------|-------|-------|
| Strategic | opus | PM, Architects, Security, Orchestrator |
| Execution | sonnet | Coder, Frontend, DevOps, Tech Writer, Marketing, Designer |
| Validation | opus | Code Reviewer, Reality Checker |
| Routine | haiku | Data gathering, formatting, lookups |

---

## CEO control points

CEO **does not participate** in operations. Needed only when:

| Situation | How to notify |
|-----------|---------------|
| Ambiguous classification | `needs-ceo` Issue |
| Public content | PR with `needs-ceo-review` |
| Breaking API change | `needs-ceo` Issue |
| Infra decisions with costs | `needs-ceo` Issue |
| 5+ failures (3 role + 2 helper) | `needs-ceo` Issue with dossier |

---

## Dynamic role selection

Orchestrator selects roles dynamically via `docs/role-capabilities.md` + graph:

1. **Signals**: labels, files, keywords, task domain
2. **Match**: signals → capability index + graph → Primary + Secondary roles
3. **Composition**: sequential (A → B), parallel (A + B), or composite prompt
4. **Fallback**: if no role fits → create ad-hoc role (see orchestrator.md → "Ad-Hoc Role Creation")

Details: `orchestrator.md` → "Step 2: Select Roles Dynamically".

---

## Helper mechanism on blockers

3 retry → helper → 2 retry with recommendation → CEO escalation.

**Helper selection**: use Secondary role from `docs/role-capabilities.md` for the same domain. If Secondary matches the failed role — take Primary from adjacent domain.

Helper MUST NOT be the same role that failed.

**CEO escalation**: create GitHub Issue with `needs-ceo` label. Orchestrator does not block — moves to next task.

---

## Operational rules

### Worktree isolation — required for code

All agents modifying code **must** work in a git worktree:

```bash
git worktree add ../<project>-wt-<issue> -b <branch-name> main
```

**Rules**:
- Each bugfix/feature creates its own worktree
- After merge — `git worktree remove` (cleanup)
- Orchestrator passes worktree path to BUILD/TEST agents, not main repo
- Main working directory — **read-only** for pipeline

**Exception**: Single Expert mode for read-only tasks (code review, analysis) can read main directly.

### RED CI — fix before new PR

CI RED on PR → fix **in the same PR** (don't create a new one). Max 3 attempts, then helper.

### O'Brien tagging

| Event | Tag |
|-------|-----|
| Code written | `code-done` |
| PR created | `pr-created` |
| Code Review OK | `review-approved` |
| **PR merged** | **`completed-work`** |
| Deployed | `deploy-verified` |

Do not tag `completed-work` before merge.

---

## Task-level locking

Optimistic lock via O'Brien to prevent two orchestrators from taking the same task:

```
1. o-brien.search(tags: ["active-work", "issue-NNN"]) → if found → SKIP
2. o-brien.store(content: "LOCK: issue #NNN", tags: ["active-work", "issue-NNN", "lock"])
3. Wait 2 sec
4. o-brien.search(tags: ["active-work", "issue-NNN"]) → if >1 records → race → delete own lock, SKIP
5. Otherwise → task locked, continue
```

On completion: `o-brien.update(tags: ["completed-work"])`.
On crash recovery: `active-work` older than 24h → `stale-work`, lock auto-released.

---

## Autonomous run

```bash
claude -p "/orchestrator Take next P0+P1 tasks from backlog. \
  Load process from graph fslangmcp-ops. Execute autonomously." \
  --allowedTools "Bash,Read,Edit,Write,Agent,Glob,Grep,Skill" \
  --max-turns 100
```

Morning CEO check:
```bash
gh issue list --label needs-ceo --repo Neftedollar/FsLangMCP
gh pr list --repo Neftedollar/FsLangMCP
```

---

## Known issues

### AGE MCP connection pool

Sequential query recommended. Parallel `cypher_query` calls may hit PostgreSQL connection limits.

**Workaround**: Orchestrator makes AGE queries **sequentially**, one `cypher_query` at a time.

### Limitations

1. Checkpoint loses retry context on crash
2. No persistent metrics (retries, turns, timing)
3. No automatic rollback
4. Context window limit for long pipelines
5. AGE connection pool limit (max 2 parallel queries)
