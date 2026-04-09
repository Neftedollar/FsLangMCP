# WORKFLOW: Content Pipeline
**Version**: 0.1
**Status**: Draft

---

## Overview

Pipeline for content: blog posts, docs, marketing materials. No SHIP — content is published via CEO approval (PR with `needs-ceo-review`).

## Actors

| Actor | Role | Model |
|-------|------|-------|
| Orchestrator | Manages flow | opus |
| Product Manager | Messaging, audience, positioning | opus |
| Content role (varies) | Content creation | sonnet |
| CEO | Final approval | human |

## Workflow Tree

### STEP 1: PLAN
**Gate**: `PLAN_APPROVED` | **Timeout**: 10 min

PM defines: audience, key message, CTA, format, target platform.

### STEP 2: BUILD
**Gate**: `BUILD_DONE` | **Timeout**: 30 min

Content role creates material. Commit to feature branch.

### STEP 3: VERIFY
**Gate**: `VERIFIED` | **Timeout**: CEO-dependent

CEO review: PR with label `needs-ceo-review`. Orchestrator creates PR and moves to next task.

**Retry policy**: 3 attempts → helper → 2 attempts → CEO escalation (5 total).

## State Transitions

```
[Todo] → [PLAN] → [BUILD] → [VERIFY (CEO)] → [Done]
[Any] → (5x fail) → [ABORT → Todo]
```
