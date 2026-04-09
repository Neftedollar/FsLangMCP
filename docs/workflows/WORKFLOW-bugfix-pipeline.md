# WORKFLOW: Bugfix Pipeline
**Version**: 1.0
**Status**: Template

---

## Overview

Pipeline for bug fixes. Skips PLAN — goes straight to BUILD. Faster cycle for known issues with clear reproduction steps.

---

## Actors

| Actor | Role | Model |
|-------|------|-------|
| Orchestrator | Manages flow, spawns sub-agents | opus |
| Senior Developer | Fix implementation + tests | sonnet |
| Frontend Developer | UI fix (conditional) | sonnet |
| Evidence Collector | Browser QA (conditional: UI bugs) | opus |
| Performance Benchmarker | Perf review (conditional: perf bugs) | opus |
| Code Reviewer | Quality gate | opus |
| Security Engineer | Security gate (parallel with Code Reviewer) | opus |

---

## Workflow Tree

### STEP 1: BUILD
**Actor**: Senior Developer + Frontend Developer (conditional)
**Gate**: `BUILD_DONE`
**Timeout**: 20 min

**Pre-action**: Create worktree with bugfix branch.

**Action**:
1. Reproduce the bug (write a failing test first)
2. Implement fix
3. Verify test passes
4. Check for regressions in related code

**Output on SUCCESS**: `{ files_changed, test_added, regression_check: "OK", status: "BUILD_DONE" }` → GO TO STEP 2

**Output on FAILURE**:
- `FAILURE(cannot_reproduce)` → request more info from issue, retry
- `FAILURE(fix_breaks_other)` → retry with broader scope
- `FAILURE(root_cause_unclear)` → escalate: convert to spike

**Retry policy**: 3 attempts → helper (Secondary role from capability index) → 2 attempts with helper recommendation → CEO escalation (5 total).

---

### STEP 2: TEST
**Actor**: Senior Developer + Evidence Collector (conditional: UI bugs)
**Gate**: `TESTS_PASSED`
**Timeout**: 15 min

**Action (Senior Developer)**:
1. Full test suite (unit + integration)
2. All existing tests must pass
3. Regression test for the fixed bug must pass

**Action (Evidence Collector — when bug was in UI)**:

Triggered when fix touched UI files or issue has label `ui-bug`.

1. Open the running app with Playwright MCP
2. Reproduce original bug scenario — confirm it's fixed
3. Screenshot before/after on affected viewports
4. Check adjacent UI for regressions

**Output on SUCCESS**: `{ test_results, browser_evidence?: [...], status: "TESTS_PASSED" }` → GO TO STEP 3

**Output on FAILURE**:
- `FAILURE(test_regression)` → return to STEP 1
- `FAILURE(bug_not_fixed)` → return to STEP 1

---

### STEP 3: VERIFY
**Actor**: Code Reviewer + Security Engineer (parallel) + Performance Benchmarker (conditional)
**Gate**: `VERIFIED`
**Timeout**: 15 min per reviewer

All run **in parallel** (read code, don't write).

**Performance Benchmarker** — triggered when bug was performance-related or fix touches hot paths.

**Output on SUCCESS**: `{ code_review: "APPROVED", security_review: "APPROVED", perf_review?: "APPROVED", status: "VERIFIED" }` → GO TO STEP 4

**Output on FAILURE**:
- `NEEDS_WORK(code_quality)` → return to STEP 1
- `NEEDS_WORK(security_issue)` → return to STEP 1, PRIORITY: security fix
- `NEEDS_WORK(perf_issue)` → return to STEP 1

**Merge rule**: Code Reviewer + Security Engineer MUST return APPROVED.

---

### STEP 4: SHIP
**Actor**: Orchestrator + DevOps (if deploy needed)
**Gate**: `SHIPPED`
**Timeout**: 10 min

**Action**:
1. Merge bugfix branch → main
2. Push to origin
3. Deploy (if hotfix)
4. Update GitHub Issue: closed with fix reference
5. Tag O'Brien: `completed-work`

---

## State Transitions

```
[Todo] → (Orchestrator selects) → [BUILD]
[BUILD] → (BUILD_DONE) → [TEST]
[TEST] → (TESTS_PASSED) → [VERIFY]
[VERIFY] → (VERIFIED) → [SHIP]
[VERIFY] → (NEEDS_WORK) → [BUILD]
[SHIP] → (SHIPPED) → [Done]
[Any] → (ABORT) → [Todo]
```

## ABORT_CLEANUP

**Triggered by**: 3 retries exhausted + helper failed

1. Preserve bugfix branch (don't delete)
2. Update GitHub Issue with abort reason
3. Issue status → Todo (re-queue)
4. O'Brien: `active-work` → `stale-work`
