# Workflow Registry

**Last updated**: 2026-04-09
**Source of truth**: AGE graph `fslangmcp-ops` + this file

---

## View 1: By Workflow

| Workflow | Spec file | Status | Trigger | Pipeline |
|----------|-----------|--------|---------|----------|
| Feature Pipeline | WORKFLOW-feature-pipeline.md | Draft | Issue with label `feature` | pipeline-feature |
| Bug Fix Pipeline | WORKFLOW-bugfix-pipeline.md | Draft | Issue with label `bug` | pipeline-bugfix |
| Autonomous Orchestration | WORKFLOW-autonomous-orchestration.md | Draft | `/orchestrator` or `claude -p` | meta |
| Infra Pipeline | WORKFLOW-infra-pipeline.md | Draft | Issue with label `infra` | pipeline-infra |
| Content Pipeline | WORKFLOW-content-pipeline.md | Draft | Issue with label `content` | pipeline-content |
| Spike Pipeline | WORKFLOW-spike-pipeline.md | Draft | Issue with label `research`/`spike` | pipeline-spike |

---

## View 2: By Component

| Component | Workflows it participates in |
|-----------|------------------------------|
| Orchestrator (`/orchestrator`) | Autonomous, Feature, Bugfix, Infra, Content, Spike |
| Product Manager (`/product-manager`) | Feature (PLAN), Content (PLAN) |
| UX Researcher (`/design-ux-researcher`) | Feature (PLAN, conditional: UI) |
| UI Designer (`/design-ui-designer`) | Feature (PLAN, conditional: UI) |
| Software Architect | Feature (PLAN), Infra (PLAN) |
| Senior Developer | Feature (BUILD, TEST), Bugfix (BUILD, TEST) |
| Evidence Collector (`/testing-evidence-collector`) | Feature (TEST, conditional: UI) |
| Performance Benchmarker (`/testing-performance-benchmarker`) | Feature (VERIFY, conditional) |
| Code Reviewer | Feature (VERIFY), Bugfix (VERIFY) |
| Security Engineer | Feature (VERIFY), Bugfix (VERIFY) |
| DevOps | Feature (BUILD, SHIP), Infra (BUILD, SHIP) |

---

## View 3: By User Journey

### CEO Journeys
| What CEO does | Underlying workflow(s) | Entry point |
|---------------|----------------------|-------------|
| Gives task to orchestrator | Autonomous → Feature/Bugfix/Infra | `/orchestrator <task>` |
| Calls role directly | Single Expert (no pipeline) | `/<role> <task>` |
| Reviews PR | Feature (SHIP), Bugfix (SHIP) | GitHub PR review |

---

## View 4: By State (Issue lifecycle)

| State | Entered by | Exited by | Workflows that trigger exit |
|-------|------------|-----------|----------------------------|
| Todo | CEO / PM creates issue | → In Progress | Autonomous (task selection) |
| In Progress | Orchestrator locks task | → Done, → Failed | Any pipeline |
| Done | Pipeline SHIP complete | (terminal) | — |
| Failed | Pipeline abort after retries | → Todo (re-queue) | CEO manual re-queue |

---

## Registry Maintenance Rules

- Update on every new workflow
- `Missing` = red flag, prioritize
- Deprecated workflows: mark `Deprecated`, don't delete
- All 4 views must be consistent
