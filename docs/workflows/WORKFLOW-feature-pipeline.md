# WORKFLOW: Feature Pipeline
**Version**: 1.0
**Status**: Template

---

## Overview

Full pipeline for new features: from issue to merge+deploy. 5 steps, each with a gate. Orchestrator manages flow, sub-agents execute work.

---

## Actors

| Actor | Role | Model |
|-------|------|-------|
| Orchestrator | Manages flow, spawns sub-agents | opus |
| Product Manager | Scope, AC | opus |
| UX Researcher | User flows, interaction patterns (conditional: UI) | opus |
| UI Designer | Layout, components, visual spec (conditional: UI) | sonnet |
| Software Architect | Design, ADR, trade-offs | opus |
| Senior Developer | Implementation + unit tests | sonnet |
| Frontend Developer | UI implementation (conditional) | sonnet |
| DevOps Automator | Infra changes + deploy | sonnet |
| Evidence Collector | Browser QA, visual evidence (conditional: UI) | opus |
| Performance Benchmarker | Performance review, bottlenecks (conditional) | opus |
| Code Reviewer | Quality gate | opus |
| Security Engineer | Security gate (parallel with Code Reviewer) | opus |

---

## Workflow Tree

### STEP 1: PLAN
**Actor**: Product Manager → UX Researcher + UI Designer (conditional: UI) → Software Architect (sequential)
**Gate**: `PLAN_APPROVED`
**Timeout**: 15 min per sub-role

**Action (Product Manager)**:
1. Define scope, acceptance criteria, user stories

**Action (UX Researcher — when task involves UI)**:

Triggered by keywords: `dashboard`, `ui`, `page`, `component`, `form`, `layout`, `landing`, `website`, `onboarding`, `flow` or file patterns: `*.html`, `*.css`, `*.tsx`, `*.jsx`, `*.vue`, `*.blade.php`.

1. Analyze user flows and interaction patterns for the feature
2. Identify key screens, states (empty, loading, error, filled)
3. Define navigation paths and user journey
4. Output: user flow description, screen list, interaction notes

**Action (UI Designer — when UX Researcher participated)**:

1. Define layout structure, component hierarchy
2. Specify visual elements: spacing, typography scale, color usage
3. Describe responsive behavior (desktop → tablet → mobile)
4. Output: component spec with states, layout description, responsive notes

**Action (Software Architect)**:
1. Design technical solution, ADR if needed
2. When UX/UI participated — map UI spec to components/files, validate feasibility

**Output on SUCCESS**: `{ scope, acceptance_criteria, design, ux_flows?, ui_spec?, files_to_change, status: "PLAN_APPROVED" }` → GO TO STEP 2

**Output on FAILURE**:
- `NEEDS_WORK(scope_unclear)` → retry with clarification
- `NEEDS_WORK(design_conflict)` → retry with specific conflict
- `NEEDS_WORK(ux_ui_infeasible)` → Architect + Designer re-negotiate
- `NEEDS_WORK(infeasible)` → ESCALATE to CEO

**Retry policy**: 3 attempts → helper (Secondary role from capability index) → 2 attempts with helper recommendation → CEO escalation (5 total). See process.md "Helper mechanism on blockers".

---

### STEP 2: BUILD
**Actor**: Senior Developer + Frontend Developer (conditional) + DevOps (conditional)
**Gate**: `BUILD_DONE`
**Timeout**: 30 min

**Pre-action**: Create worktree with feature branch.

**Action**:
1. Developer implements backend + unit tests
2. Frontend Developer implements UI from UI spec (if UI feature) — parallel if no data dependency
3. DevOps makes infra changes (if needed) — sequential after backend

**Output on SUCCESS**: `{ files_changed, tests_added, build_result: "OK", status: "BUILD_DONE" }` → GO TO STEP 3

**Output on FAILURE**:
- `FAILURE(build_error)` → retry: fix compilation
- `FAILURE(test_error)` → retry: fix failing tests
- `FAILURE(design_gap)` → return to STEP 1

---

### STEP 3: TEST
**Actor**: Senior Developer + Evidence Collector (conditional: UI changes)
**Gate**: `TESTS_PASSED`
**Timeout**: 20 min

**Action (Senior Developer)**:
1. Full test suite (unit + integration)
2. All existing tests must pass
3. New/changed code must have tests

**Action (Evidence Collector — when UI files changed)**:

Triggered when BUILD touched UI files (`*.html`, `*.css`, `*.tsx`, `*.jsx`, `*.vue`, `*.blade.php`, `templates/`).

1. Open the running app with Playwright MCP (`browser_navigate`)
2. Take screenshots of changed pages across viewports (desktop, tablet, mobile)
3. Walk key user journeys: click nav, fill forms, trigger interactions
4. Capture before/after screenshots for interactive elements
5. Report visual issues, broken layouts, non-functional interactions

**Output on SUCCESS**: `{ test_results, browser_evidence: [...screenshots], status: "TESTS_PASSED" }` → GO TO STEP 4

**Output on FAILURE**:
- `FAILURE(test_regression)` → return to STEP 2
- `FAILURE(browser_qa_fail, evidence: [...screenshots])` → return to STEP 2

---

### STEP 4: VERIFY
**Actor**: Code Reviewer + Security Engineer + Performance Benchmarker (all parallel)
**Gate**: `VERIFIED`
**Timeout**: 15 min per reviewer

All run **in parallel** (read code, don't write).

**Action (Code Reviewer)**: quality patterns, maintainability, test coverage.

**Action (Security Engineer)**: OWASP, auth, injection, data exposure.

**Action (Performance Benchmarker — conditional)**:

Triggered when BUILD changed backend code (routes, queries, services) or frontend code (components, bundles).

1. Review for N+1 queries, unoptimized loops, missing DB indexes
2. Check memory allocation patterns, connection/resource leaks
3. Frontend: bundle size impact, unnecessary re-renders, missing lazy loading
4. Identify O(n^2)+ algorithms on potentially large datasets
5. Flag missing pagination, unbounded queries, missing caching

**Output on SUCCESS**: `{ code_review: "APPROVED", security_review: "APPROVED", perf_review?: "APPROVED", status: "VERIFIED" }` → GO TO STEP 5

**Output on FAILURE**:
- `NEEDS_WORK(code_quality)` → return to STEP 2
- `NEEDS_WORK(security_issue)` → return to STEP 2, PRIORITY: security fix
- `NEEDS_WORK(perf_issue, details)` → return to STEP 2

**Merge rule**: Code Reviewer + Security Engineer MUST return APPROVED. Performance Benchmarker issues are advisory unless critical (unbounded queries, memory leaks).

---

### STEP 5: SHIP
**Actor**: Orchestrator + DevOps
**Gate**: `SHIPPED`
**Timeout**: 10 min

**Action**:
1. Merge feature branch → main
2. Push to origin
3. Deploy
4. Update GitHub Project: issue → Done
5. Tag O'Brien: `completed-work`

---

## State Transitions

```
[Todo] → (Orchestrator selects) → [PLAN]
[PLAN] → (PLAN_APPROVED) → [BUILD]
[BUILD] → (BUILD_DONE) → [TEST]
[TEST] → (TESTS_PASSED) → [VERIFY]
[VERIFY] → (VERIFIED) → [SHIP]
[VERIFY] → (NEEDS_WORK) → [BUILD]
[SHIP] → (SHIPPED) → [Done]
[Any] → (ABORT) → [Todo]
```

## ABORT_CLEANUP

**Triggered by**: 3 retries exhausted + helper failed

1. Preserve feature branch (don't delete)
2. Update GitHub Issue with abort reason
3. Issue status → Todo (re-queue)
4. O'Brien: `active-work` → `stale-work`
