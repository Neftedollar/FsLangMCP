# WORKFLOW: Infra Pipeline
**Version**: 0.1
**Status**: Draft

---

## Overview

Pipeline for infrastructure tasks: CI/CD, Docker, deploy scripts, cloud config. Skips TEST — infra is tested by deployment.

## Actors

| Actor | Role | Model |
|-------|------|-------|
| Orchestrator | Manages flow | opus |
| Software Architect | Architecture, cost analysis | opus |
| DevOps Automator | Implementation | sonnet |
| Security Engineer | Review (if secrets/network affected) | opus |

## Workflow Tree

### STEP 1: PLAN
**Gate**: `PLAN_APPROVED` | **Timeout**: 15 min

Architect determines: what to change, cost impact, risk assessment. If cost > $0 → CEO escalation.

### STEP 2: BUILD
**Gate**: `BUILD_DONE` | **Timeout**: 20 min

DevOps implements changes. Worktree for code, direct edit for infra-only files.

### STEP 3: VERIFY
**Gate**: `VERIFIED` | **Timeout**: 10 min

Security Engineer review (if secrets/network). Orchestrator self-review for simple changes (<=2 files, no secrets).

### STEP 4: SHIP
**Gate**: `SHIPPED` | **Timeout**: 15 min

Merge, deploy, verify deployment health. Rollback plan required.

**Retry policy**: 3 attempts → helper → 2 attempts → CEO escalation (5 total).

## State Transitions

```
[Todo] → [PLAN] → [BUILD] → [VERIFY] → [SHIP] → [Done]
[Any] → (5x fail) → [ABORT → Todo]
```
