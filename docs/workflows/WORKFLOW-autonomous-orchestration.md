# WORKFLOW: Autonomous Orchestration
**Version**: 1.0
**Status**: Template

---

## Overview

Meta-workflow for autonomous mode. Orchestrator picks tasks from the backlog and runs the appropriate pipeline for each. Runs headless via `claude -p`.

---

## Actors

| Actor | Role | Model |
|-------|------|-------|
| Orchestrator | Task selection, pipeline execution, reporting | opus |

---

## Workflow Tree

### PHASE 0: BOOTSTRAP
**Timeout**: 5 min

1. Read `CLAUDE.md`, `docs/process.md`
2. Load graph context (pipelines, modules, security findings)
3. Load O'Brien recent context: use o-brien MCP recall tool (recent: 20)
4. Check for `active-work` locks — skip locked tasks

### PHASE 1: SELECT TASK
**Gate**: `TASK_SELECTED`
**Timeout**: 5 min

1. Fetch open issues from GitHub Project:
   ```bash
   gh issue list --repo Neftedollar/FsLangMCP --state open --json number,title,labels,assignees
   ```
2. Filter: exclude `needs-ceo`, `blocked`, already-locked tasks
3. Prioritize: P0 → P1 → P2. Within same priority: oldest first
4. Acquire lock via O'Brien (see process.md "Task-level locking")
5. Classify pipeline type by labels: `feature` → feature, `bug` → bugfix, `infra` → infra, `content` → content, `research`/`spike` → spike

**Output**: `{ issue_number, pipeline_type, priority }`

### PHASE 2: EXECUTE PIPELINE
**Timeout**: varies by pipeline

Run the selected pipeline workflow:
- Feature: `WORKFLOW-feature-pipeline.md`
- Bugfix: `WORKFLOW-bugfix-pipeline.md`
- Infra: `WORKFLOW-infra-pipeline.md`
- Content: `WORKFLOW-content-pipeline.md`
- Spike: `WORKFLOW-spike-pipeline.md`

Follow all gates, retries, escalation rules from the specific workflow.

### PHASE 3: POST-TASK
**Timeout**: 5 min

1. Update O'Brien: `active-work` → `completed-work`
2. Update graph with new knowledge (modules, insights, findings)
3. Update GitHub Issue status
4. Evaluate remaining turn budget

### PHASE 4: LOOP OR EXIT
**Decision point**:

- If turn budget remaining > 30% → GO TO PHASE 1 (next task)
- If turn budget remaining <= 30% → EXIT with summary
- If no tasks available → EXIT with "backlog empty"

---

## State Transitions

```
[Start] → [BOOTSTRAP] → [SELECT TASK]
[SELECT TASK] → (task found) → [EXECUTE PIPELINE]
[SELECT TASK] → (no tasks) → [Exit: backlog empty]
[EXECUTE PIPELINE] → (done) → [POST-TASK]
[POST-TASK] → (budget > 30%) → [SELECT TASK]
[POST-TASK] → (budget <= 30%) → [Exit: budget low]
[Any] → (unrecoverable error) → [Exit: error]
```

## Launch Command

```bash
claude -p "/orchestrator Take next P0+P1 tasks from backlog. \
  Load process from graph fslangmcp-ops. Execute autonomously." \
  --allowedTools "Bash,Read,Edit,Write,Agent,Glob,Grep,Skill" \
  --max-turns 100
```

## Morning CEO Check

```bash
# What happened overnight?
gh issue list --label needs-ceo --repo Neftedollar/FsLangMCP
gh pr list --repo Neftedollar/FsLangMCP
```

## Safety Rails

- **Max tasks per run**: no hard limit, governed by turn budget
- **Escalation**: any 5x failure creates `needs-ceo` issue and moves on
- **No merge to main without PR**: even in autonomous mode
- **Crash recovery**: `active-work` older than 24h → `stale-work`, lock auto-released
