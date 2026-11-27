# Roadmap

A living document. **Not a delivery contract** — priorities shift with real usage signal.

## Current status (pre-1.0)

**Published: v0.20.0.** The server exposes 35 MCP tools across headline, semantic,
LSP-proxy, and meta categories. This release adds standalone script checking without
execution and compact diagnostic grouping. The existing `find`/`check` contract
continues to distinguish incomplete evidence from a clean or absent result.

**Post-release main:** [#328](https://github.com/Neftedollar/FsLangMCP/pull/328)
adds Hidden-severity counting regression coverage,
[#330](https://github.com/Neftedollar/FsLangMCP/pull/330) tests nullable OS-thread
telemetry, and [#331](https://github.com/Neftedollar/FsLangMCP/pull/331) documents and
wire-tests the existing coordinate contract without changing its 0-based semantics.
These merged changes are not a claim that another NuGet release has shipped.

The triage below is a snapshot of **23 open issues on 2026-11-12**, against main
`b6f2ac3f880d41d90da02df39fda7259c6f16826`. Closing every feature request is **not** a
prerequisite for 1.0. The aim is to stabilize the existing contract before expanding it.

## 1.0 exit criteria and evidence

| Gate | Current evidence | Still required |
| --- | --- | --- |
| No blocker-grade contract or UX defects | Recent fixes and regression tests improve specific behaviors; they do not certify all tools. | Reproduce and triage current reports by impact; explicitly resolve or document each release blocker. |
| Tool surface stable across three consecutive minor releases | The current stdio `tools/list` exposes 35 tools. | Compare names and input contracts across the release tags; an unchanged count alone does not demonstrate compatibility. |
| Input-property documentation reaches MCP JSON schema | A real stdio probe of the current-main-content build found **0 descriptions among 193 input properties**. Tool-level descriptions are present. | Expose property descriptions and assert them on the wire. Better argument errors in #268 are related but do not close this gate. |
| VS.Threading subagent binding reliability | Historical residual reports followed #121 across v0.8.4–v0.9.0. They do not establish a defect in the current release. | Recheck the original scenario on the supported runtime; record the actual result before prescribing binding changes or declaring closure. |
| Documented performance baselines | Targets remain warm `set_project` ≤ 2s for a 10-project solution and cold field `find` ≤ 5s for a 50k-LOC project; no qualifying measurement is recorded here. | Publish reproducible fixtures, SDK/platform, cache state, correct result/coverage checks, and measured results. Keep performance probes separate from timing-sensitive correctness assertions. |
| Documentation matches the shipped surface | Strict documentation build/deploy infrastructure exists. This roadmap distinguishes the published release from subsequent main changes. | Audit README, CONTRIBUTING, architecture, integration guidance, and detailed tool docs against actual schemas and behavior; pass the strict docs checks. |
| Independent stable non-toy adoption | #144 contains reports from third-party review workloads, including defects. This establishes use, not stable current-version adoption. | Obtain a dedicated current-version usage report with workload and remaining limitations, rather than using stars or an old report as proof. |

Green tests support the cases they assert; they are not proof that all these gates
have been met. For regressions, prefer tests that fail under a targeted behavioral
mutation, not merely tests that exercise a code path.

## Next bounded iteration

1. **#199: reliable test evidence.** Separate remaining wall-clock assertions,
   temporary-resource ownership, and testhost/fixture lifecycle stalls. Reproduce
   each failure class, use deterministic correctness checks where possible, and
   keep unproven hang causes explicit. Do not close the issue by increasing a
   timeout or fixing only one assertion.
2. **#268: actionable argument errors.** Fix the shared typed-input boundary and
   test actual wire responses for unknown/missing fields and bounded suggestions.
   The current `fcs_file_outline` request with `filePath` instead of `path` still
   returns only `Invalid tool arguments.`

Then choose #225 or #157 based on a bounded reproduction and user impact. Property
schema descriptions remain a separate required 1.0 task. Dependency PRs are reviewed
independently, especially compiler/analyzer changes that may alter these results.

## Open-issue classification

These are priorities and scope boundaries, not promises to implement everything.
Each link is the source of detailed acceptance criteria and subsequent evidence.

### Concrete next work

| Issue | Bounded outcome |
| --- | --- |
| [#199](https://github.com/Neftedollar/FsLangMCP/issues/199) — load-sensitive tests | Reliable assertions and attributable lifecycle diagnostics; remaining work is not declared fixed here. |
| [#268](https://github.com/Neftedollar/FsLangMCP/issues/268) — argument errors | Shared, bounded unknown/required-field errors, verified over stdio. |
| [#225](https://github.com/Neftedollar/FsLangMCP/issues/225) — external definitions | Explain an external C# symbol without an unnecessary full F# sweep; preserve local-definition behavior using a mixed-language fixture. |
| [#157](https://github.com/Neftedollar/FsLangMCP/issues/157) — outline projection | Compact projected member rows and per-type drill-down with real response bounds. Current wire schema has neither projection nor type filtering. |
| [#213](https://github.com/Neftedollar/FsLangMCP/issues/213) — dogfood CLI | Reuse existing stdio test-client infrastructure for the missing general-purpose harness, rather than adding another protocol implementation. |

### Design or proof required before implementation

| Issue | Boundary and evidence needed |
| --- | --- |
| [#156](https://github.com/Neftedollar/FsLangMCP/issues/156) — solution dead-code analysis | Current schema is project-local. Distinguish production, test-only, and unused candidates with explicit incomplete coverage; test-only never means safe to delete. |
| [#154](https://github.com/Neftedollar/FsLangMCP/issues/154) — CLR public surface | Add an opt-in emitted-surface view, preserving source-view semantics; compare a built fixture with `Assembly.GetExportedTypes()`. |
| [#153](https://github.com/Neftedollar/FsLangMCP/issues/153) — module inventory/collisions | Assembly-scoped semantic inventory with actual FS0248, compiled-name, namespace, and cross-assembly fixtures. |
| [#197](https://github.com/Neftedollar/FsLangMCP/issues/197) — inferred signatures/arity | Audit existing signature tools first; call-site signature help alone does not demonstrate compiled arity or FS0030 evidence. |
| [#195](https://github.com/Neftedollar/FsLangMCP/issues/195) — CLR surface/skeletons | Partially overlapping existing work: current tool description already advertises non-public members, CLR accessibility, abstractness, and constraints. Isolate remaining DIM/modifier/constructor gaps, assembly attributes, and skeleton generation rather than reimplementing all three together. |
| [#228](https://github.com/Neftedollar/FsLangMCP/issues/228) — CLR type rendering | Additive rendering, not replacement of F# signatures; verify void, byrefs, generics, nesting, and nullability limits. |
| [#231](https://github.com/Neftedollar/FsLangMCP/issues/231) — explicit SDK override | Repository SDK flexibility does not solve target-project overrides. Preserve strict defaults; isolate evaluation and include selected host/SDK in cache identity and result provenance. |
| [#155](https://github.com/Neftedollar/FsLangMCP/issues/155) — build validation profile | Optional bounded `dotnet build` engine with explicit restore/configuration/warning policy and process-tree cleanup. FCS is not build ground truth. |
| [#63](https://github.com/Neftedollar/FsLangMCP/issues/63) — workspace edits | Effectful expansion: preview, validation, and stale-file safety need a concrete design before any apply operation is called safe. |
| [#55](https://github.com/Neftedollar/FsLangMCP/issues/55) — project-file operations | Define disk/project changes and F# compile-order invariants; not a prerequisite for stability of existing tools. |

### Deferred, exploratory, or independently scheduled

| Issue | Reason and next decision |
| --- | --- |
| [#198](https://github.com/Neftedollar/FsLangMCP/issues/198) — repo-wide find | Multi-solution coverage, budgets, and cache behavior need architecture work before widening sweep scope. |
| [#158](https://github.com/Neftedollar/FsLangMCP/issues/158) — extraction analysis | Separate semantic capability: prove captured-state/call-graph usefulness and define conservative purity before adding it. |
| [#226](https://github.com/Neftedollar/FsLangMCP/issues/226) — source/decompilation | Resolve version identity, provenance, licensing, network/budget limits, and non-execution guarantees first. |
| [#208](https://github.com/Neftedollar/FsLangMCP/issues/208) — snippet execution | New execution/trust surface. v0.20.0 script checking does not execute snippets and does not fulfill this issue. |
| [#76](https://github.com/Neftedollar/FsLangMCP/issues/76) — DAP | Optional debugging subsystem; adapter design is not proof of implementation or portability. Not a core 1.0 gate. |
| [#73](https://github.com/Neftedollar/FsLangMCP/issues/73) — tree-sitter | A syntax-only fallback has a real use case, but tree-sitter has not been established as the right implementation. An evidence-backed decision not to adopt is valid. |
| [#271](https://github.com/Neftedollar/FsLangMCP/issues/271) — macos-latest | Restore the moving image only after the upstream fix and two independent unchanged hosted smoke runs. The supported pinned image is not itself a release blocker. |
| [#144](https://github.com/Neftedollar/FsLangMCP/issues/144) — growth | Reconcile stale checklist entries: GIF and docs infrastructure already exist. Social preview, outreach, and stable adoption evidence remain separate outcomes. |

## Other retained work

- **Walker benchmark:** measure the `ParsedInput.fold` classifier on realistic cold
  inputs instead of assuming that the rewrite is faster.
- **`fcs_make_internal_visible` Variant B:** an optional future `InternalsVisibleTo`
  workflow; today only the existing accessibility-edit variant ships.
- **Content:** demos, tutorials, and case studies follow reliable evidence; they do
  not substitute for correctness or adoption gates.

## Will NOT do

- **Compete with Ionide for human editor use.** This server is for agents.
- **Replace `dotnet build` as ground truth.** An optional build-backed profile may
  invoke it; FCS must not silently claim equivalent assurance.
- **First-class Paket support.** `.fsproj` and NuGet `PackageReference` remain the
  supported primary target; Paket is not on the CI matrix.

## How priorities work

Prioritize broken contracts and security, then reliable build/test evidence, then
bounded features, polish, and content. Every issue needs version, request, observed
result, expected result, and acceptance evidence. Add further evidence for the same
behavior to its existing issue; unrelated observations belong in separate bounded
issues. Update this snapshot as decisions and verified results change.
