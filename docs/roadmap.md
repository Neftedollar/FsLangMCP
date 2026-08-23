# Roadmap

A living document. **Not a delivery contract** — priorities shift with real usage signal.

## Current status (pre-1.0)

`v0.16.0` ships **35 MCP tools** across headline, semantic, LSP-proxy, and meta categories. The surface is still evolving toward a stable shape: v0.13.1 consolidated the former search/check aliases behind `find` and `check`, v0.15.0 made incomplete evidence explicit and tightened typed tool input to strict JSON, and v0.16.0 made payloads self-explaining — reconciled diagnostic counts, honest sweep/readiness reporting, typed SDK-pin errors, response-size budgets, and a field-impact planning mode for `find`. A 1.0 release will lock the contract.

## 1.0 exit criteria

These conditions need to be true before cutting `1.0.0`:

- **No open blocker-grade UX issues** across the repository's bounded UX/bug trackers.
- **Tool surface stable across 3 consecutive minor releases** with no renames or removals — a soak period proving the contract holds.
- **`///` doc-comments surface in the MCP JSON schema's `properties[x].description`.** Today they don't (see v0.8.6 CHANGELOG). Needs either an FsMcp.Server-side change or a local schema-generation pass that picks up XML doc.
- **VS.Threading bind race in subagent context fully closed.** v0.8.2 #121 fix held in most cases but residual reports surfaced across v0.8.4–v0.9.0 subagent sessions. Needs deeper `RuntimeFrameworkVersion` or binding-redirect investigation.
- **Performance baselines documented and met**: warm-cache `set_project` ≤ 2s on a 10-project solution; cold-cache `find(kind="field")` ≤ 5s on a 50k-LOC project. (Today both are unmeasured.)
- **Documentation in lockstep with the surface**: README, `CONTRIBUTING.md`, `docs/architecture.md`, and `AGENT_INTEGRATION.md` all reflect the shipped tool set.
- **At least one third-party adoption signal**: someone other than the maintainer running FsLangMcp in a non-toy project and reporting stable usage in a dedicated issue/discussion.

## Deferred items / planned work

Ordered roughly by impact, not by date:

- **Walker performance benchmark** — `FieldFormClassifier` was rewritten in v0.9.0 (#124) to use `ParsedInput.fold`. The acceptance criteria called the new walker "comparable or shorter" than the old recursion but didn't benchmark cold-cache cost on large projects. Add a benchmark; profile if regression appears.
- **Subagent-context VS.Threading race** — see 1.0 exit criteria. Likely needs a binding-redirect or pinned `RuntimeFrameworkVersion`, not just the existing top-level `PackageReference` pin.
- **Killer-story blog content** — "VS.Threading rescue across 9 broken subagent sessions" and "Dogfooding loop on FsLangMcp 0.5 → 0.9" — adoption work targeted at the multi-agent F# audience.
- **`fcs_make_internal_visible` Variant B** — auto-generate an `InternalsVisibleTo` attribute for the test project. Today only Variant A ships (drop the `private` keyword in-place).

## Will NOT do

Explicit non-goals — bring them up only if a strong adoption signal flips the call:

- **Compete with Ionide for editor use.** FsLangMcp is for agents. Human IDE workflows are well-served by Ionide / `fsautocomplete` directly; rebuilding that experience on top of MCP would dilute focus.
- **Replace `dotnet build` for ground-truth project validation.** FCS is faster, but `FSharpChecker` may serve from cached ASTs and miss cross-project staleness. For absolute certainty across project boundaries, `dotnet build` remains authoritative.
- **First-class Paket support.** `.fsproj` + NuGet `PackageReference` is the primary target. Paket users can almost certainly get FsLangMcp working but it isn't on the CI matrix.

## How priorities work

Triage order, highest first:

1. **Bug fixes** — anything where the documented contract is wrong on the wire.
2. **Security fixes** — vulnerable transitive packages, untrusted-input issues.
3. **Infrastructure / build** — keep CI green and `dotnet pack` working.
4. **Features** — new tools and response fields once the surface is stable.
5. **Polish** — error-message wording, tool descriptions, doc edits.
6. **Content** — blog posts, demos, conference talks.

Every reproducible UX behavior is filed directly as one bounded issue with version, request,
observed result, expected result, and acceptance criteria. New evidence for the exact same behavior
is added to that issue; unrelated observations are never batched into a numbered catch-all.
Positive/non-actionable notes stay in the originating report until they form a concrete request.
