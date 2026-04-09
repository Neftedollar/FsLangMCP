# WORKFLOW: Spike Pipeline
**Version**: 0.1
**Status**: Draft

---

## Overview

Research-only pipeline. One step — PLAN. Output = document, not code. For research, POC evaluations, architecture spikes.

## Actors

| Actor | Role | Model |
|-------|------|-------|
| Orchestrator | Launches and accepts result | opus |
| Researcher (varies) | Research by topic | opus |

## Workflow Tree

### STEP 1: PLAN (research)
**Gate**: `PLAN_APPROVED` | **Timeout**: 30 min

Researcher investigates. Output: document in `docs/spikes/` with:
- Question / hypothesis
- Findings
- Recommendation
- Next steps (issues to create)

**Retry policy**: 3 attempts → helper → 2 attempts → CEO escalation (5 total).

## State Transitions

```
[Todo] → [PLAN] → [Done]
[PLAN fail x5] → [ABORT → Todo]
```

## Notes

- Spike does NOT create code, only documents
- Spike MAY recommend creating feature/infra issues as follow-up
- Orchestrator does NOT create follow-up issues independently — passes recommendations to CEO
