# Changelog

All notable changes to **FsLangMCP** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
(pre-1.0: minor bumps may include breaking surface changes; see release notes).

## [Unreleased]

### Changed

- **Dropped the redundant `file` from nested `range` objects in site-list responses** — `find`, `fcs_project_symbol_uses`, `fcs_find_member_usages`, `fcs_record_field_audit`, and diagnostics each already carry a top-level `file`, so `range.file` duplicated it (a hot `find` = 75 sites × one redundant absolute path each). Added `rangeToJsonNoFile` and applied it **only** where the enclosing object already has `file`. **Standalone ranges that are the sole carrier of the path — declaration locations (`declarationLocation`, `declarationRange`) and the symbol-at-position map — keep `file` unchanged.** Response-shape trim only (consumers read the parent `file`); ships in 0.12.0. (#139)

## [0.11.0] - 2026-06-28

### Removed

- **BREAKING — the agent-facing MCP tool surface shrank from 36 → 22 tools: 14 thin single-tool aliases over the consolidated `find` / `check` were removed (#136).** Since 0.10.0 these were already steered toward `find` / `check` via `Prefer …` description hints; #136 completes the consolidation by dropping the registrations. The backend `FcsBridge` / `FsAutoCompleteBridge` methods (`FindSymbol`, `References`, `WorkspaceSymbol`, `CompileProject`, `ParseAndCheckFile`, `ValidateSnippet`, etc.) are unchanged — only the agent-facing tool registrations were removed. Removed tools, grouped:
  - **find cluster (7):** `textDocument_definition`, `textDocument_references`, `workspace_symbol`, `fcs_find_symbol`, `fcs_project_symbol_uses`, `fcs_find_member_usages`, `fcs_record_field_audit`
  - **check cluster (5):** `workspace_diagnostics`, `fsharp_compile`, `fcs_parse_and_check_file`, `fcs_check_file`, `fcs_validate_snippet`
  - **low-level position/symbol (2):** `fcs_file_symbols`, `fcs_type_at_position`

  Every capability remains reachable: cross-project **and** single-project symbol / reference / record-field-set / member-usage search via `find` (with `kind` + `scope` narrowing); type-check verdicts for file / project / workspace / inline snippet via `check`; raw per-file symbol extraction via `fcs_file_outline`; and position- or unsaved-buffer symbol / type lookups via `fcs_symbol_at_word`.

### Changed

- **`[FCS in-process]` / `[FSAC]` / `[meta]` description prefixes were stripped from all 22 remaining tool descriptions — each now leads with its action verb.** The implementation-layer tag leaked an internal backend distinction into the most valuable routing position (the first token the agent reads) without helping the call/skip decision. The `ToolDescriptionSchemaTests` (`dotnet test` gate) and `scripts/audit-tool-descriptions.py` schema rule were inverted accordingly: from "every description must start with a recognized tag" to "no description may start with a `[`-bracket tag", and the overlap-pair set was pruned to the three pairs whose members both still exist (`fcs_referenced_symbols`↔`fcs_nuget_types`, `fcs_nuget_types`↔`fcs_nuget_members`, `fcs_signature_help`↔`fsharp_signature_data`). `docs/tool-description-schema.md` documents the new rule.
- **`find`'s description was rewritten to advertise member call-site search and to drop now-stale references.** A tool-selection A/B (120 agents — the pre-consolidation 34-tool surface vs this 22-tool surface) caught a single regression from the removals: with the dedicated `fcs_find_member_usages` gone, agents fell back to `rg` for "find call sites of a `.Member()` on a type" in 5/5 trials instead of reaching for `find` — its old text under-sold the member-usage intent and still named the deleted `fcs_find_symbol` / `fcs_record_field_audit`. The rewrite leads with the four intents (definitions, references, record-field set sites, member call-sites on a type `x.Foo`) and contrasts against textual `rg`. A re-test dropped member-usage fallback from 100% → 0% (36/36 trials chose `find`), no regression on the other find tasks.

### Notes

- **Accepted limitations of the consolidated path (carried over from the 0.10.0 `find` / `check` design):** `find` / `check` do not accept `text` (unsaved buffer content) or raw `projectOptions` / `workspacePath` overrides — Write the file to disk first, then call `find` / `check`. Position-based and unsaved-buffer symbol / type lookups remain available through `fcs_symbol_at_word`.

## [0.10.1] - 2026-06-28

### Fixed

- **`find`'s multi-project sweep now caches each project's symbol-use enumeration, so a warm `find` is dramatically faster (#131).** FCS keeps `ParseAndCheckProject` warm via its project cache, but `GetAllUsesOfAllSymbols()` is NOT memoized — it re-walks every recorded symbol use (~16–18k per project on this repo) on EVERY `find`, so a warm `find` cost the same as a cold one (the `projectCacheSize` bump from 0.10.0 could not help: the cost was use-enumeration, not the type-check). The sweep now memoizes `(all-uses, diagnostics)` per project, keyed by the resolved-options cache key plus a source-file write-time stamp. Measured over stdio on this 2-project solution (`find("FindArgs")`, 69 sites): **cold `sweepElapsedMs` 11307 → warm 82/78** (≈ 138× faster, warm sub-100 ms). **Correctness is preserved unconditionally:** the stamp changes the instant any swept source file's on-disk mtime moves, so an edit is a cache MISS that runs the identical original `ParseAndCheckProject` + `GetAllUsesOfAllSymbols` path — a cached `find` is never staler than an uncached one — and `set_project`'s `ClearCaches()` drops the memo on a project switch. The cache is query-independent (it holds the raw enumeration), so different queries on an unchanged project all reuse it. +2 tests (warm-reuse + on-disk-edit invalidation).
- **The `find` sweep cache key now also includes the referenced-assembly mtimes, so a dependency rebuild invalidates dependent projects — no stale cross-project results (Codex P1 review of #134).** The #131 cache key stamped only each project's OWN `SourceFiles` mtimes. For a project A that references project B (P2P) or any other assembly, A's key therefore moved only when A's own source changed: rebuilding B (its output DLL changes) while A's source was untouched left A's key unchanged, so the sweep served A its **stale** cached `(all-uses, diagnostics)` even though a fresh `ParseAndCheckProject` would see B's new metadata — workspace `find` kept reporting stale cross-project usages until A's file was edited or `ClearCaches()` ran. The key now folds in a referenced-assembly write-time stamp (the `-r:`/`--reference:` targets in `OtherOptions`, each existing file's `LastWriteTimeUtc.Ticks`, missing files mapped to `-1`, never throwing): when B's output (the consumer's `obj/.../ref/B.dll` reference assembly) is rebuilt, its mtime moves → the consumer's key changes → cache MISS → fresh sweep. Every reference is stamped (framework/NuGet refs included) — correctness-first, since a path-based "immutable ref" skip risks misclassifying a mutable ref, and the extra stats are OS-metadata-cached and sub-millisecond, so the warm-cache win still holds for the no-change case. +1 cross-project invalidation test (the existing #131 test only edited a file in the SAME project, which is why the P1 slipped through).
- **The `find` sweep cache key now also includes the source-file mtimes of directly and transitively referenced F# projects, so a dependency SOURCE edit (without a rebuild) also invalidates dependent projects (Codex P2 review of #134).** P1 closed the DLL-rebuild staleness gap; P2 closes the complementary scenario: a developer edits a dependency's `.fs` file on disk without immediately rebuilding its output assembly. FCS's `ParseAndCheckProject` for a consumer reads referenced F# project sources directly via `ReferencedProjects[FSharpReference].SourceFiles` — so a fresh consumer check WOULD see the edited source, but the P1 DLL mtime hasn't moved → the P1 stamp alone would leave the consumer serving stale results. The key now additionally folds in a `referencedProjectSourcesStamp`: walks `options.ReferencedProjects`, and for each `FSharpReferencedProject.FSharpReference` case (the F# P2P case, carrying the referenced project's `FSharpProjectOptions`) stamps its `SourceFiles` mtimes with the same try/with-resilient approach. The walk recurses transitively (FCS's `ReferencedProjects` is NOT pre-flattened — only direct references appear at each level) using a `HashSet<string>` visited-set keyed on `ProjectFileName` to avoid re-stamping shared deps and guard against cycles. `PEReference` / `ILModuleReference` cases carry no `FSharpProjectOptions` and fall through, covered by the P1 DLL stamp. +1 proof-by-breaking test: the test bumps only Domain.fs mtime (no DLL touched), asserts the consumer cache count grows by MORE than 1 (both Stubs and App were re-keyed, not just Domain's own entry); on the pre-P2-fix key the count would grow by exactly 1 (only Domain), causing the assertion to fail.
- **`check(speed="fast")` now reports `totalDiagnostics` consistent with the surfaced `diagnostics` list (#133).** Since 0.10.0 the fast path surfaces severity-3/4 (information/hint) diagnostics when the caller raises the `severity` floor, but the response still set `totalDiagnostics = ErrorCount + WarningCount`, so an info-only snapshot returned a non-empty `diagnostics` list with `totalDiagnostics: 0` — list and count disagreeing, unlike the trusted path. `totalDiagnostics` is now the full-set count across all severities (mirroring the trusted path's `allDiags.Length`): with `severity="all"` the surfaced list length and `totalDiagnostics` agree exactly. `verdict` stays error-based and `errorCount`/`warningCount` stay full-set tallies; the trusted path is unchanged. +1 test.
- **Release build is now statically compilable (FS3511 in `find`'s position resolution), so `dotnet build -c Release --warnaserror` is robust across SDK versions.** `ResolveQueryAtPosition`'s five-level-deep match inside a `task {}` prevented the F# compiler from reducing the state machine statically under Release optimization (FS3511). The purely synchronous inner block — `match checkedResults with … Some checkResults -> symbol-lookup …` — was extracted into a dedicated `ResolvePositionInFile` member returning plain `Result<string, JsonNode>` (no awaits needed), leaving the outer `task {}` with a single `return` at its deepest branch, which the compiler can reduce statically. Same fix pattern as `ProjectSweepUses` from #131. No behaviour change.

## [0.10.0] - 2026-06-28

### Added

- **`find <query>` — one multi-project symbol search that supersedes the seven single-project search tools (#128).** Sweeps every member `.fsproj` of the active solution and unions four site kinds in a single call: definitions, references, record-field set sites (`{ Field = expr }` and `{ x with Field = expr }`), and member-usage sites. This recovers cross-project usage sites that the single-project `fcs_find_symbol` / `fcs_record_field_audit` miss — on the consolidation validation fixture the single-project path surfaced **1** site where the whole-solution `find` sweep surfaced **11**. Bare `find(query)` suffices (`kind=auto`, `scope=auto` over the whole solution); optional `kind` (`auto`|`symbol`|`members`|`field`|`definition`|`position`) and `scope` (`auto`|`file`|`project`|`workspace`) narrow it. Falls back to the FSAC `workspace/symbol` index when the FCS sweep is empty, so `matched=false` is a real negative. Mechanics: `docs/tools-detailed.md#find`.
- **`check` — one trustworthy type-check verdict that supersedes the five lower-level check tools (#128).** Returns a single `verdict` (`clean` | `errors` | `unknown`) collapsed from a FRESH in-process FCS type-check, so it never reports the stale-`{}` false-clean that `workspace_diagnostics` can serve right after an `Edit`/`Write` — the false-clean that historically drove agents to fall back to `dotnet build`. Bare `check()` covers the active project (`scope=auto`, `speed=trusted`); optional `scope` (`auto`|`file`|`project`|`workspace`|`snippet`), `path`, `snippet` (inline source), `speed` (`trusted` default | `fast` = cached FSAC snapshot), and `severity`. `unknown` means the check could not run (no project context), not a pass. Mechanics: `docs/tools-detailed.md#check`.

### Changed

- **The 12 legacy "cluster" tools are now routing-steered toward `find` / `check`.** The seven find-cluster descriptions (`fcs_find_symbol`, `fcs_record_field_audit`, `fcs_find_member_usages`, `workspace_symbol`, `fcs_project_symbol_uses`, `textDocument_references`, `textDocument_definition`) gained a trailing `Prefer find for cross-project work.` steer; the five check-cluster descriptions (`workspace_diagnostics`, `fsharp_compile`, `fcs_check_file`, `fcs_parse_and_check_file`, `fcs_validate_snippet`) gained `Prefer check for a yes/no verdict.` `fcs_record_field_audit` was trimmed before the steer (491 → 497 after; cut the `during widening refactors` clause and a `see` filler word) to stay within the 500-char `ToolDescriptionSchemaTests` / `audit-tool-descriptions.py` ceiling. README, `AGENT_INTEGRATION.md`, and `docs/tools-detailed.md` now lead with `find` / `check` and mark the twelve cluster tools as lower-level primitives. The cluster tools remain fully registered — actual removal is telemetry-gated and deferred to a future release.
- **`find` now defaults to compact one-line-per-site output (`contextLines` defaults to `0`, down from `1`; default `maxResults` 500 → 80).** Each site emits `file` / `range` / `kind` / `project` / `symbolFullName` / `lineText` with **no** `before`/`after` context arrays unless `contextLines > 0` is passed, and the `breakdown` + `resolution` summary is always present (the full-set counts are independent of the page cap). Native dogfooding found a bare `find(query)` on a hot symbol overflowed the MCP token ceiling (~73k chars, over the ~25k-token limit). With the compact default plus the de-duplication below, a 69-site hot symbol (`find("FindArgs")`) now fits one page at ~37k chars (down from a truncated 40-site page at ~41k chars), and pagination (`cursor`/`nextCursor`) remains the backstop for genuinely huge result sets. Pass `contextLines:2` to restore the previous richer output.
- **`find` now returns a single de-duplicated flat `sites` list — the grouped `definitions` / `references` / `fieldSites` / `memberSites` buckets were removed.** Every site was previously serialized twice (once in its grouped bucket, once in the flat `sites` superset), doubling the payload — the reason the default page cap had been forced down to 40. Each flat-`sites` entry is already self-describing (`file` / `range` / `kind` / `project` / `symbolFullName` / `lineText`), so an agent filters by `kind` and reads `breakdown` for per-kind counts; `breakdown`, `resolution`, `perProject`, and the pagination envelope are unchanged. The per-site cost halved (~1030 → ~527 chars; at a fixed 54-site page `find("FindArgs")` dropped from ~55.6k chars to ~28.4k chars), so the default `maxResults` was retuned 40 → 80 to fit more results in one call while staying safely under the MCP token ceiling.

### Fixed

Pre-publish fixes folded into 0.10.0 from the Codex review of #129 / #126 (all in unreleased code):

- **`find(kind=position)` now keys the sweep on the resolved symbol's `FullName`, not its `DisplayName`.** A cursor resolves THE specific symbol under it; keying the subsequent multi-project sweep on `DisplayName` matched every same-named symbol, so positioning on one `Config` swept an unrelated `Config` in another namespace (and every same-named overload). The sweep key now prefers `FullName`, falling back to `DisplayName` only for locals/synthetic symbols that lack a useful one.
- **`find(kind=field|members)` now honors `exact=false` on the declaring type.** The generic symbol branch already did substring matching, but the field/member declaring-type predicate still used ordinal/full-name-boundary matching — so `find(query="role", exact=false, field="Propose")` missed `TraderRole.Propose`. The declaring-type predicate now threads `exact` through, matching the symbol branch.
- **`check(speed="fast")` now surfaces diagnostics at the requested `severity` floor.** The cached FSAC snapshot stored only error-severity (code 1) entries, so `check(speed="fast", severity="warning"|"all")` omitted the warnings the caller asked for (and the reported `warningCount` was inconsistent with the empty list). The snapshot now retains all severities and the fast path filters them by the requested floor; `errorCount`/`warningCount` stay full-set tallies and `verdict` stays error-based.
- **`fcs_nuget_members` now enumerates class fields.** The field guard was `IsFSharpRecord || IsValueType`, which skipped public `val` fields on reference-type classes (they live only in `FSharpFields`, not `MembersFunctionsAndValues`). Classes are now enumerated too, with compiler-generated backing fields (`Name@` / `<Prop>k__BackingField`) filtered so an auto-property is not duplicated by its hidden field. (FCS still returns an empty `FSharpFields` for classes imported from a C#/IL assembly, so C# instance/const fields remain unrecoverable through the symbol API — now documented in `docs/tools-detailed.md`.)

## [0.9.3] - 2026-06-28

### Added

- **`fcs_nuget_members <packageId> <typeName>` — member-level companion to `fcs_nuget_types` (#125).** Enumerates the members of one type from a referenced NuGet assembly (methods, properties, fields, constructors, events, union cases) with formatted FCS signatures, accessibility, `[Obsolete]` flag, and XML-doc summary. Resolves the assembly through the same `GetReferencedAssemblies()` path as `fcs_nuget_types`; paginated (default 500, max 2000), returns an empty `matchedTypes` marker (not an error) on no match. Type matching is generic-arity-aware (a bare `FSharpOption` resolves the arity-suffixed compiled type), compiler-generated property accessors (`get_`/`set_`) are filtered so a property isn't duplicated by its accessor, and identical member rows are de-duplicated. +8 tests; the tool description passes the `docs/tool-description-schema.md` audit.

### Fixed

- **`set_project` now returns a clear `invalid_args` envelope naming `projectPath` when the argument is missing or supplied under the wrong key (#100).** Previously a wrong/missing key let `Path.GetFullPath(null)` throw `ArgumentNullException` whose message named the internal `path` parameter (`Value cannot be null. (Parameter 'path')`), misdirecting callers to the wrong field. The handler now routes through `ArgsValidation.requireNonBlank "projectPath"` before any path operation, matching the other six handlers standardised in #120. +2 tests.

### Security

- **Patched the MessagePack serialization chain behind the StreamJsonRpc transport (DoS advisories).** `StreamJsonRpc` 2.24.\* → 2.25.\* (pulls patched `MessagePack` 2.5.302) and `Nerdbank.MessagePack` 1.1.62 → 1.2.30 (clears GHSA-92vj-hp7m-gwcj and GHSA-qjvr-435c-5fjh). Pinned `Microsoft.NET.StringTools` to 18.4.0: `Nerdbank.MessagePack` 1.2.30 drops the transitive StringTools 18.4.0 that had been masking MessagePack's stale 17.6.3 — which lacks `SpanBasedStringBuilder.Equals(string, StringComparison)` and silently breaks Ionide.ProjInfo's MSBuild project loading. `dotnet restore` now passes with NuGet audit enabled; 301/301 tests green.

## [0.9.2] - 2026-05-21

### Changed

- **Tool description sweep against `docs/tool-description-schema.md` (5-slot schema, 500-char ceiling).** All 33 registered tool descriptions in `Program.fs` re-audited. Four routing-blockers fixed (descriptions that exceeded the 500-char ceiling and pushed implementation detail into the agent-routing prompt budget): `fcs_check_file` 610 → 452, `fcs_find_member_usages` 581 → 448, `fcs_make_internal_visible` 699 → 419, `fcs_type_at_position` 622 → 451. Mechanics moved to dedicated H2 sections in `docs/tools-detailed.md` (now covers 9 tools, up from 5). Eight additional descriptions gained explicit `prefer X` / `avoid Y` callouts to close overlap-pair ambiguity (`workspace_diagnostics`→`fcs_check_file` stale-cache hint, `fcs_referenced_symbols`↔`fcs_nuget_types` substring-vs-exact routing, `fcs_validate_snippet`→`fcs_parse_and_check_file` when-file-exists hint, `fsharp_compile`→`dotnet build` for IL emission, `fsharp_project_inspect` over textual `.fsproj` reads, `fcs_project_outline` over `workspace_symbol` for whole-project overview, `textDocument_rename` over textual rename).

### Added

- **`scripts/audit-tool-descriptions.py` + `just audit-descriptions`.** Python analyzer enforcing the schema: parses `TypedTool.define` entries from `Program.fs`, applies 500-char ceiling + tag-prefix rule as hard failures (non-zero exit), reports under-budget descriptions and missing overlap-pair callouts as soft warnings. Closes the long-standing reference in `docs/tool-description-schema.md` §"Adding a new tool" → step 4 ("Run the length analyzer") to a previously non-existent tool.
- **`ToolDescriptionSchemaTests.fs` in the xunit suite (+4 tests, 289 → 293 passing).** F#-side enforcement so `dotnet test` is the canonical gate (CI already runs it; the Python script is the local dev convenience). Three rules enforced: every description starts with a recognized tag prefix (`[FSAC]` / `[FCS in-process]` / `[meta]`), no description exceeds 500 chars, every overlap pair has at least one `prefer X` / `avoid Y` callout. Violations are aggregated per rule so the failure message lists all offenders at once (not just the first).
- **4 new H2 sections in `docs/tools-detailed.md`:** `fcs_check_file`, `fcs_find_member_usages`, `fcs_make_internal_visible`, `fcs_type_at_position`. Each follows the established template (`How it works internally`, `Caveats`, `Related tools`). Routing descriptions in `Program.fs` cross-reference these via `Mechanics: docs/tools-detailed.md#<anchor>` footers.

### Notes

- No behaviour changes. Descriptions are routing-time metadata only; agent prompt-budget pressure shrank by ~750 chars / ~190 tokens across the four over-budget tools combined.

## [0.9.1] - 2026-05-21

### Documentation

- **`AGENT_INTEGRATION.md` added at repo root** (158 + 16-line refresh = 174 lines). Salvaged from a prior orphan branch (`docs/agent-integration-guide`, authored 2026-05-17), then surgically refreshed for tools shipped since: boundary heuristic table now points to `fcs_nuget_types` / `fcs_referenced_symbols` (v0.7.0, #113) for NuGet type enumeration instead of `dotnet fsi + reflection`; points to `fcs_validate_snippet` with `mode="fsi"` (v0.7.0, #112) for `.fsi` sketch validation instead of "until `parse_fsi_sketch` lands". Subagent-brief template lists newer tools (`fcs_suggest_open` from v0.9.0 #67, `fcs_record_field_audit` from v0.8.0 #117). "See also" footer links `docs/troubleshooting.md` and `docs/tool-description-schema.md`. CLAUDE.md referenced this file as the canonical multi-agent integration guide for distribution to other contributors — now the file actually exists.
- **`CONTRIBUTING.md` added at repo root** (86 lines). Build + test commands with current 289-test baseline. Step-by-step walkthrough for "Adding a new MCP tool" referencing real files (Types.fs args records with `///` docs, FcsBridge member implementation, Program.fs `TypedTool.define` registration, description per `docs/tool-description-schema.md`, regression tests). Repo conventions (no `Co-Authored-By`, F# style, warnings-as-errors via `Directory.Build.props`, `ArgsValidation.requireNonBlank` at Types.fs:415, proof-by-breaking for regression tests). PR process. Bug/feature reporting routes (issue #100 catch-all for usage feedback, dedicated issues for concrete bugs).
- **`docs/architecture.md` added** (102 lines). High-level diagram showing the two-bridge architecture (stdio MCP transport → `FsAutoCompleteBridge` for fsautocomplete child process + `FcsBridge` for in-process FCS). File-by-file tour in compile order matching `FsLangMcp.fsproj`. Design-choice paragraphs covering: two-bridge rationale, Workstation+Concurrent GC defaults (v0.5.1 fix for stdio MCP bursty workload), tool-description schema enforcement (~2700-token system-prompt budget), structured error envelope (v0.8.2 #120). "Where to add things" pointers cross-link to CONTRIBUTING.md and the schema doc.
- **`docs/roadmap.md` added** (51 lines). Pre-1.0 status. **1.0 exit criteria** (concrete, testable): all #100-class blockers closed, surface stable across 3 consecutive minor releases, `///` field docs surface in MCP JSON schema (currently they don't — see v0.8.6 CHANGELOG), VS.Threading bind race fully closed (v0.8.2 #121 partial), performance baselines (warm `set_project` ≤2s on 10-project solution; cold `fcs_record_field_audit` ≤5s on 50k-LOC project), documentation reflects current surface, ≥1 third-party adoption report. **Deferred items** with issue links. **"Will NOT do"** section: not competing with Ionide for editor use, not replacing `dotnet build` for ground-truth project validation, not adding Paket support.
- **README polish**:
  - Two new shields.io badges: `nuget/dt/FsLangMcp` (downloads total) and `badge/target-net10.0`.
  - New `### When to use FsLangMcp (vs alternatives)` subsection between Components and Quickstart — three concrete "if your agent needs X, use Y" bullets covering raw FSAC, FsLangMcp itself, and raw FCS.
  - New "Which 'check this code' tool" callout inside the FCS in-process section before the tool table — explicit decision tree for `fcs_parse_and_check_file` / `fcs_check_file` / `fsharp_compile`.

(No code or behaviour changes. Closes the remaining developer-advocate audit findings; 1.0 readiness is now a documented track, not a vibe.)

## [0.9.0] - 2026-05-20

### Added

- **`fcs_suggest_open` (closes #67) — given an unresolved symbol name (e.g. from an `FS0039 'X' is not defined` diagnostic), returns ranked `open` directive candidates from BOTH the project and referenced assemblies (BCL + NuGet).** Composes the same `EnsureProjectResults` + `allEntitiesFromAssembly` primitives that `fcs_referenced_symbols` and `fcs_nuget_types` use. Resolves `FSharpEntity.AccessPath` for each match. Ranking: project hits first (more relevant), then references; dedup by `(accessPath, fullName)` within each tier; reference entries that duplicate a project entry are dropped from the reference tier entirely. Output shape: `{ candidateCount, candidates: [{ openPath, entityFullName, source: "project"|"reference", assembly, kind, accessibility }] }`. Closes the most common F# error→fix loop where an agent would otherwise have to guess the namespace or read docs.

### Changed

- **`FieldFormClassifier` walker reimplemented via `ParsedInput.fold` (closes #124).** The previous 88-line hand-rolled `walkExpr`/`walkDecl` recursion only descended into a curated subset of `SynExpr`/`SynModuleDecl` arms — record literals inside class member bodies (`SynModuleDecl.Types`), `for ... do` loops (`SynExpr.For`/`ForEach`), CE-bind continuations (`SynExpr.LetOrUseBang`), `function | ... ->`, `while`, `New`, `ObjExpr`, `Lazy` were all missed and fell through to the textual heuristic fallback. New 33-line implementation uses `ParsedInput.fold` (the public FCS 43.12+ position-independent whole-tree fold API) with a single match arm on `SynExpr.Record`; every expression-containing AST node is covered automatically and the implementation has no maintenance burden as FCS adds new node types. The `fallbackHeuristic` callsites in `formOf` are preserved as defensive safety nets for parse failures and any future un-handled node kinds. Schema-compatible: `form` values remain `"literal"`/`"with-update"`/`"unknown"`. +3 regression tests proven via proof-by-breaking (with the new walker bypassed to fallback-only, those 3 tests deterministically fail — the assertions distinguish parse-tree-path from fallback, not just smoke-test the audit).

### Tests

- +6 net new tests (3 for #124 walker coverage + 3 for #67 across BCL hit / project hit / ranking / no-hit / invalid-args). 283 → 289 passing.

### Process notes

- Code reviewer iter-1 caught test theatre in BOTH #124 and #67 tests during this batch (fixtures that didn't actually distinguish the path under test). Fixer applied proof-by-breaking to harden the regression tests so they fail deterministically when the production code reverts. Lesson recorded for future review-fix loops.

## [0.8.6] - 2026-05-20

### Documentation

- **Tool description schema documented + 5 over-long descriptions compressed.** An AI-engineer audit of all 32 tool registrations found 7× length variance (154-1096 chars). The longest five — `fcs_validate_snippet`, `fcs_nuget_types`, `fcs_record_field_audit`, `fcs_find_symbol`, `fcs_referenced_symbols` — were over-explaining implementation detail (temp-file splicing, walker internals, exact escape semantics) which belongs in deep docs, not the routing-prompt that LLMs read on every agent call. Compressed each to ≤500 chars following the new 5-slot schema (`[Tag] What + Prefer-X-over-Y + Key-params + Caveat + Cross-ref`); moved the implementation depth to a new `docs/tools-detailed.md` with one H2 section per compressed tool. Total system-prompt overhead dropped from 13,241 → 10,937 chars (-2,304 chars / ~575 tokens saved per agent call).
- **3 FSAC tools gained "prefer FCS alternative" anti-recommendations.** `workspace_symbol`, `textDocument_completion`, `textDocument_definition` were positive-only ("useful for...") and didn't tell agents when to route to the agent-shaped FCS-side equivalents. Each now explicitly points to `fcs_find_symbol` / `fcs_symbol_at_word` / `fcs_type_at_position` for non-IDE workflows.
- **15 Args records documented with `///` field doc-comments** (~74 fields across `CompletionArgs`, `PositionArgs`, `ReferencesArgs`, `SetProjectArgs`, `FSharpCompileArgs`, `ProjectHealthArgs`, `FcsParseAndCheckArgs`, `FcsFileSymbolsArgs`, `FcsFileOutlineArgs`, `FcsSymbolAtWordArgs`, `FSharpProjectInspectArgs`, `FcsSignatureHelpArgs`, `CodeActionArgs`, `RenameArgs`, `RuntimeStatusArgs`). Defaults are stated explicitly (verified against `defaultArg` sites in the handlers — no hallucinations). **Caveat for agent integrators**: confirmed that `///` doc-comments on F# record fields do NOT currently surface in the MCP JSON schema's `properties[x].description`. The value of this change is for IDE tooltips, source-readers, and future schema-gen work that may pick up XML docs — not (yet) for runtime agent arg-construction.
- **New `docs/tool-description-schema.md`** documents the 5-slot schema for future tool authors with good-example + anti-pattern sections, so the next added tool stays within ~250-400 chars and includes a "prefer X over Y" callout when overlap exists.

(No code or behaviour changes. v0.8.6 ships discoverability and documentation polish.)

## [0.8.5] - 2026-05-20

### Documentation

- **NuGet listing populated.** The package previously shipped with `description: "Package Description"` (the literal SDK default), no tags, no project URL, no license. v0.8.5 adds `<Description>`, `<Authors>` (Roman Melnikov), `<PackageTags>` (`fsharp;mcp;lsp;fsautocomplete;fcs;ai;agent;ionide;compiler-service;claude-code`), `<RepositoryUrl>` / `<RepositoryType>` / `<PackageProjectUrl>` pointing at the GitHub repo, `<PackageLicenseExpression>MIT</PackageLicenseExpression>`, and `<PackageReleaseNotes>` pointing at CHANGELOG. **A `LICENSE` file (MIT, copyright 2026 Roman Melnikov) was added at the repo root** and packed into the nupkg so the tool ships with explicit licensing — required for corporate-procurement compliance pipelines.
- **README first-screen rewritten as a pitch.** The first 16 lines now answer "who is this for" (AI coding agents on F# projects) and "what makes it different" (agent-shaped tool surface with paginated responses, structured `invalid_args` envelopes, cache-invalidating reanalysis, snippet validation against the loaded project's references, and record-field audits catching construction sites textual search misses). The pre-existing 2-line component list is demoted to a `### Components` subsection.
- **New `## Quickstart` section** — 60-second on-ramp from `dotnet tool install -g FsLangMcp` through `--bootstrap-tools`, MCP client config, the first `set_project` call, and a `project_health` preflight. Each step has a copy-pasteable code block.
- **New `docs/troubleshooting.md`** keyed by user-visible symptoms: `status: "not_ready"` after `set_project`, `FileNotFoundException: Microsoft.VisualStudio.Threading 17.14.0.0` (affects ≤ 0.8.1), stale `workspace_diagnostics` after edit, and `fcs_find_symbol` zero-match-but-symbol-exists (covers both record-field-set sites and broken-project diagnostics-fallback). Linked from the README Quickstart close-out.
- **Customer-name leak in `fcs_record_field_audit` tool description removed.** The string previously referenced "LlmTrader's port-record widening refactors" — a customer-specific anecdote that meant nothing to anyone outside that customer. Rewritten as the generic "port-record widening refactors (adding a field to a domain record type)" use case. Schema and behaviour unchanged.

(No code or behaviour changes. v0.8.5 ships purely to fix NuGet discoverability and onboarding friction surfaced in the developer-advocate audit after v0.8.4.)

## [0.8.4] - 2026-05-20

### Documentation

- **CHANGELOG brought up to date.** 11 missing release entries (0.5.3 → 0.8.3) added between [Unreleased] and the existing 0.5.2 entry. Compare-links footer extended. The CHANGELOG was last updated for 0.5.2 (2026-05-06); since then the project shipped 11 releases including substantial new tools (`fcs_record_field_audit`, `fcs_make_internal_visible`, `fcs_validate_snippet`, `fcs_referenced_symbols`, `fcs_nuget_types`, `fcs_find_member_usages`, `fcs_check_file`, `fslangmcp_version`) and a major correctness rework of `fcs_make_internal_visible`'s text guard (v0.8.1) and `fcs_record_field_audit`'s `formOf` classifier (v0.8.2). Each entry follows the existing Keep a Changelog tone and cites the originating issue.
- **README "What You Get" rewritten** as three tables (LSP-proxy / FCS in-process / Meta) covering all 32 currently-registered MCP tools with 1-sentence descriptions. Previously the list was static at the v0.5.x surface and was missing roughly 10 tools added across 0.6.x-0.8.x.
- **README "Response Shape" extended** with the standardised `{"status": "invalid_args", "message": "..."}` envelope (from v0.8.2 #120) and a new "Notable response fields" subsection covering `set_project.fslangmcpVersion`/`loadedProjects`/`readiness`, `workspace_diagnostics.analyzedAt`/`mostRecentAnalyzedAt`/`analyzedAtByUri`, `fcs_find_symbol.projectDiagnosticsScope`, and `fsharp_runtime_status.fslangmcpVersion`.
- **Issue-number attributions corrected** in both CHANGELOG and README. The original v0.8.0 and v0.8.1 commit messages contained a systematic swap of #114/#115/#116/#117/#119 (issues created in a different order than the feature-by-feature mental model). The CHANGELOG now matches the actual GitHub issue titles for each cited number; commit history itself is left as-is (shipped tags are immutable). Verified across 29 issue citations.

(No code or behaviour changes in this release — pure documentation.)

## [0.8.3] - 2026-05-20

### Changed

- **`fcs_record_field_audit` description clarifies optional `path`.** Tool description now states explicitly that `path` is optional when `projectPath` is set, and recommends supplying `path`+`text` together when auditing against an unsaved buffer. Closes a doc gap noted in the v0.8.2 triage pass. (No behaviour change.)

### Fixed

- **Comment-only follow-ups from the v0.8.2 review loop.** No behaviour change; 280 tests unchanged.
  - `FieldFormClassifier.classify` now carries a comment above the `SigFile` arm explaining the intentionally-empty dictionary (`.fsi` files have no record-literal use sites), guarding against a future maintainer "completing" the empty branch.
  - `RecordFieldAudit.parseFileForForms` gains a `PERF TODO` noting the serial `Async.RunSynchronously` cold-cache cost; references the `#122` follow-up issue (`#124`) as the natural place to fold in parallelization or batched-parse improvements.

## [0.8.2] - 2026-05-20

Sequential subagent pipeline closed the four-issue follow-up batch from the v0.8.1 triage. One real bug (#121) and three refactors (#120, #122, #123). 268 → 280 tests, all review-loop must-fix items closed.

### Fixed

- **`set_project` no longer intermittently fails with `Could not load Microsoft.VisualStudio.Threading 17.14.0.0` in subagent / multi-process hosts.** The assembly was on disk; the failure was a transitive bind via StreamJsonRpc 2.24 → SolutionPersistence with `rollForward` defaulting to `Minor` in `runtimeconfig`. Fix promotes `Microsoft.VisualStudio.Threading.Only 17.14.15` from a transitive to a top-level `PackageReference` so the bind is satisfied at startup probing rather than at first use of StreamJsonRpc internals, and adds `rollForward: LatestMajor` to `runtimeconfig.template.json` (matching peer F# tools — `fsautocomplete`, `Ionide.ProjInfo.tool`). Observed across 9 separate subagent sessions on 2026-05-19. (#121, P1)

### Changed

- **`workspace_diagnostics.mostRecentAnalyzedAt` is now scoped to the `fileGlob`-filtered subset.** Previously the field returned the workspace-wide max regardless of filter — misleading when callers scoped to `src/Adapters/*.fs` but got a timestamp from `tests/`. Reuses the same `globMatches` predicate that filters `analyzedAtByUri`, so the timestamp reflects exactly the scoped subset. Backward-compatible when `fileGlob` is `None`. (#123)
- **Standardised required-string arg validation across six MCP handlers** (`fcs_record_field_audit`, `fcs_project_symbol_uses`, `fcs_find_member_usages`, `fcs_find_symbol`, `fcs_referenced_symbols`, `fcs_nuget_types`). Previously each used a different guard (`Option.ofObj`+default, `invalidArg`, or none); now all go through `ArgsValidation.requireNonBlank : string -> string -> Result<string, JsonNode>` in `Types.fs` and return the standard `{ "status": "invalid_args", "message": "…" }` envelope. `fcs_make_internal_visible` gained validation it previously lacked. **Behaviour change:** `fcs_project_symbol_uses` / `fcs_find_member_usages` / `fcs_find_symbol` on blank required-string args previously raised `ArgumentException` (surfacing as MCP transport-level error); they now return the structured envelope, same as the other three handlers. Aligns the FCS tool surface to one error contract. (#120)
- **`fcs_record_field_audit` `form` classifier now uses an AST walker instead of a textual heuristic.** Old heuristic scanned the field's start line + 2 preceding lines for `with`; multi-line record updates beyond 2 lines were misclassified as `literal`. New `FieldFormClassifier` walks `SynExpr.Record` and tags each field-name range based on `copyInfo.IsSome` (`with-update`) vs `IsNone` (`literal`). Strictly additive: when the walker doesn't visit a site (member-body, for-loop, computation-expression bind), falls back to the textual heuristic so behaviour never regresses below v0.8.1. Wire format unchanged — `form` still takes `"literal"` / `"with-update"` / `"unknown"`. (#122)

## [0.8.1] - 2026-05-19

Four-iteration reviewer/fixer loop on v0.8.0 (`engineering-code-reviewer` ↔ `engineering-fsharp-developer`) until APPROVED-CLEAN. Geometric convergence (6 → 3 → 3 → 1 → 0), scope stayed inside areas v0.8.0 touched. 256 → 268 tests, no API change.

### Fixed

- **`fcs_make_internal_visible` no longer risks corrupting string literals containing ` private`.** `FindPrivateSpan` could strip the substring ` private` from inside a regular string literal on a recognized-keyword line — `let msg = " private channel"` was at risk. Now blocked by a state-machine `PositionIsUnsafe` helper that classifies each character as code / regular-string / verbatim-string / triple-quoted-string / block-comment / line-comment. (P1, close-call latent bug.) (#118 follow-up)
- **`fcs_make_internal_visible` handles F# verbatim (`@"..."` with `""` escape, literal `\`) and triple-quoted (`"""..."""`) strings correctly.** Previously the classifier only handled C-style escaping. (#118 follow-up)
- **`fcs_make_internal_visible` handles `(* doc *) let private foo = 1` correctly.** Strips when block comment closes before the `let`; refuses when the position sits inside an open block comment; refuses on unclosed block-comment openers. Discriminator is `pos < i` (correct inside-span check), not the tautological `pos > commentStart`. `FindPrivateSpan` also peels `(* ... *)` and `[<...>]` prefix blocks from the `before`-text so post-comment / post-attribute declarations are recognised. (#118 follow-up)
- **`fcs_make_internal_visible` recognises `and` for mutually-recursive bindings.** `let rec foo = ... and private bar = ...` now strips correctly. (#118 follow-up)
- **`fcs_find_symbol` surfaces error-severity diagnostics on zero-match queries** instead of returning an empty result that hides project breakage. When `matchedFileSet` is empty, falls back to whole-project error-severity diagnostics with new response field `projectDiagnosticsScope = "errors-only-no-matches"` so callers can distinguish "no matches because the project is broken" from "no matches because the symbol doesn't exist". (#114 follow-up)
- **`project_health` no longer false-positives `[<TestFixture>]` as a test count.** Test-attribute regex gained `\b` word-boundary so the `TestFixture` declaration doesn't count as one `Test`. (#119 follow-up)
- **`project_health.binaryOutputPath` no longer walks the entire monorepo.** `findLatestBuildArtifact` restricted to the project's `bin/` directory while retaining case-insensitive filename equality so Linux CI with different casing still resolves. (#119 follow-up)
- **`Version.current` no longer throws `NullReferenceException` on degenerate SDK builds.** Null/empty `AssemblyInformationalVersion` is now guarded at module init. (#115 follow-up)

## [0.8.0] - 2026-05-19

Two-commit minor release. The first commit (`b8a1207`) wired version metadata and analysis timestamps; the second (`b6a4aa8`) added two new tools and enriched two existing ones. All changes additive.

### Added

- **`fcs_record_field_audit`** — new FCS tool that finds every construction site for a `(typeName, fieldName)` pair, covering both `{ Field = expr; ... }` literal form and `{ existing with Field = expr }` update form. Complements `fcs_find_symbol` / `textDocument_references`, which look up the *type name* and miss field-set uses. FCS predicate: `symbol :? FSharpField && DeclaringEntity match && Name match && not IsFromDefinition`. Each site reports file, range, `form` (`literal` / `with-update` / `unknown` via line-text heuristic — false positives possible if a comment contains ` with `; replaced with parse-tree classifier in 0.8.2), and 2 lines of source context. Paginated; default 200, max 1000. `path`+`text` together supports unsaved-buffer audits. Validated on LlmTrader's `TraderRole.Propose` (28 caller sites; only 2 were found by `fcs_find_symbol`) and `RiskDebatorRole`. (#117)
- **`fcs_make_internal_visible`** — new FCS tool (Variant A only) that drops the `private` keyword from a `let` / `let rec` / `module` / `module rec` / `type` / `member` / `val` / `new` / `static` / `abstract` / `override` declaration at the given `(line, character)`. Returns a workspace edit `{ status: "ok", edits: [{ range, newText: "" }], appliedPreview, originalLineText }` — does **not** write the file. When the position has no symbol or the line has no recognized `private` modifier, returns `{ status: "no_action", reason }`. Detection is text-driven (FCS `IsFromDefinition` + `Accessibility.IsPrivate` proved brittle on module-level let bindings); `FindPrivateSpan` requires a recognized declaration keyword before ` private` and peels off `[<...>]` attribute blocks. Variant B (auto-add `InternalsVisibleTo` on the test project) deferred to a follow-up. (#118)
- **`fslangmcp_version`** — new zero-arg meta tool returning `{ fslangmcpVersion, productName }` for explicit "what am I talking to" queries. Pure: no project context required, no side effects. (#115)
- **`set_project` response includes `fslangmcpVersion`** as the first field in `result`. (#115)
- **`fsharp_runtime_status` response includes top-level `fslangmcpVersion`**. (#115)
- **`workspace_diagnostics` response carries analysis timestamps.** Single-file response gains `analyzedAt`; workspace-wide response gains `mostRecentAnalyzedAt` and `analyzedAtByUri` (per-URI `DateTimeOffset` map). Callers can now self-check "my last `Write` was at T1, the `analyzedAt` is T2 < T1, results are stale" without falling back to a build. `DiagnosticsTarget` records the timestamp whenever FSAC pushes `publishDiagnostics`. `forceReanalyze` arg + FSAC re-analyze hook deferred to a follow-up — needs protocol-level investigation; timestamps cover the highest-value half of the issue. (#116 partial)
- **`project_health` per-project enrichment.** Reports `isTestProject` (xunit / NUnit / Expecto via `PackageReference`), `testFrameworks`, `testCount` (Fact / Theory / Test / TestCase / TestMethod regex, qualified-name- and `Attribute`-suffix-tolerant), `lastBuildSucceeded`, `lastBuildAt`, and `binaryOutputPath` (bin glob keyed off `<AssemblyName>` with fallback to project basename). Pure file-system + regex; no `dotnet test` invocation. (#119)
- **`fcs_find_symbol` projectDiagnostics scoping.** `FcsFindSymbolArgs` gains `includeInfo: bool option` (default `false`). The `projectDiagnostics` field is now filtered to (a) files containing a match for `symbolQuery` AND (b) severity ≥ `Warning` unless `includeInfo=true`. New response fields: `includeInfo`, `matchedFileCount`, `projectDiagnosticsScope: "matched-files"`. (#114)

### Changed

- **MCP `serverInfo.version` now reflects the real product version.** Previously hardcoded `"0.4.0"` — three minor versions stale. Now read from `AssemblyInformationalVersion` at runtime via a new `Version` module (strips any `+commit` suffix). (#115)

## [0.7.0] - 2026-05-19

Three new tools / arg-shape extensions, all additive. 217 → 231 tests.

### Added

- **`fcs_type_at_position` gains `fuzzy: bool`.** When `true` and the exact position misses, scans ±2 lines / ±5 cols (line-weight 2×) and snaps to the nearest symbol; the response then carries `resolvedLine` / `resolvedCharacter` and `fuzzySnap=true`. `no_symbol` responses always include `lineText` and `surroundingLines` so 1-based-vs-0-based mistakes are self-correcting. (#111)
- **`fcs_validate_snippet`** — new FCS tool that compiles an arbitrary F# snippet (`.fs` or `.fsi` mode) against the loaded project's references without modifying the project on disk. Writes the snippet to a uniquely-named file under the OS temp dir, splices it into a fresh copy of project options (cached options not mutated), runs `ParseAndCheckFileInProject`, and deletes the temp file before returning. Useful for "does this signature parse?" probes without scaffolding a scratch `.fsproj`. Caveats documented in the tool description (an `.fsi` snippet without paired `.fs` produces "signature has no implementation"; snippet can reference any project type but not symbols defined later in the compile order). (#112)
- **`fcs_referenced_symbols`** — new FCS tool that searches across the project's *referenced* assemblies (NuGet + framework) for types whose `DisplayName` or `FullName` contains the query (case-insensitive). Complements `workspace_symbol` (project-local). Each result reports assembly, kind (class / interface / struct / enum / record / union / module / ...), accessibility (public / internal / private / unknown), and `isObsolete`. `includeNonPublic=true` exposes internals. Lazy: first call triggers `ParseAndCheckProject` if not warm. Paginated; default 200, max 1000. Cursor stability is best-effort — if the project's references change between calls, the offset may shift; treat the cursor as ephemeral. (#113)
- **`fcs_nuget_types`** — new FCS tool that enumerates all types exported by one referenced assembly, matched by **exact** `SimpleName` (case-insensitive). `packageId='Spectre.Console'` resolves to assembly `Spectre.Console` only — **not** `Spectre.Console.Cli`. `packageId='System'` resolves to the literal `System` assembly only — **not** every `System.*` assembly. When a NuGet package ships multiple assemblies, call once per assembly name. Returns `matchedAssemblies=[]` when no assembly matches; does **not** silently fall back to a less-specific assembly. Paginated; default 500, max 2000. (#113)

### Changed

- **`fcs_nuget_types` matching is strict `SimpleName`-only.** Rejected during review: reverse-prefix and dotted-prefix variants both produced misleading matches under BCL / multi-assembly NuGet packages (e.g. `System` would match every `System.*` assembly). To discover assembly names, callers should use `fcs_referenced_symbols` with a partial query first.
- **Structured `invalid_args` JSON** for empty `query` / `packageId` / `cursor` and missing `projectPath` in `fcs_referenced_symbols` / `fcs_nuget_types` (previously raised; now consistent with `fcs_validate_snippet`).
- **Two-tier fallback for entity name resolution.** `entity.DisplayName` → `entity.LogicalName` → `"<unknown>"` (FCS throws on certain synthetic / anonymous symbols).

## [0.6.1] - 2026-05-19

Three issues closed (#108, #109, #110); 14 new tests (199 → 214). All changes additive.

### Added

- **`fcs_check_file`** — new FCS tool with surgical cache invalidation for one project. Drops only that project's options + project-results cache entries (other loaded projects keep their warm caches), then calls `checker.InvalidateConfiguration` on the same project before re-running parse + check. Returns a diagnostics-focused payload with `errorCount` + `totalDiagnostics`. Use this when `workspace_diagnostics` looks stale right after an `Edit` / `Write`. Honest docs about FCS's internal AST cache remaining for transitively-referenced files; for absolute cross-project ground truth, fall back to `dotnet build`. Adds `BoundedCache.TryRemove` for per-key removal. (#109)
- **`workspace_diagnostics` filter args.** `DiagnosticsArgs` gains optional `fileGlob` and `severity` fields. Glob uses POSIX / gitignore semantics: `*` matches single segment, `**` matches zero+ segments, `/**/` between segments collapses zero+ dirs. `severity` accepts `"error" | "warning" | "information" | "hint"` and filters diagnostics inside each file; files that empty out after filtering are dropped from the workspace response. Implementation in pure `LspResponseShape` helpers (`severityCodeOf`, `fileMatchesGlob`, `filterDiagnosticsBySeverity`) wired into `LspBridge.Diagnostics`. (#108)
- **Accessibility metadata in symbol JSON.** `symbolToJson` now exposes `"accessibility": "private" | "internal" | "public" | "unknown"`. Wraps `FSharpSymbol.Accessibility` in `try/with` — FCS throws on certain synthetic / anonymous symbols. (#110)

### Changed

- **Glob semantics rewritten from naive `.*` substitution to POSIX-style state machine** (`* → single segment`, `** → cross-segment`, `/**/ → zero+ dirs`, `**/ → leading zero+ dirs`). Code-review-driven.
- **`fcs_check_file` cache invalidation is surgical (per-project)** rather than nuking caches for every loaded project.

## [0.6.0] - 2026-05-18

Two issues closed (#105, #107); 8 new tests (191 → 199). Minor bump because optional-`projectPath` is a public-arg-shape change.

### Added

- **`fcs_find_member_usages`** — new FCS tool finding all usage sites of a member declared on a specific type. Resolves via FCS so dotted access (`x.Foreground`), pipeline application, and overload resolution are handled correctly — unlike a textual `rg`. Pass `typeName` (`DisplayName` e.g. `"Style"` or `FullName` e.g. `"MyApp.Theme.Style"`) and `memberName`. `typeName` matching is deliberately conservative: `DisplayName` equality always (so `"Style"` won't false-match `"StyleSheet"`); with `exact=false` the `FullName` may match at segment boundaries (`"Theme.Style"` yes, `"Theme.StyleSheet"` no). Pagination via cursor, same shape as `fcs_project_symbol_uses`. (#107)

### Changed

- **`projectPath` is now optional on per-project tools.** After `set_project`, omit `projectPath` and the active project is used; pass it explicitly to target a different `.fsproj`. Affected arg records (each `string` → `string option`, hence the minor bump):
  - `FSharpCompileArgs`
  - `ProjectHealthArgs`
  - `FcsProjectOutlineArgs`
  - `FSharpProjectInspectArgs`
  - `FcsGetProjectOptionsArgs`

  Resolvers (`ProjectFiles.resolveProjectPath`, `ProjectHealth.resolveHealthProjectPath`, `FcsBridge.CompileProject` / `ProjectOutline`) accept `string option` and return a clear `"projectPath is required. Either pass it or call set_project first"` error when neither path nor active project is available. Reduces per-call token tax for multi-call agent flows. (#105)

## [0.5.6] - 2026-05-18

### Added

- **`set_project` response surfaces `loadedProjects` and `readiness` signals** so agents can tell what was warmed and which downstream tools will work. (#106)
  - `loadedProjects: string array` — `.fsproj` paths discovered for the requested workspace. Single `.fsproj` → `[path]`; `.slnx` / `.sln` → all `.fsproj` entries that exist on disk.
  - `readiness: { lsp: bool, projectOptions: bool, symbolIndex: bool }`:
    - `lsp` — `workspaceLoad` notification received.
    - `projectOptions` — FCS can load options for the first `.fsproj` (probed via `Ionide.ProjInfo` in `Program.fs`).
    - `symbolIndex` — a non-empty `workspace_symbol` response observed since the last `set_project` (sticky flag, reset on LSP restart).

  Resolves the "agent has no way to tell what was warmed" gap from `#100`.

### Changed

- **Internal refactor:** `.sln` / `.slnx` parsing extracted to `FsLangMcp.ProjectFiles.SolutionParsing` (`fsprojsFromSln`, `fsprojsFromSlnx`, `listProjects`). Was duplicated between `ProjectHealth` and the new `SetProject` path. +6 tests for `SolutionParsing`. 191/191 passing.

## [0.5.5] - 2026-05-18

No user-facing changes. Bundles post-0.5.4 follow-ups.

### Changed

- **`LspResponseShape` extracted into a pure module** with 13 unit tests exercising `lspState` / `symbolIndexReady` / diagnostics response shapes. Bridge methods are now trivial wrappers; the full response-building path is unit-testable without spinning up FSAC.
- **CI coverage gate recalibrated from un-measured 55% to baseline-aligned 54%** (current run measures 54.8% on `FsLangMcp.dll` only). The 55% threshold was set conservatively without measuring the actual coverage of the new test layout; 54% leaves a 0.8% buffer below current to catch real regressions. Raising the gate back is tracked as part of integration-test work.

## [0.5.4] - 2026-05-18

Three LSP-readiness issues closed (#102, #103, #104); all response shapes additive (`status` / `result` preserved, new fields appended). 172/172 tests pass.

### Fixed

- **`workspace_diagnostics` returns `lspState` + `diagnosticsFileCount`** so an empty payload during warm-up is distinguishable from "all clean". Previously an empty `{}` could mean either. (#102)
- **`workspace_symbol` returns `lspState` + `symbolIndexReady`.** An empty result within 3s of `workspaceReady=true` is flagged as `symbolIndexReady=false` so agents can tell "no matches" from "index still building". (#103)
- **`fcs_type_at_position` tool description corrected.** Now states explicitly that `set_project` / `projectPath` / `projectOptions` is required for resolved types and that the file at `path` must exist on disk. The previous description oversold standalone capability. (#104)

## [0.5.3] - 2026-05-08

### Added

- **`project_health` accepts `.sln` and `.slnx`** as `projectPath`. Resolves to the single `.fsproj` inside (error if 0 or > 1). `workspacePath` is normalized to its parent directory when a solution file is passed, so FCS does not consider source files outside the workspace. +5 tests covering `.slnx → ready`, `.sln → ready`, `.slnx multi → blocked`, `.sln multi → blocked`, `workspacePath file → normalized to directory`.

### Security

- **Overrode transitive `Nerdbank.MessagePack` to `1.1.62`** to address [GHSA-2cwq-pwfr-wcw3](https://github.com/advisories/GHSA-2cwq-pwfr-wcw3). The entire 1.0.x series is vulnerable; 1.1.62 is the patched line. `StreamJsonRpc 2.24.84` pins the transitive dep to `1.0.2` — a direct reference at `1.1.62` overrides it.

## [0.5.2] - 2026-05-06

### Fixed

- **`textDocument_codeAction` no longer raises `InvalidOperationException` on every call.** `LspBridge.CodeAction` was attaching the same `JsonNode` instance as both `range.start` and `range.end` of the request payload — the second attach failed because each `JsonNode` has a single `Parent`. Build two distinct position nodes. Originally caught in PR #43; landed in #98. (P1)
- **`Cursor.tryDecode` returns a structured `Error` for non-object JSON payloads** (`[]`, `[1,2,3]`, `"x"`, `42`, `true`, `null`) instead of escaping as `InvalidOperationException`. Originally caught in PR #84; landed in #98.
- **`fcs_project_outline` rejects pathological pagination args up front.** `maxFiles=0` previously emitted empty pages with `truncated=true` and a non-advancing `nextCursor` — a non-terminating loop for cursor-following clients. `maxResultsPerFile<0` was similarly silent. Both now raise `ArgumentException` immediately. Originally caught in PR #84; landed in #98.
- **`validateSourcePath` accepts `text = Some ""` as a valid unsaved-buffer state** instead of forcing `File.Exists` on the path. Restores the new-empty-file editor flow. Originally caught in PR #83; landed in #98.
- **`fsharp_runtime_status` survives concurrent FSAC restarts.** `Process.HasExited` could throw if the handle was disposed by a parallel `set_project` / stop. Wrapped in a small live-check helper that treats any read failure as "no live child", returning an empty `children` list rather than failing the whole diagnostic call. Originally caught in PR #85; landed in #98.
- **`publish.yml` `workflow_dispatch` fails fast on non-tag refs.** Manual dispatches from the default branch produced `PackageVersion = "main"` and an opaque `dotnet pack` failure several minutes into the run. An early guard step now refuses anything that isn't a `v*` tag. Originally caught in PR #89; landed in #98.

### Changed

- **Test project layout: linked-source `<Compile Include="../../*.fs">` → `<ProjectReference>`** with `[<assembly: InternalsVisibleTo("FsLangMcp.Tests")>]` baked into the production fsproj (per ADR-0001 in #95, implemented in #97). Closes the linked-source incidents that affected Stryker (#88) and the XPlat Code Coverage collector (#92). User-facing impact: the published nupkg now carries an `InternalsVisibleTo` metadata attribute referencing the test assembly — informational, no behavior change. Coverage gate recalibrated 70% → 55% to reflect the genuine production-only baseline (the pre-migration 78% was inflated by self-instrumented test code).

## [0.5.1] - 2026-05-06

### Changed

- **Workstation GC + Concurrent GC defaults** baked into the published tool via `runtimeconfig.template.json`. The default Server GC commits memory aggressively and only releases under OS pressure — a poor fit for stdio MCP servers, which are bursty (load → idle → next FCS request) and rarely trigger pressure events on dev laptops. Workstation GC returns committed memory promptly while idle, dramatically reducing apparent RSS growth without measurable throughput impact at typical agent request rates. Operators can opt back into Server GC with `DOTNET_gcServer=1` (env always wins over `runtimeconfig`). Aligned with [FsMcp 1.1.0's runtime-tuning guide](https://github.com/Neftedollar/FsMcp/blob/main/docs/runtime-tuning.md).
- **Bumped `FsMcp.Server` and `FsMcp.TaskApi` from 1.0.1 to 1.1.1.** No source changes required; build passes with 0 warnings at WarningLevel 5. New surface in 1.1.x (resources/subscribe, subscribeToolsChanged, HTTP RunSessionHandler) is not consumed by FsLangMCP — our tool list is static and we run on stdio only.

## [0.5.0] - 2026-05-06

### Added

- **`fsharp_runtime_status`** — observational MCP tool reporting runtime, FCS checker config, cache stats, child processes, and assembly counts. Useful for diagnosing FCS resource pressure during long agent sessions. (#79)
- **Cursor pagination across project-scoped tools** — opaque, Base64-encoded cursor returned as `nextCursor` alongside `truncated` / `pageOffset` / `pageSize` / `totalEstimate`. Adopted in:
  - `fcs_project_outline` (paginates files) (#78)
  - `fcs_project_symbol_uses` (paginates uses) (#80)
  - `fcs_find_symbol` project-wide variant (paginates symbol groups) (#80)
- **`memberCounts` map per file** in `fcs_project_outline` summaryOnly mode — top-level `{ kind: count }` reflecting the *full* per-file outline, independent of `maxResultsPerFile`. Replaces the unreleased `_summary` sentinel node. (#82)
- **Filter args** for `fcs_project_outline`: `filter` (regex with `NonBacktracking` + 250 ms timeout) and `nameContains` (substring list). Applied before truncation. (#78)

### Changed

- `fcs_project_outline` defaults are now **conservative**: `summaryOnly = true`, `maxFiles = 50`, `maxResultsPerFile = 30` — keeps responses inside typical 25–50k token windows on ~50-file projects. Callers wanting full detail must opt in. (#78)
- Tool descriptions sharpened to be **agent-facing**: clearer scope, when-to-use guidance, and prerequisites across the FCS tool surface. (#77, #81)
- Internal: `Cursor.paginationFields` takes a `unitName` parameter so each tool's `totalEstimate` carries its own paginated unit (`files` / `uses` / `symbols`).

### Removed

- The synthetic `{ "kind": "_summary", "memberCount": N }` node previously appended inside `fcs_project_outline` summaryOnly entries. **Not breaking** for any released consumer — the sentinel was introduced in #78 between 0.4.0 and 0.5.0 and never shipped to NuGet. (#82)

## [0.4.0] - 2026-05-04

- Added agent-friendly navigation tools: `fcs_file_outline`, `fcs_project_outline`, `fcs_find_symbol`, `fcs_symbol_at_word`.
- Added `fsharp_project_inspect` for read-only `.fsproj` structure, compile order, references, and `.fsi`/`.fs` pairing.
- Added shared project file filtering for generated/build/test artifacts used by project-wide scans.
- Added `fsharp_signature_data` as a structured FSAC signature-data helper.
- Improved `set_project` workspace selection for directories: single solutions/projects are selected explicitly; ambiguous directories return candidates instead of guessing.
- Removed `textDocument_hover` from the exposed MCP tool surface; use `fcs_symbol_at_word` or exact-position FCS helpers instead.

## [0.3.1] - 2026-05-04

- Updated `FSharp.Compiler.Service` to `43.12.203` and aligned the implicit `FSharp.Core` package version to `10.1.203`.
- Centralized FCS / `FSharp.Core` package version settings in `Directory.Build.props`.
- Adjusted signature-help type formatting for the latest FCS API shape.
- Kept `fsharp_compile` on the FCS project typecheck path introduced in 0.3.0.

## [0.3.0] - 2026-05-04

- Added `project_health` for read-only project/tooling preflight.
- Added `fsharp_compile` as an FCS-backed project parse+typecheck tool using `FSharpChecker.ParseAndCheckProject`.
- Removed reliance on the unavailable `fsautocomplete 0.83` `fsharp/compile` endpoint and the `dotnet build` fallback.
- Added F# analyzer wiring and `just analyze`.
- Added tests for project health, compile validation, cache behavior, and invalid compile inputs.

## [0.2.0] - 2026-04-09

- **Breaking**: all LSP proxy responses now wrapped in `{status, result}` envelope. (#39)
- Added `fcs_get_project_options` tool wrapping proj-info. (#27, #37)
- Migrated from `Newtonsoft.Json` to `System.Text.Json`. (#13, #43)
- Performance: offload `LoadProjectOptionsFromFsproj` to `Task.Run`. (#32, #36)
- Refactor: split `Program.fs` into logical modules. (#22, #40)
- Structured `ToolError` DU with `errorKind` field. (#20, #34)

<!--
  Compare links: only versions that exist as git tags are linked.
  Earlier releases (0.2.0, 0.3.0, 0.3.1, 0.4.0) shipped without tags;
  backfilling them would point at synthetic refs.
-->
[Unreleased]: https://github.com/Neftedollar/FsLangMCP/compare/v0.9.1...HEAD
[0.9.1]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.9.1
[0.9.0]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.9.0
[0.8.6]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.8.6
[0.8.5]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.8.5
[0.8.4]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.8.4
[0.8.3]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.8.3
[0.8.2]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.8.2
[0.8.1]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.8.1
[0.8.0]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.8.0
[0.7.0]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.7.0
[0.6.1]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.6.1
[0.6.0]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.6.0
[0.5.6]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.5.6
[0.5.5]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.5.5
[0.5.4]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.5.4
[0.5.3]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.5.3
[0.5.2]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.5.2
[0.5.1]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.5.1
[0.5.0]: https://github.com/Neftedollar/FsLangMCP/releases/tag/v0.5.0
