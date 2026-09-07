---
title: Detailed tool mechanics
description: Implementation behavior, caveats, and routing guidance for the FsLangMCP tools that need deeper explanation.
toc:
  from: 2
  to: 2
---

The MCP description tells you *whether* to call a tool; this file tells you *how it works internally*.

**Start here.** `find` and `check` are the primary entry points in the 35-tool v0.18.0 surface.
The consolidation aliases below were removed in v0.11.0 and are no longer registered:

| Removed names | Current route |
|---|---|
| `workspace_symbol`, `fcs_find_symbol`, `fcs_project_symbol_uses`, `fcs_find_member_usages`, `fcs_record_field_audit`, `textDocument_references`, `textDocument_definition` | `find` (`kind` selects symbol/member/field/definition/position behavior) |
| `workspace_diagnostics`, `fsharp_compile`, `fcs_check_file`, `fcs_parse_and_check_file`, `fcs_validate_snippet` | `check` (`scope` selects file/project/workspace/snippet behavior) |
| `fcs_type_at_position` | `fcs_symbol_at_word`, or `find(kind="position")` when exact coordinates matter |
| `fcs_file_symbols` | `fcs_file_outline` |

---

## set_project

**Routing description:** Initializes or switches the active FSAC/LSP context. Every other tool
resolves its project against whatever `set_project` last bound. Returns a `readiness` object with
both the stable boolean flags (`lsp`, `projectOptions`, `symbolIndex`) and a richer
`symbolIndexState` / `symbolIndexHint` pair that explains *why* `symbolIndex` is `false` and what
to do about it. On an unsatisfiable `global.json` SDK pin, `set_project` instead returns a typed
`{status: "infrastructure_error", errorKind: "sdk_not_found"}` envelope, not a readiness payload
(#192).

### symbolIndexState

| `symbolIndexState` | Meaning | What still works |
|---|---|---|
| `ready` | The FSAC symbol index has produced at least one non-empty `workspace/symbol` result. | Everything, including symbol-index fallback. |
| `warming` | The LSP is up but the index hasn't warmed yet, and either the warm-up window since workspace-ready hasn't elapsed, or `workspaceReadyAt` itself isn't known yet (nothing to measure elapsed time from). | `check`, and every `find` site the FCS sweep produced. `find`'s zero-hit index fallback can be incomplete — read its `fsacFallbackState`. Wait briefly and retry symbol-index-dependent calls. |
| `not_warmed` | The LSP is up, and the warm-up window since workspace-ready has elapsed with no non-empty index result **observed** (#194) — this reports absence of evidence, not a diagnosis. See Caveats: a healthy session can sit here indefinitely. | FCS-derived results are unaffected: `check`, and every `find` site the sweep produced. `find`'s **zero-hit fallback** is the index (`via="fsac-symbol-index"`), so a cold index costs that one confirmation — a zero-hit `find` reads as `not_found` without it. This is a terminal-for-now state, not "still loading". |
| `blocked_on_lsp` | FSAC has a live process tracked, or a restart was explicitly requested, but this call never observed workspace-ready. Covers both a `restartLsp=true` call whose readiness wait timed out and a `restartLsp=false` call reusing an already-live-but-not-yet-ready session. | Nothing LSP-dependent. Retry `set_project` with `restartLsp=true`. |
| `not_started` | Neither a restart was requested nor is an LSP process currently live. Covers a session that never started the LSP as well as one that started and has since died or been stopped. | Nothing LSP-dependent. Call `set_project` with `restartLsp=true`. |

### How it works internally

`warming` and `not_warmed` compare elapsed time since `workspaceReadyAt` against a shared warm-up
window (`symbolIndexWarmupWindow`, 3 seconds) — the same window and the same `workspaceReadyAt`
timestamp used by the internal FSAC `workspace/symbol` probe that `find` falls back to when its
own FCS sweep finds nothing (see `## find`, "How it works internally", step 4). Sharing the inputs
is where the agreement ends: past that window, `assessSymbolIndex` (the probe's readiness check)
and `setProjectReadiness` (used by `set_project`) answer different questions from the same elapsed
time. `assessSymbolIndex` returns `true` — "an empty result is now trustworthy; stop treating it
as still-indexing." `setProjectReadiness` returns `not_warmed` — "no warm signal has been observed
yet." Reusing the window and timestamp gives `set_project` one timing source instead of two
duplicated `TimeSpan.FromSeconds 3.0` literals; it does not make the two surfaces agree on what
elapsed time means.

### Caveats

1. **`not_warmed` is not evidence of a problem.** FSAC's symbol index is queried only as a
   fallback, when `find`'s own FCS sweep comes up empty. A session where `find` keeps resolving
   through FCS may never query the index at all, so `not_warmed` can be the expected, indefinite
   steady state of a perfectly healthy session — not a fault to chase.
2. **What a cold index actually costs `find`.** `check` never touches the symbol index, and
   neither does any `find` result the FCS sweep produced — those sweep FCS project options
   directly. But `find` *is* the consumer of the fallback (#194 review): on a sweep with zero
   hits it probes `workspace/symbol` and, when the index answers, reports the match with
   `via="fsac-symbol-index"`. While the index is cold that confirmation is unavailable, so a
   zero-hit `find` reports `not_found` where a warm index might have matched. `find` says so in
   its own payload — `fsacFallbackState` (`not_ready` / `unavailable` / `failed` /
   `context_mismatch`) and `fsacFallbackReason` — so read those before treating a zero-hit
   `find` as proof of absence. Position-based LSP tools that lean on the index are degraded the
   same way.
3. **The boolean `symbolIndex` field never changes shape.** `symbolIndexState` /
   `symbolIndexHint` are additive; existing integrations reading only the booleans are unaffected.

### Related tools

- `check` — never consults the symbol index; safe to use in any state, including `not_warmed`.
- `find` — its FCS sweep is safe in any state, but its zero-hit fallback is the symbol index.
  Safe to run; read `fsacFallbackState` before reading a zero-hit result as absence.
- `fsharp_runtime_status` — inspect whether the FSAC child process itself is healthy when
  `symbolIndexState` stays `blocked_on_lsp` longer than expected. `not_warmed` alone is not that
  signal — see Caveats.

---

## find

**Routing description:** Multi-project symbol search. Sweeps every member `.fsproj` of the
solution and unions definitions, references, record-field set sites, and member-usage sites.
Bare `find(query)` suffices; optional `kind`
(`auto`|`symbol`|`members`|`field`|`definition`|`position`) and `scope` narrow it. `scopeNote`
names how many projects were actually swept and how to widen/narrow (#193 — see below); a
deadline before discovery instead explains which phase could not finish. Prefer over text search
for cross-project refactors.

**Signature:** `query` is the only required argument. `kind` (default `auto`) and `scope` (default
`auto`) shape the sweep. `exact` (default `true`) toggles exact-vs-substring matching. `member` /
`field` restrict the member-usage / record-field unions. `path` + `line` + `word` + `occurrence` +
`character` anchor `kind=position`. `contextLines` (default 0), `includeDeclaration` (default true),
`includeInfo` (default false), `includeSiteTypes` (default false — see *Field-impact mode*),
`projectPath` (falls back to active `set_project`), `maxResults`
(default 80, valid range 1..1000), `timeoutMs` (default 120000, non-negative), and `cursor` round
out the surface. `scope=file` requires `path`; `scope=project` requires a direct `.fsproj` target or
a `path` that resolves to one member project of the requested solution.

### One request deadline

`timeoutMs` is one monotonic end-to-end budget, including queue admission, position resolution,
project discovery, project-options loading, the semantic sweep, and FSAC fallback. No phase
restarts that clock. Up to 250 ms (5% of the requested budget, with a 1 ms minimum for positive
budgets) is reserved **inside** it for response construction; `responseConstructionAllowanceMs`
reports the reservation. The response planner, diagnostic projection, source-line streaming,
JSON copying, and final serialization checks observe that same bounded allowance.

An immediate or pre-discovery expiry returns `errorKind="find_timeout"` and never performs a new
scan just to count missing projects. A known explicit `.fsproj` is `not_started`; an undiscovered
solution has no invented member count. `coverage.phases` names the affected phase, while
`projectsNotStarted`, `projectsTimedOut`, `projectsBusy`, `projectsMissing`, and `projectsFailed`
remain distinct. An incomplete zero-result answer stays indeterminate.

If response construction expires after the sweep, `errorKind="find_response_timeout"` preserves
the available semantic evidence but sets `resultSetComplete=false`,
`paginationRestartRequired=true`, and `nextCursor=null` for an initial request. Restart without a
cursor after narrowing that request. A continuation instead follows the no-sites same-cursor
retry contract below. `coverage.complete` may still be true: completing analysis does not imply
successful delivery of every site.

When the semantic deadline leaves a positive initial result incomplete, the response is an
explicit cursorless partial: `deliveryStatus="partial"`, `truncated=true`,
`totalEstimateIsLowerBound=true`, `paginationRestartRequired=true`,
`retrySameCursor=false`, and `paginationIncompleteReason="deadline_incomplete"`. The known sites
remain useful, but the complete stream must be restarted from page zero with a larger deadline.
An incomplete continuation is stricter: it returns no sites and no new cursor with
`errorKind="cursor_validation_incomplete"`, `paginationRestartRequired=false`, and
`retrySameCursor=true`.

`breakdownComplete` independently says whether all per-kind counts were computed. If the response
deadline interrupts that counting pass, `breakdown` retains the counted prefix as lower bounds and
sets the flag to false, even though `totalSites` already names the full known site set. Later
context/planner expiry does not invalidate a completed breakdown.

Requests sharing an in-flight worker retain independent deadlines. A short-lived caller cannot
cancel work still needed by a live caller; when all waiters expire, avoidable continuations stop.
Uncancellable work already running retains its admission slot until it actually completes, so an
early timeout cannot create an unbounded worker backlog. A synchronous filesystem or compiler
call already in progress is not forcibly interrupted.

### Bare-call default

`find(query)` runs `kind=auto` over `scope=auto` — i.e. it sweeps **every** member project of the
active solution and unions all four site kinds. No position, no project path, no flags needed; the
common case is a single argument.

### scopeNote (#193)

Sweep breadth is unchanged by any `scope` value — `auto`/`workspace` always sweep every member
project the resolved sweep target actually has, exactly as `file`/`project` always narrow to one.
What's new is that **every response that completes a sweep carries a top-level `scopeNote`**
(`succeeded`, `partial`, and `unknown` all get one) reporting the real outcome (driven by
`projectsSwept`/`projectsAnalyzed`, not by which `scope` string was requested) and the recipe to
change it. Argument validation, ordinary `kind=position` resolution failures, and a missing
project context remain note-less. A typed pre-sweep deadline response instead carries a note
describing the expired phase and explicitly says that later semantic work was not started.

- **One project swept** — however the sweep target arrived (an explicit `.fsproj` `projectPath`, a
  `path`-derived fallback, or a solution/directory that itself has only one member project): the
  note warns that cross-project usages in sibling projects are not visible, and names the actual
  widening recipe — pass the solution's `.sln`/`.slnx` as `projectPath` (or `set_project` it) with
  `scope='workspace'`. Requesting `scope='workspace'` alone, against an `.fsproj` `projectPath`,
  does **not** widen anything: `SolutionParsing.listProjects` on a bare `.fsproj` is always
  `[itself]`, so once the sweep target is a single project, no `scope` value can grow it — only a
  different (solution/directory) `projectPath` can. For `scope='file'` the note additionally names
  the file-level filter (sites are kept only for `path`, not just narrowed to one project).
- **More than one project swept**: the note reports how many member projects of which solution were
  swept, and names the narrowing recipe — pass a single `.fsproj` as `projectPath` to sweep just
  that one project (faster, but misses cross-project usages). This is the discoverability that
  answers the field report behind #193: a 40 s sweep against a solution/directory target now tells
  the caller, in the response, how to narrow it next time.
- **Coverage is never overstated**: if a project timed out, failed, or was rejected as busy before analysis completed
  (`status="partial"`/`"unknown"`, `projectsAnalyzed < projectsRequested`), the note says "analyzed
  K of N" instead of "swept N" and calls out the incompleteness explicitly, instead of claiming a
  sweep happened that didn't.

### Result delivery vs sweep coverage (#235)

`coverage.complete` and `resolution.complete` answer different questions. Coverage is true when
every requested project was analyzed. Resolution is true only when the current response starts at
offset zero and contains all `totalSites`. A first page capped by `maxResults` therefore has
`coverage.complete=true` but `resolution.complete=false`; a later cursor page remains resolution-
incomplete even when it is the final page and `truncated=false`, because that page omits earlier
sites. For an exhaustive refactor count, follow `nextCursor`, reconcile against
`totalEstimate.sites`, and treat only an unpaged offset-zero response as complete in isolation.

### Stateless v2 cursor consistency (#259)

`find` cursors contain only `v=2`, `tool="find"`, the next offset, and fixed-size SHA-256
identities for the canonical query and complete canonical stream. The query identity includes
materialized defaults, normalized target/path and position inputs, and every result-shaping flag.
It deliberately excludes `cursor`, `maxResults`, and `timeoutMs`, so callers may alternate page
sizes or increase the remaining-time budget without changing the logical query.

The stream identity covers the ordered declared project/coverage ledger, resolved query/kind/
scope and position-symbol identity, response-affecting diagnostic identities, and every sorted
site after deterministic bounded source/context and site-type-alternative shaping but before page
slicing. Page size, page offset, and the final 60,000-character envelope cannot reshape a row.
This recomputation retains no per-cursor result state and puts no plaintext query, local path,
diagnostic, or source text in the token. The hash is a consistency identity, not an authorization
mechanism and not a claim that FCS observed an atomic filesystem snapshot.

For position requests, the identity binds the actual resolved symbol's assembly, declaration/
signature locations, and untruncated semantic type/signature, not just its name. Retyping a call
to select another overload therefore invalidates the cursor even if every site row is identical.
A removed identifier or line is stale; unavailable type checking returns incomplete validation.
The same-cursor retry route also applies to deadline expiry during final page planning and
serialization, after the snapshot hash has already matched.

Continuation failures are typed and never return sites:

| `errorKind` | Caller action |
|---|---|
| `cursor_query_mismatch` | The request changed; repeat it without a cursor. |
| `cursor_stale` | Source, solution, coverage, diagnostics, or result rows changed; restart at page zero. |
| `cursor_out_of_range` | Discard the cursor and restart at page zero. |
| `cursor_version_unsupported` | Discard a legacy/unknown-version cursor and restart under v2. |
| `cursor_malformed` | Discard the invalid token and restart. |
| `cursor_tool_mismatch` | Use the cursor only with the tool that issued it. |
| `cursor_validation_incomplete` | Retry the same cursor, normally with a larger `timeoutMs`; no page was delivered. |

After a process restart, an explicit `projectPath` can be validated directly. A request that
relied on active context must repeat `set_project` first. With neither, `find` returns its normal
missing-context error and does not claim that the cursor was validated. Other paginated tools
continue to mint and accept their legacy offset-only cursors, but their strict legacy decoder
rejects a tagged v2 `find` token instead of extracting its offset.

### Serialized response budget (#258)

`maxResults` is an upper bound on sites delivered on the requested page, not a promise that every
requested row fits. `find` first constructs and hashes every canonical pre-pagination row, then
assembles the requested page and measures it with the same indented
`System.Text.Json` options used by `Tools.renderToken`. The hard ceiling is **60,000 UTF-16 code
units** (`System.String.Length` after production serialization), so Unicode is measured in the
same unit the transport string uses rather than estimated bytes or tokens. The measurement covers
every variable section: `sites`, `projectDiagnostics`, `perProject`, coverage/resolution metadata,
notes, and pagination.

Source context is bounded before assembly. `lineText` and every `before`/`after` entry contain at
most 512 UTF-16 code units; a positive `contextLines` request is capped at 8 lines per side.
`range` never changes. `lineTextSourceStartColumn`, `lineTextSourceEndColumn` (exclusive),
`lineTextSourceLength`, and `lineTextTruncated` map the visible matched-line snippet back to the
full source. Context entries carry the analogous `sourceStartColumn`, `sourceEndColumn`,
`sourceLength`, and `truncated` fields. `contextLinesRequested`, `contextLinesApplied`, and
`contextLinesTruncated` expose the vertical cap.

The response planner keeps an in-order site prefix. It first accounts for diagnostics and
per-project rows, then reduces delivered sites until the complete serialized root fits. If even
the first site competes with oversized optional metadata, diagnostics and per-project detail are
reduced with explicit returned/total/truncation fields so pagination can still progress. The
cursor offset is always `pageOffset + returnedSiteCount`; `cursorAdvancedBy` exposes the same
increment. Count-capped and budget-capped pages therefore compose without skips.

Metadata and site prefix counts are selected with logarithmic binary searches, not by removing one
row and reserializing repeatedly. Every probe still builds the complete candidate response and
measures it with the production serializer; no byte, token, or per-row size estimate decides what
fits. Per-project detail retains priority over diagnostics exactly as before: the planner first
keeps all project rows and finds the largest diagnostics prefix that fits. Only when even zero
diagnostics cannot coexist with all project rows does it keep diagnostics empty and search the
largest fitting project prefix.

Raw diagnostics are counted before projection, but only the first 200 eligible records are
converted to JSON. `projectDiagnosticsTotalCount` reports the eligible total;
`projectDiagnosticsCountComplete=false` marks a deadline-interrupted count rather than presenting
a partial count as exhaustive. The serialized budget can reduce the delivered prefix further:
compare `projectDiagnosticsReturnedCount` and `projectDiagnosticsTruncated` with the total.

After planning, every public `Find` result passes once through the same exact-serializer final
guard, including validation, position-resolution, and project-discovery errors that return before
the planner. An oversized early result is replaced by a fixed typed recovery envelope containing no
caller-controlled strings, no continuation cursor, and `cursorAdvancedBy=0`. The guard is reusable
by an outer deadline path for timeout envelopes it originates; applying it does not change timeout
or admission behavior. Its generic recovery requires restarting without a cursor
(`reuseOriginalCursor=false`), because shortening a query/path can change cursor identity; the
site-aware late planner recovery remains separate and retains its more precise cursor semantics.

Every site in the complete sorted stream is projected to a canonical JSON row before page slicing
or planning. Its `project`, source snippet metadata, and deterministic per-site
`siteTypeAlternatives` projection do not depend on
cursor offset, `maxResults`, neighbouring rows, or remaining response budget. The planner may
select only an in-order prefix of those already-formed rows; it never removes or reshapes fields
inside a delivered site.

If one bounded site still cannot fit, the tool returns `status="aborted"`,
`deliveryStatus="blocked"`, and `errorCode="find_site_exceeds_response_budget"`, with scalar
coverage/match ledgers preserved and a `recovery` recipe. `nextCursor` is deliberately `null` and
`cursorAdvancedBy=0`. The planner has already tested one site with diagnostics and per-project rows
reduced as far as possible, so lowering `maxResults` cannot affect the failing payload;
`recovery.sameCursorRetry.allowed=false` even for a continuation. Restart without a cursor before
reducing context/metadata shaping or changing query/kind/member/field, scope/projectPath/path,
position, or any other identity input. Fixed metadata overflow uses
`find_metadata_exceeds_response_budget` under the same non-looping contract.

### kind and scope

| `kind` | What it resolves |
|--------|------------------|
| `auto` (default) | Union of `symbol` + `members` + `field` definitions/references/sites |
| `symbol` | Grouped definitions + references |
| `members` | Member-usage sites on a type; pair with `member` |
| `field` | Record field sites — construction, copy-update, mutation, pattern, read; pair with `field` |
| `definition` | Definition sites only |
| `position` | Exact-position resolution; needs `path` + `line` + `word`/`character` |

| `scope` | Breadth |
|---------|---------|
| `auto` (default) / `workspace` | Sweep every member `.fsproj` of the active solution |
| `project` | The active (or `projectPath`) project only |
| `file` | The single file at `path` only |

### How it works internally

1. Resolves the sweep target list — every member `.fsproj` of the active solution via
   `SolutionParsing.listProjects` (or the single active project when `scope=project`/`file`).
2. For each project, runs FCS name resolution and unions four site kinds: definitions, references,
   record-field sites (see *Field-impact mode* for the five-way field classification), and
   member-usage sites.
3. De-duplicates the union by `(file, range)` and groups by symbol identity.
4. When FCS finds no sites, probes the project-bound FSAC `workspace/symbol` index without
   treating a warming, disconnected, or mismatched FSAC session as a valid zero.
5. Returns flat `sites` entries with `file`, `range`, `lineText`, plus scoped
   `projectDiagnostics` and explicit project-coverage counts.

### Field-impact mode: `includeSiteTypes` (#207)

Changing a record field's type is a fan-out edit: every construction site, every copy-and-update,
every mutation, every destructuring pattern, and every read may need a *different* change, and on a
solution they are spread across projects. `find(kind='field', field='Name', includeSiteTypes=true)`
is built for exactly that loop.

**Site kinds.** Field sites are classified from the parse tree into five kinds, each of which needs
a different edit:

| `kind` | Source shape | The edit it implies |
|--------|--------------|---------------------|
| `field-set-literal` | `{ Field = expr }` | change the value expression |
| `field-set-update` | `{ x with Field = expr }` | change the value expression |
| `field-set-mutation` | `x.Field <- expr` | change the assigned expression |
| `field-pattern` | `\| { Field = binding } ->` | change the pattern / the binding's downstream use |
| `field-read` | `x.Field` in an expression | the read's result type changes — follow the ripple |

`field-set-mutation` and `field-pattern` are new in v0.16.0. Both used to be reported as
`field-read`, which for a mutation labelled a **write** as a read. `breakdown` gains the matching
`fieldSetMutation` / `fieldPattern` counters; the existing counters keep their meaning, and
`fieldRead` now means only "an expression that reads the field".

**`siteType`.** With `includeSiteTypes=true`, every field site row additionally carries `siteType`:
the field's type as the **current** typecheck resolves it at that site, rendered with that site's
own `open`s (so `string`, not `Microsoft.FSharp.Core.string`). Across one sweep the value differs
per row whenever the query spans several fields — or, under `exact=false`, several declaring types
— which is the column that lets an agent plan the edit without opening each file:

```json
{ "file": "…/App/Impact.fs", "range": { "startLine": 12, "startColumn": 25, … },
  "kind": "field-set-mutation", "symbolFullName": "Domain.Shipping.Shipment.Attempts",
  "lineText": "let bump (s: Shipment) = s.Attempts <- s.Attempts + 1", "siteType": "int" }
```

**The check-after-edit boundary (explicit non-goal).** `siteType` is only ever the type the field
has **today** — the "before" half. `find` deliberately does **not** typecheck a hypothetical new
record shape: knowing the "after" type requires compiling the modified code, which is what `check`
does. The intended loop is therefore:

1. `find(kind='field', field='Name', includeSiteTypes=true)` — enumerate every site with its kind,
   line text, and today's type;
2. edit all of them;
3. `check(scope='project')` — one verdict on the result.

Every response with `includeSiteTypes=true` states this boundary in a top-level `siteTypesNote`.

**Ledger and degradation.** A `siteTypes` object accompanies the note:

```json
"siteTypes": { "requested": true, "fieldSites": 8, "typed": 8,
               "degraded": 0, "degradedUnresolved": 0, "degradedTimedOut": 0,
               "typedDifferentlyByAnotherProject": 0, "alternativesTruncatedRows": 0 }
```

`typed + degraded` always equals `fieldSites`, counted over the **whole** matched set rather than
the returned page, so the ledger stays true across pagination. A site whose type FCS cannot produce
gets `siteType: null` and is counted in `degradedUnresolved`; a site reached after the `timeoutMs`
budget is exhausted gets `siteType: null` and is counted in `degradedTimedOut`. A per-site miss is
never allowed to fail the call. The key itself is always present on a field row, so a consumer reads
an explicit `null` rather than having to distinguish it from an absent key.

**One site, several projects.** Sites are de-duplicated by physical source location, so a `.fs`
linked into more than one `.fsproj` is swept once per project — and those projects can resolve the
same field to different types (different conditional symbols, a different generic instantiation).
The FIRST project to resolve it keeps both the `siteType` value and the row's `project` label; every
other answer appears in a per-row `siteTypeAlternatives` array, each entry naming the type **and the
projects that resolved it**, so the site can be planned per project:

```json
"siteType": "int", "project": "ProjA",
"siteTypeAlternatives": [ { "siteType": "bool",   "projects": ["ProjC"] },
                          { "siteType": "string", "projects": ["ProjB"] } ]
```

`typedDifferentlyByAnotherProject` counts those rows. It is a **subset of `typed`**, not a third
bucket, so the `typed + degraded = fieldSites` identity is unchanged. Both the field and the counter
are absent/zero on a normal single-project sweep.

**Why it is bounded and page-invariant.** Unlike `siteType`, the alternatives column grows with the
number of projects a linked file is compiled by. Deterministic per-site limits keep it bounded in
cardinality: at most 3 distinct types per row and 3 projects per type. The remainder becomes
`siteTypeAlternativesOmitted` on the row and `projectsOmitted` on the entry. A capped row also
records `siteTypeAlternativesOffset`, the zero-based index of the first omitted alternative
(not a separate cursor or an input parameter). These limits depend
only on the site's sorted alternatives, so the same site serializes identically at different
`maxResults` and page boundaries. `siteTypes.alternativesTruncatedRows` counts delivered rows that
hit either cap, and the `siteTypesNote` says so. The final 60,000-unit response guard handles total
page size by delivering fewer whole rows, never by dropping `project` or alternative details from
a row. Narrow with `scope`/`projectPath` to see the full picture for a contested file.

**Scope and cost.** `includeSiteTypes` annotates **field sites only** — non-field rows under
`kind='auto'` stay lean, and a `kind` that produces no field sites at all (`definition`, `symbol`,
`members`) gets a `siteTypesNote` saying so instead of silently doing nothing. Type resolution runs
inside `find`'s single `timeoutMs` budget.

**Page budget.** `siteType` is capped at 200 characters (plus a `...` marker), alternatives are
capped as described above, and source snippets are bounded. These row-local controls reduce the
chance of a first-item overflow; the authoritative bound remains the measured 60,000 UTF-16-unit
production JSON ceiling, which may lower the delivered count below the default `maxResults=80`.

**Known limit — generic records.** FCS reports no per-use generic arguments for record fields, so a
field on `Box<'T>` renders as the type **parameter** `'T` at every site, not as the instantiation
(`int`, `string`) at that site. The note repeats this; a regression test pins it, so if a future FCS
starts instantiating, the docs get updated with it.

### The cross-project problem it solves

A symbol resolved only against **one** project's symbol table can miss uses in downstream projects.
On the consolidation validation fixture, the single-project path surfaced **1** site where the
whole-solution `find` sweep surfaced **11**. `find` removes the "did I check every project?" burden —
the bare call already swept all of them.

### Caveats

1. **Read `outcome` and `coverage.complete` before acting on absence.** `matched=false` /
   `outcome="not_found"` is emitted only after every requested FCS project completed. If any
   project failed, timed out, or was rejected as busy, `matched` is `null`, `outcome="indeterminate"`, and
   `status="unknown"` unless another backend positively proves a match. Positive sites from an
   incomplete sweep use `status="partial"` because more sites may still exist.
2. **`exact=true` is the default** — set `exact=false` for case-insensitive substring matching;
   broad substrings on common names ("Id", "Create") can return large pages.
3. **`kind=position` needs coordinates** — supply `path` + `line` + (`word` or `character`).
   0-based LSP coordinates apply.
4. **`siteType` never predicts the post-edit type.** It is the current typecheck's answer only;
   run `check` after the edits for the "after" verdict (see *Field-impact mode*).

### Related tools

- `fcs_symbol_at_word` — tolerant symbol/type inspection when a file, line, and word are known.
- `fcs_project_outline` / `fcs_file_outline` — structural overviews rather than use-site search.

---

## check

**Routing description:** One trustworthy verdict for the active F# context. Bare `check()`
suffices: returns `verdict` (`clean`|`errors`|`unknown`) after a FRESH in-process type-check, so it
never reports a stale-`{}` false-clean within the current FCS/check profile. Optional `scope`
(`auto`|`file`|`project`|`workspace`|`snippet`), `path`, `snippet` (inline source), `speed` (trusted
default | `fast` = cached FSAC snapshot), `snippetPosition` (`start`|`end`, default `end`), and
`severity` shape the request.

**Signature:** every argument is optional. `scope` (default `auto`) picks `snippet` when `snippet`
is set, `file` when `path` is set, else the active project (or the whole solution when it spans
>1 `.fsproj`). `path` / `snippet` / `fileGlob` / `mode` (`fs`|`fsi`) target the unit to check;
`snippetPosition` controls where a snippet sits in project compile order.
`speed` (`trusted` default | `fast`), `severity` (`error` default | `warning` | `information` |
`hint` | `all`), `projectPath` (falls back to active `set_project`), and `timeoutMs` (default 60000)
round out the surface.

### Bare-call default

`check()` runs `scope=auto` at `speed=trusted` — a FRESH in-process FCS type-check of the active
project, collapsed to a single `verdict`. No path, no project, no flags needed for the common
"did my last edit compile?" question.

### verdict and speed

| `verdict` | Meaning |
|-----------|---------|
| `clean` | The checked unit type-checked with zero errors under the current FCS/check profile |
| `errors` | At least one error-severity diagnostic |
| `unknown` | The check could not run (no project context / resolution failed) — **not** clean |

When `unknown` has a machine-actionable infrastructure cause, inspect `blockingReason` rather than
parsing `reason`. An unavailable exact SDK pin reports `errorKind="sdk_not_found"` together with
`requestedSdkVersion`, `installedSdks`, `dotnetHostPath`, configured `sdkRoots`, `globalJsonPath`,
and `remedies`. Project/file checks place it at the top level; a trusted workspace check places it
on the affected `perProject` row. Fast workspace expectation failures may expose multiple
`blockingReasons`. Timeout, cancellation, admission pressure, generic project failures, and an
untyped unavailable FSAC snapshot remain separately classified as `timeout`, `cancelled`,
`fcs_worker_busy`, `project_failure`, and `fsac_unavailable`.

| `speed` | Behaviour |
|---------|-----------|
| `trusted` (default) | Runs a FRESH FCS check; the verdict reflects the current source on disk |
| `fast` | Reads a project-bound FSAC snapshot; `clean` requires a current publication for every evaluated in-scope source file |

### Snippet placement

`scope=snippet` type-checks source text as one synthetic project file. `snippetPosition` makes its
compile-order location explicit and the response echoes the normalized effective value:

| `snippetPosition` | Compile-order semantics |
|-------------------|-------------------------|
| `end` (default) | Appends the snippet after every evaluated project source file, preserving v0.17.1 behavior and allowing it to consume symbols from the final `<Compile>` item |
| `start` | Prepends the snippet before every evaluated project source file, so project-source symbols are intentionally not yet in scope |

Both placements keep diagnostics scoped to the snippet itself: project-file diagnostics and
synthetic wrapper artifacts remain filtered, and temporary files are deleted after checking.
Ordinary F# shadowing rules apply at the selected point; for example, an `end` snippet can open a
project module and then shadow one of its values with a local binding.

This is source-order validation, not built-assembly execution. Symbols that exist only after a
build step (for example generated or emitted members absent from evaluated source files) are not
made available by `snippetPosition=end`.

### How it works internally

1. Resolves `scope`: `snippet` when `snippet` is set, `file` when `path` is set, else `project`
   (escalating to `workspace` when the solution spans more than one `.fsproj`).
2. At `speed=trusted`, runs a fresh in-process FCS pass (`ParseAndCheckFileInProject` for a file,
   `ParseAndCheckProject` for a project/workspace) so the result reflects the current source — never
   a stale cached payload. At `speed=fast`, derives the expected files from evaluated FCS project
   options, then accepts only diagnostics from the matching live FSAC generation.
3. For `scope=snippet`, inserts the synthetic source file at `snippetPosition=start` or `end`, then
   scopes diagnostics to the snippet's content: wrapper artifacts
   (FS0222 missing-module, FS0225 source-file bookkeeping), diagnostics attached to other
   project files, and duplicates are removed, and `file` reads `"snippet"` — so bare
   expression code without a `module` header is valid.
4. Collapses the diagnostics into one `verdict` (`clean` / `errors` / `unknown`).
5. Returns `verdict` + `errorCount` + `warningCount` + a `diagnostics` array filtered to `severity`,
   plus `totalDiagnostics`/`infoCount`/`belowSeverityFloorCount` covering the full severity set (see
   below).

### Project-scope coverage boundary (#222)

`scope=project` checks exactly one project. A library can therefore be `clean` while an app or
test project that references it has an error caused by the same edit. Every resolved project-scope
response makes that boundary machine-readable:

- `downstreamProjectsChecked: false`
- `recommendedScope: "workspace"`
- `coverageNote` explaining that downstream consumers were not analyzed

Use `scope=workspace` with a solution or directory `projectPath` when the verdict must include
consumers. The project path does **not** evaluate or scan downstream projects merely to produce a
count: that would make the narrow check unexpectedly expensive and add more ProjInfo/MSBuild work.
An explicit workspace check against a single `.fsproj` is rejected as `invalid_args`, because it
would still cover only that project while presenting itself as workspace evidence.

### The stale-`{}` problem it solves

Right after an `Edit`/`Write`, a cached FSAC diagnostics snapshot can be an empty `{}` while the
file actually has errors — the false-clean that historically drove agents to fall back to
`dotnet build`. At `speed=trusted`, `check` re-checks fresh in-process. At `speed=fast`, missing or
stale files now produce `unknown` with `complete=false`; a current error remains `errors` even if
the rest of the scope is incomplete.

### Caveats

1. **`unknown` ≠ `clean`** — `unknown` means the check could not run (no `set_project`, unresolved
   options). Establish project context and retry; do not treat it as a pass. Prefer its structured
   `blockingReason`/`blockingReasons` when present; `reason` remains the human-readable explanation.
2. **`severity` filters the array only** — `errorCount`/`warningCount` always reflect the full
   result regardless of the `severity` cutoff applied to the returned `diagnostics`.
3. **`totalDiagnostics = errorCount + warningCount + infoCount`, always** — `totalDiagnostics`
   counts the FULL diagnostic set across every severity (including Info/Hidden), so it can be
   larger than `errorCount + warningCount` alone and larger than the `diagnostics` array length.
   `infoCount` is a tally of the Info+Hidden diagnostics in that full set. `belowSeverityFloorCount`
   says how many of those full-set diagnostics were excluded from `diagnostics` by the `severity`
   floor (never by the 50-item cap — that is `diagnosticsTruncated`'s job); when it is `> 0`,
   `diagnosticsNote` spells out the gap and points at `severity="all"`. At `scope='workspace'`,
   this identity holds per-project too: every successfully analyzed `perProject[]` entry carries
   its own `errorCount` / `warningCount` / `infoCount` (#205), the same genuine
   `countInfoDiagnostics` tally used everywhere else — not a `total - error - warning` remainder.
4. **`speed=fast` is coverage-aware but still cached** — inspect `complete`, `expectedFiles`,
   `missingFiles`, `staleFiles`, and `sessionGeneration`. Use the default `trusted` when you need a
   fresh type-check of a just-written on-disk edit.
5. **Project scope is not a workspace verdict** — read `downstreamProjectsChecked` and follow
   `recommendedScope` before treating a clean library check as evidence about tests/apps.
6. **The current FCS/check profile is not every build configuration** — `clean` does not cover
   configuration-specific compiler options that are absent from that profile. Optimized Release
   compilation can surface diagnostics such as FS3511; before merge or release, run
   `dotnet build -c Release --warnaserror`.

### Related tools

- `fcs_explain_diagnostic` — explain an F# diagnostic after `check` finds it.
- `fcs_diagnostic_fixes` / `fcs_suggest_open` — preview targeted fixes for reported diagnostics.

---

## fcs_nuget_types

**Routing description:** Enumerate all types in one referenced assembly.
`packageId` accepts the NuGet package id OR an assembly SimpleName it ships (exact,
case-insensitive, never a prefix); the two often differ, and multi-assembly packages resolve
fully. Each entry reports `displayName`, `fullName`, `kind`, `accessibility`, `isObsolete`.
Paginated; default 500, max 2000. A miss adds `hint` + `candidatePackages`.

### `packageId` is a package id OR an assembly name

A NuGet package id and the assembly it ships are frequently **different strings**:
`Microsoft.Orleans.Core.Abstractions` ships `Orleans.Core.Abstractions.dll`;
`Microsoft.VisualStudio.Threading.Only` ships `Microsoft.VisualStudio.Threading.dll`. This tool
used to match the assembly SimpleName only, so those packages resolved to zero assemblies and
returned an empty — but `status: "ok"` — payload (issue #191).

Both spellings now resolve. The mapping comes from the project's own restore output
(`obj/project.assets.json`, falling back to the `-r:` reference paths under the NuGet
global-packages cache), so it reflects what this project actually restored — it is not a
heuristic on the string. Prefix matching is still rejected in both directions: `System` does
not match `System.Text.Json`, and `Newtonsoft.Json.Schema` does not fall back to
`Newtonsoft.Json`.

### How it works internally

1. Loads the project via `FSharpChecker.ParseAndCheckProject` (warm from cache if available).
2. Builds a packageId → assembly-SimpleName map from `<projectDir>/obj/project.assets.json`
   (every `targets` entry, `compile` and `runtime` sections). When that file is unavailable,
   the same map is derived from the `-r:` reference paths.
3. Iterates over all referenced assemblies in the project options.
4. Matches an assembly when its `SimpleName` equals `packageId` (case-insensitive, exact) OR
   when the map says `packageId` ships that assembly.
5. Walks the matched assembly's top-level and nested namespaces to collect `FSharpEntity` entries.
6. Filters by accessibility (public by default; set `includeNonPublic=true` for internal/private).
7. Returns a paginated list of type entries.

### Miss payload

When zero assemblies match, two extra fields are added (they are absent on a hit, so the
success shape is unchanged):

| Field | Type | Notes |
|---|---|---|
| `hint` | string | Names the package-id-vs-assembly-name distinction, and distinguishes "not in this project's restore graph" from "restored, but no assembly of it is on the compile line" (analyzer / build-only / runtime-only packages) |
| `candidatePackages` | array | Up to 5 `{ packageId, assemblies }` entries from the restore graph whose id or assembly names relate to the query, so the correct spelling is one turn away |

The map records **every** restored package, including those that ship no referenceable assembly
at all (`IncludeAssets=analyzers`, build-only, content-only). Those appear with
`assemblies: []`, and asking for one by id gets the "restored, but contributes no compile-time
reference" hint rather than the false "not in this project's restore graph".

### Caveats

1. **Exact matching, never prefix** — `packageId="System"` matches only the `System.dll`
   assembly, not `System.Text.Json.dll`, `System.Collections.dll`, etc.
2. **Multi-assembly packages resolve fully** — a package that ships several assemblies (e.g.
   `TypeShape` → `TypeShape.dll` + `TypeShape.CSharp.dll`) returns the types of all of them in
   one call, and `matchedAssemblies` lists each. To narrow to one, pass that assembly's
   SimpleName — but note a name that is *also* a package id (here, `TypeShape`) still resolves
   as the package, so the narrowing works for `TypeShape.CSharp` and not for `TypeShape`.
3. **No-match is never a fuzzy fallback** — when `matchedAssemblies=[]`, read `hint` and
   `candidatePackages`; `fcs_referenced_symbols` searches everything that IS loaded.
4. **Un-restored projects** — if `obj/project.assets.json` is missing AND the reference paths
   are not under a NuGet packages cache, only the SimpleName arm is available (i.e. pre-#191
   behaviour) and the miss payload has no candidates to offer.
5. **Lazy warm-up** — first call after `set_project` triggers `ParseAndCheckProject`, which may
   take several seconds on a large project.

### Related tools

- `fcs_referenced_symbols` — substring search across all referenced assemblies; use to discover
  assembly names before calling this tool.
- `fcs_nuget_members` — drill into one specific type's members after finding it with this tool.

---

## fcs_nuget_members

**Routing description:** Enumerate members of one type from a referenced
assembly (`packageId` + `typeName`). `packageId` accepts the NuGet package id OR an assembly
SimpleName it ships; the two often differ. Use after `fcs_nuget_types` to discover type names.
Each entry: `name`, `kind`, `signature`, `accessibility`, `isAbstract`, `genericParameters`,
`isObsolete`, `xmlDocSummary`.
Paginated; default 500, max 2000. A miss adds `hint`, plus `candidatePackages` when the
`packageId` did not resolve.

### How it works internally

1. Resolves the project via `EnsureProjectResults` (same warm-cache path as `fcs_nuget_types`).
2. Matches assemblies by `SimpleName` OR through the packageId → assembly map built from the
   project's restore output — identical logic to `fcs_nuget_types`, see that section for why
   `Microsoft.Orleans.Core.Abstractions` and `Orleans.Core.Abstractions` both resolve (#191).
3. Walks all entities in the matched assembly via `allEntitiesFromAssembly` and filters those
   whose `DisplayName` equals `typeName` (case-insensitive) OR whose `FullName` equals or ends
   with `.typeName` at a segment boundary.
4. For each matched entity, enumerates three member sources:
   - `MembersFunctionsAndValues` — methods, properties, constructors, events
   - `FSharpFields` — record, struct, AND class fields (e.g. F# `val` fields), with
     compiler-generated backing fields (`Name@`, `<Prop>k__BackingField`) filtered out so an
     auto-property is not duplicated by its hidden field
   - `UnionCases` — F# union cases (only for union types)
5. Reads method accessibility from ECMA-335 metadata when possible, without loading/executing the
   assembly. Project-system `obj/.../ref` assemblies are paired with their implementation under
   `bin/...`; NuGet `ref/<tfm>` assemblies are paired with `lib/<tfm>` when shipped. Ambiguous or
   unavailable metadata falls back to FCS.
6. Renders each `FSharpGenericParameter.Constraints` collection both structurally and as a
   `where T : ...` suffix in the signature.
7. Overloaded methods appear as separate entries with distinct `signature` strings.
8. Filters by accessibility (public/protected by default; `includeNonPublic=true` for private/internal).
9. Returns a paginated list with cursor mechanics identical to `fcs_nuget_types`.

### Response shape per entry

| Field | Type | Notes |
|---|---|---|
| `name` | string | `DisplayName` of the member |
| `kind` | string | `"method"`, `"property"`, `"constructor"`, `"event"`, `"field"`, `"union-case"`, `"function"` |
| `signature` | string | `Name(param: Type, …) -> ReturnType`, plus `where T : class, new()`-style constraints; `Name: Type` for fields |
| `accessibility` | string | `"public"`, `"protected"`, `"protected internal"`, `"internal"`, `"private protected"`, `"private"`, `"unknown"` |
| `isAbstract` | bool | Exact ECMA-335 abstract flag when metadata resolves unambiguously; otherwise the FCS dispatch-slot fallback |
| `genericParameters` | array | `{ name, constraints[] }` rows; empty for non-generic members |
| `isObsolete` | bool | `true` if `[<Obsolete>]` attribute is present |
| `xmlDocSummary` | string\|null | `<summary>` content from `///` XML doc, if available in-source; null for compiled-only assemblies |

### Caveats

1. **Type matching is case-insensitive** but the matched type must appear in the matched assembly.
   Use `fcs_nuget_types` with the same `packageId` to discover the correct `DisplayName`/`FullName`.
2. **`matchedTypes=[]` has two distinct causes, and `hint` says which** — either the
   `packageId` resolved to no assembly at all (then `candidatePackages` lists the closest
   package ids from this project's restore graph, as documented under `fcs_nuget_types`), or
   the assembly resolved but exports no such type (then `hint` names the assembly that was
   searched and points at `fcs_nuget_types`). The tool never falls back to a fuzzy match.
3. **xmlDocSummary is null for compiled BCL/NuGet types** — XML doc is only available for F# source
   files in the loaded project with `///` comments. For BCL types, use the official documentation.
4. **Signature type naming** — uses FCS `BasicQualifiedName` for parameter/return types; generic
   constraints are now present, but BCL types can still appear under F# spellings such as
   `Microsoft.FSharp.Core.string` or `unit`. CLR-oriented naming is tracked separately.
5. **Class fields: F# `val` yes, imported C#/IL fields no** — public `val` fields on a reference-type
   class ARE now enumerated (they live only in `FSharpFields`, not `MembersFunctionsAndValues`), and
   compiler-generated backing fields are filtered. However, FCS returns an empty `FSharpFields` for
   classes imported from a **C#/IL** assembly, so public instance/const fields declared in C# are not
   surfaced through the symbol API — an FCS limitation outside this tool's control.
6. **Warm-up** — first call after `set_project` triggers `ParseAndCheckProject`.

### Related tools

- `fcs_nuget_types` — enumerate all types in an assembly first to discover the correct `typeName`.
- `fcs_referenced_symbols` — substring search across all assemblies when you don't know which
  assembly a type lives in.

---

## fcs_referenced_symbols

**Routing description:** Search across the project's referenced assemblies (NuGet + framework)
for types by DisplayName or FullName substring (case-insensitive). Complements `find`, which
searches project-local source. Each result reports assembly, kind,
accessibility, and `isObsolete`. Set `includeNonPublic=true` for internals. Paginated; default
200, max 1000. First call triggers `ParseAndCheckProject` if not warm. Cursor is best-effort.

### How it works internally

1. Loads the project (warm from FCS cache if `set_project` already ran).
2. Iterates over `FSharpAssembly` entries from the checked project's `ProjectContext.GetReferencedAssemblies()`.
3. Walks each assembly's `FSharpEntity` tree and matches `DisplayName` or `FullName` against
   the `query` string (case-insensitive substring).
4. Applies accessibility filter (public only by default; internals included with
   `includeNonPublic=true`).
5. Returns paginated entries: `displayName`, `fullName`, `assembly`, `kind`, `accessibility`,
   `isObsolete`.

### Caveats

1. **Lazy warm-up** — first call after `set_project` (or on a cold process) triggers
   `ParseAndCheckProject`, which may be slow. Subsequent calls are fast.
2. **Cursor stability** — the cursor encodes a byte offset into the flattened entity stream.
   If the project's NuGet references change (e.g., after a restore), the offset shifts.
   Treat the cursor as ephemeral across project changes.
3. **Scope** — covers only *referenced* assemblies, not the project's own source. For
   project-local symbols, use `find`.
4. **Broad queries** — a single-character query like "I" can return thousands of results;
   use `maxResults` and `cursor` to page through them.

### Related tools

- `fcs_nuget_types` — enumerate all types in a specific assembly, by NuGet package id or by
  assembly SimpleName (exact, either spelling).
- `find` — search project-local symbols with source context.

---

## fcs_make_internal_visible

**Routing description:** Drop the `private` keyword from a declaration at
`(line, character)` — a non-destructive workspace edit that returns
`{ status, edits, appliedPreview, originalLineText }` and does NOT write the file. Use before
tests need to call internals. Returns `{ status: "no_action", reason }` when the position has no
symbol or the line has no recognized `private` modifier. Variant B (auto-add `InternalsVisibleTo`
on the test project) is a planned follow-up.

### Supported declaration forms

The text scan recognizes `private` immediately following any of:

- `let` / `let rec`
- `module` / `module rec`
- `type`
- `member`
- `val`
- `new`
- `static`
- `abstract`
- `override`

### How it works internally

1. Uses FCS to confirm the position at `(line, character)` resolves to a real symbol. This avoids
   editing inside comments or strings that happen to look like declarations.
2. Scans the raw source line via `FindPrivateSpan` for one of the recognized declaration keywords
   followed by ` private`. The state-machine `PositionIsUnsafe` skips:
   - regular string literals (`"..."`)
   - verbatim strings (`@"..."`)
   - triple-quoted strings (`"""..."""`)
   - block comments (`(* ... *)`)
   - line comments (`// ...`)
3. Returns the `private`-token span as a workspace edit with `newText = ""`. The host (Claude
   Code, Cursor, etc.) applies the edit; this tool never writes to disk.

### Caveats

1. **Heuristic-locating, FCS-validating** — the position-to-symbol check is the safety net.
   Without a resolved symbol at the cursor, the tool returns `no_action` rather than guessing.
2. **One declaration per call** — multi-cursor / batch use is not supported. Call the tool once
   per site.
3. **Does not touch `InternalsVisibleTo`** — exposing internals to a test project still requires
   editing the `.fsproj`. That's Variant B (planned).

### Related tools

- `textDocument_rename` — for renames; this tool is access-modifier-only.
- `textDocument_codeAction` — FSAC's general code-action endpoint; less targeted.

---

## fcs_symbol_at_word

**Routing description:** Tolerant FCS symbol lookup by line plus word/occurrence. Returns symbol
identity, kind, type string, definition range, and optional documentation without requiring an
exact cursor column.

### How it works internally

1. Reads project options (cached after `set_project`).
2. Calls `FSharpChecker.ParseAndCheckFileInProject` on the file.
3. Locates the requested `word` occurrence on the 0-based `line`, then asks FCS for the symbol use.
4. Returns symbol identity, kind, type string, definition range, and documentation when available.

### Caveats

1. **Project context required** — without `set_project` (or an explicit project path), types are often
   reported as unresolved or `obj`. Always set the project first.
2. **File must exist on disk** — this tool reads the file by path.
3. **Line is 0-based; occurrence is 1-based** — use `occurrence` to select repeated words on one line.

### Related tools

- `find(kind="position")` — exact-position resolution when coordinates are already known.
- `fcs_signature_help` — overload/parameter info at a call site.

---

## fcs_public_api

**Routing description:** Emit an F# project's public API surface — every public type and its
public members with signatures — sorted stably by `fullName` then member name, so two version
snapshots diff cleanly. Public-only by default (`includeInternal=true` adds `internal`; `private`
is never emitted).

### Response budget and `truncatedByBudget` (#206)

A page is closed for either of two independent reasons, and the response tells you which:
`maxResults` (default 100, hard ceiling 1000) caps the page by **type count**, and a shared
45,000-character serialized-size budget (`responseCharBudget` in `FcsBridge.fs`, the same
constant `fcs_file_outline` uses — see below) caps it by **response size**, closing the page early
even when `maxResults` would still allow more entities. The 45,000 figure is deliberately below the
~72,000-char MCP ceiling it targets: it is measured per-entity, standalone, before the entity is
embedded two levels deeper in the actual response (`entities` inside the root object) and before
the response envelope is added, both of which cost real characters the per-entity measurement can't
see — the margin covers that gap even on signature-*sparse* shapes (short-field records,
member-less modules) where the gap is largest. A handful of API-dense types — long member lists,
verbose generic signatures — can also cross that budget well before the count cap does; this is
what the field failure behind #206 looked like: a 2.5k-line project's default `fcs_public_api` call
produced a page so large the MCP client spilled it to a side file instead of returning it inline.

`truncated: true` means more entities remain regardless of which cap closed the page.
`truncatedByBudget: true` is additive and appears **only** when the char budget was the reason —
absent when the page closed at `maxResults` or reached the end of the (filtered) list. Either way,
whenever a page closes early with entities left over, an additive `hint` field names
`namespaceFilter` together with 2–3 real namespaces pulled from the remainder (not placeholders —
copy-pasteable values from the actual unreturned entities), so the next call can narrow instead of
re-fetching the same oversized shape. A page always contains at least one entity — a single
oversized type is never dropped into an empty page just because it alone exceeds the budget.

### Caveats

1. **An entity's member list is never split across pages.** The budget close operates on whole
   entities, so one page never returns half of a type's members.
2. **The MCP client may still spill an oversized response to a side file, with a client-defined
   shape.** `truncatedByBudget` and the char budget exist to make that spill unnecessary in the
   common case, but a very large single entity (one type with an enormous member list) can still
   exceed the budget on its own. Prefer `namespaceFilter` over parsing a spilled side file — that
   file's shape belongs to the client, not this server.

### Related tools

- `fcs_project_outline` — whole-project structural overview; prefer `fcs_public_api` specifically
  for API-stability/breaking-change diffs.
- `fcs_file_outline` — shares the same response-char-budget mechanism for one file's outline.

---

## fcs_project_outline

`timeoutMs` is one end-to-end deadline (default 60,000 ms), not a fresh allowance per phase. Queue
admission subtracts from it; project evaluation and each sequential file outline receive only the
remainder. Negative values return `invalid_args` / `invalid_timeout`; zero returns deterministic
`unknown` / `project_outline_timeout` before project evaluation starts.

### Coverage and filtered pagination (#243)

Read `status` together with the general `coverage` ledger. `filesRequested` reconciles into
`filesScanned`, `filesTimedOut`, `filesFailed`, and `filesNotStarted`; `phases` and `issues` are
bounded, with `issuesReturned` / `issuesTruncated` describing the sample. `ok` means the requested
work completed, `partial` means at least one file was scanned but evidence is incomplete, and
`unknown` means no file outline completed successfully.

`filterCoverage` remains the semantic filter-exhaustiveness ledger. If filtered discovery is
incomplete, `matchingFiles` and `totalEstimate.files` are lower bounds and the response suppresses
`nextCursor`; `paginationRestartRequired=true` tells callers to retry from offset zero after the
blocking work settles. This avoids a cursor that falsely implies the matching set was fully known.

### Protected-worker lifetime

FCS/MSBuild tasks are not reliably cancellable. If timeout or cancellation wins a race, the
response may return while the actual task continues, but the shared FCS gate remains retained until
that task really completes or faults. Deadline checks between options, parse, and check phases stop
later phases from starting; the project loop never starts another file after expiry/cancellation.
Public `fcs_file_outline` keeps its existing independent behavior—the deadline-aware core is used
only by `fcs_project_outline` with one pre-resolved project-options snapshot.

## fcs_file_outline

**Routing description:** Agent-friendly compact F# outline for one file. `summaryOnly=true`
(default) returns module/type headers, attributes, per-kind `memberCounts`, a bounded
CustomOperation index, and parse/check diagnostics — no per-member signatures.
`summaryOnly=false` restores full per-member
`name`/`kind`/`range`/`signature`/`accessibility`/`attributes` entries.

`attributes` is a compact array of attribute full names. Top-level `customOperationCount` is the
full count; `customOperations` decodes the operation-name constructor argument into bounded rows
with `operationName`, `memberName`, `fullName`, and source range. The rows obey `maxResults` and the
shared response-size budget. `customOperationsTruncated`,
`customOperationsTruncatedByBudget`, and `customOperationsHint` make incomplete indexes explicit.
These fields are available in summary mode, so the common lookup does not require full signatures.

Entries, CustomOperation rows, parse diagnostics, and check diagnostics share one conservative
45,000-character node budget. The fully assembled JSON is then measured with the exact serializer
used on the wire and trimmed to a hard 60,000-character ceiling. `returnedEntryCount`,
`parseDiagnosticCount`, and `checkDiagnosticCount` distinguish surfaced rows from full totals;
`entriesTruncatedByBudget`, `parseDiagnosticsTruncated`, `checkDiagnosticsTruncated`,
`responseTruncatedByBudget`, and the accompanying hints make every size cut explicit. For a file
with a very large diagnostic set, use `check(scope="file")` for the diagnostic-focused view.

### `downgradedToSummary` size guard (#206)

`summaryOnly=false` has no count-based ceiling of its own beyond `maxResults` (default 200), and a
file with many signature-heavy top-level bindings can still serialize past the shared 45,000-
character `responseCharBudget` within that count — two field-observed outlines hit 54KB and 65KB.
Before assembling the final response, a `summaryOnly=false` request whose full entries would cross
the conservative node budget is downgraded to the same header-only shape `summaryOnly=true`
produces (name/kind/fullName/range, no signatures), plus an additive `hint` explaining the
downgrade and naming `maxResults` — the narrowing knob available today — as the way to fit full
signatures in one page. The final assembled response guard also accounts for diagnostics and JSON
envelope/indentation, rather than assuming summary mode is always small. The `summaryOnly` field
in the response always echoes what was
**requested**, not what was actually returned; check the additive `downgradedToSummary: true` flag
to know the entries shape actually changed. Neither field appears when the full output already
fits the budget — a small file with `summaryOnly=false` gets the unmodified pre-#206 response.
`count` is the pre-budget definition slice capped by `maxResults`
(`count = min(definitionsInFile, maxResults)`). `returnedEntryCount` is the actual length of the
surfaced `entries` array after summary shaping and response-budget trimming; use it when consuming
the array. Lowering `maxResults` shrinks `count` on either shape, downgraded or not.
`memberCounts` is the one field that is genuinely uncapped — it is computed from the full,
untruncated definition set regardless of `maxResults` or the downgrade, so it is the field to read
for "how many of kind X does this file really have," never `count`.

### Caveats

1. **`downgradedToSummary` only fires for an explicit `summaryOnly=false` request.** A summary
   request can still set the array-level truncation fields when attributes, CustomOperation rows,
   or diagnostics consume the shared budget; both modes receive the exact final size check.
2. **A future per-type narrowing parameter (tracked separately, #157) is not implemented here.**
   Until it lands, `maxResults` is the only narrowing knob the downgrade hint can point to.
3. **The 60,000-character ceiling applies to this tool's serialized JSON response.** It is not a
   token-count promise: tokenization and any outer MCP/client envelope are client-defined.

### Related tools

- `fcs_project_outline` — whole-project structural overview; prefer `fcs_file_outline` for one
  file's structure.
- `find` (`kind="symbol"`) — raw, unfiltered, cross-file symbol search when this tool's
  local/noisy-symbol filtering or summary/budget shaping gets in the way.
- `fcs_symbol_at_word` — a single symbol at an exact position, no whole-file shaping at all.
- `fcs_public_api` — shares the same response-char-budget mechanism for a project's public
  surface.
