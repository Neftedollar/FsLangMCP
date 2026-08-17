# Detailed Tool Mechanics

The MCP description tells you *whether* to call a tool; this file tells you *how it works internally*.

**Start here.** `find` and `check` are the primary entry points in the 35-tool v0.16.0 surface.
The consolidation aliases below were removed in v0.13.1 and are no longer registered:

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
rides exactly the sweep-outcome responses and names how many projects were actually swept and how
to widen/narrow (#193 — see below); every pre-sweep return carries no note. Prefer over text search
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
change it. `scopeNote` rides exactly those sweep-outcome responses; every pre-sweep return —
argument validation, `kind=position`'s own resolution failures, and a missing project context — is
note-less, because none of them reach the code that builds the note.

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
- **Coverage is never overstated**: if a project timed out or failed before analysis completed
  (`status="partial"`/`"unknown"`, `projectsAnalyzed < projectsRequested`), the note says "analyzed
  K of N" instead of "swept N" and calls out the incompleteness explicitly, instead of claiming a
  sweep happened that didn't.

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

**Why it is bounded.** Unlike `siteType`, whose 200-character cap bounds it per row, the
alternatives column grows with the number of projects a linked file is compiled by. Three limits
keep a full page inside the response ceiling, and each one is *reported*, never silent: at most 3
distinct types per row and 3 projects per type (the remainder becomes `siteTypeAlternativesOmitted`
on the row and `projectsOmitted` on the entry), and a page-wide 6000-character allowance spent in
row order — rows past it carry only `siteTypeAlternativesOmitted`, the count of other types, with no
strings. `siteTypes.alternativesTruncatedRows` counts the rows on this page that hit any of the
three, and the `siteTypesNote` says so. Narrow with `scope`/`projectPath` to see the full picture
for a contested file.

**Scope and cost.** `includeSiteTypes` annotates **field sites only** — non-field rows under
`kind='auto'` stay lean, and a `kind` that produces no field sites at all (`definition`, `symbol`,
`members`) gets a `siteTypesNote` saying so instead of silently doing nothing. Type resolution runs
inside `find`'s single `timeoutMs` budget.

**Page budget.** `siteType` is capped at 200 characters (plus a `...` marker) so a pathological
generic signature cannot blow the page. Measured growth on a real sweep is ≈19 chars per site; the
worst case is 80 × (527 + 203 + 15) ≈ 59.6k chars, still under the ~72k-char MCP ceiling, so the
default `maxResults` of 80 does **not** need lowering when the flag is on.

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
   project failed or timed out, `matched` is `null`, `outcome="indeterminate"`, and
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
never reports a stale-`{}` false-clean and you don't fall back to `dotnet build`. Optional `scope`
(`auto`|`file`|`project`|`workspace`|`snippet`), `path`, `snippet` (inline source), `speed` (trusted
default | `fast` = cached FSAC snapshot), and `severity` shape the request.

**Signature:** every argument is optional. `scope` (default `auto`) picks `snippet` when `snippet`
is set, `file` when `path` is set, else the active project (or the whole solution when it spans
>1 `.fsproj`). `path` / `snippet` / `fileGlob` / `mode` (`fs`|`fsi`) target the unit to check.
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
| `clean` | The checked unit type-checked with zero errors |
| `errors` | At least one error-severity diagnostic |
| `unknown` | The check could not run (no project context / resolution failed) — **not** clean |

| `speed` | Behaviour |
|---------|-----------|
| `trusted` (default) | Runs a FRESH FCS check; the verdict reflects the current source on disk |
| `fast` | Reads a project-bound FSAC snapshot; `clean` requires a current publication for every evaluated in-scope source file |

### How it works internally

1. Resolves `scope`: `snippet` when `snippet` is set, `file` when `path` is set, else `project`
   (escalating to `workspace` when the solution spans more than one `.fsproj`).
2. At `speed=trusted`, runs a fresh in-process FCS pass (`ParseAndCheckFileInProject` for a file,
   `ParseAndCheckProject` for a project/workspace) so the result reflects the current source — never
   a stale cached payload. At `speed=fast`, derives the expected files from evaluated FCS project
   options, then accepts only diagnostics from the matching live FSAC generation.
3. For `scope=snippet`, first scopes diagnostics to the snippet's content: wrapper artifacts
   (FS0222 missing-module, FS0225 source-file bookkeeping), diagnostics attached to other
   project files, and duplicates are removed, and `file` reads `"snippet"` — so bare
   expression code without a `module` header is valid.
4. Collapses the diagnostics into one `verdict` (`clean` / `errors` / `unknown`).
5. Returns `verdict` + `errorCount` + `warningCount` + a `diagnostics` array filtered to `severity`,
   plus `totalDiagnostics`/`infoCount`/`belowSeverityFloorCount` covering the full severity set (see
   below).

### The stale-`{}` problem it solves

Right after an `Edit`/`Write`, a cached FSAC diagnostics snapshot can be an empty `{}` while the
file actually has errors — the false-clean that historically drove agents to fall back to
`dotnet build`. At `speed=trusted`, `check` re-checks fresh in-process. At `speed=fast`, missing or
stale files now produce `unknown` with `complete=false`; a current error remains `errors` even if
the rest of the scope is incomplete.

### Caveats

1. **`unknown` ≠ `clean`** — `unknown` means the check could not run (no `set_project`, unresolved
   options). Establish project context and retry; do not treat it as a pass.
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
Each entry: `name`, `kind`, `signature`, `accessibility`, `isObsolete`, `xmlDocSummary`.
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
5. Overloaded methods appear as separate entries with distinct `signature` strings.
6. Filters by accessibility (public-only by default; `includeNonPublic=true` for private/internal).
7. Returns a paginated list with cursor mechanics identical to `fcs_nuget_types`.

### Response shape per entry

| Field | Type | Notes |
|---|---|---|
| `name` | string | `DisplayName` of the member |
| `kind` | string | `"method"`, `"property"`, `"constructor"`, `"event"`, `"field"`, `"union-case"`, `"function"` |
| `signature` | string | Formatted as `Name(param: Type, …) -> ReturnType` for methods; `Name: Type` for fields |
| `accessibility` | string | `"public"`, `"internal"`, `"private"`, `"unknown"` |
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
4. **Signature formatting** — uses FCS `BasicQualifiedName` for types; generic type parameters may
   appear as `'T` or fully-qualified names depending on the FCS representation.
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

## fcs_file_outline

**Routing description:** Agent-friendly compact F# outline for one file. `summaryOnly=true`
(default) returns module/type headers plus per-kind `memberCounts` only — no per-member
signatures — which keeps the response small on large files by construction. `summaryOnly=false`
restores full per-member `name`/`kind`/`range`/`signature`/`accessibility` entries.

### `downgradedToSummary` size guard (#206)

`summaryOnly=false` has no count-based ceiling of its own beyond `maxResults` (default 200), and a
file with many signature-heavy top-level bindings can still serialize past the shared 45,000-
character `responseCharBudget` within that count — two field-observed outlines hit 54KB and 65KB.
Rather than ever return that oversized payload, a `summaryOnly=false` request whose full
entries would cross the budget is downgraded to the same header-only shape `summaryOnly=true`
produces (name/kind/fullName/range, no signatures), plus an additive `hint` explaining the
downgrade and naming `maxResults` — the narrowing knob available today — as the way to fit full
signatures in one page. The `summaryOnly` field in the response always echoes what was
**requested**, not what was actually returned; check the additive `downgradedToSummary: true` flag
to know the entries shape actually changed. Neither field appears when the full output already
fits the budget — a small file with `summaryOnly=false` gets the unmodified pre-#206 response.
`count` is the length of the returned `entries` slice — it is capped by `maxResults` exactly like
`entries` itself (`count = min(definitionsInFile, maxResults)`), and unaffected only by the
downgrade: lowering `maxResults` shrinks `count` on either shape, downgraded or not.
`memberCounts` is the one field that is genuinely uncapped — it is computed from the full,
untruncated definition set regardless of `maxResults` or the downgrade, so it is the field to read
for "how many of kind X does this file really have," never `count`.

### Caveats

1. **`downgradedToSummary` only fires for an explicit `summaryOnly=false` request.**
   `summaryOnly=true` is already small by construction and is never measured against the budget.
2. **A future per-type narrowing parameter (tracked separately, #157) is not implemented here.**
   Until it lands, `maxResults` is the only narrowing knob the downgrade hint can point to.
3. **The MCP client may still spill an oversized response to a side file, with a client-defined
   shape** — same caveat as `fcs_public_api` above.

### Related tools

- `fcs_project_outline` — whole-project structural overview; prefer `fcs_file_outline` for one
  file's structure.
- `find` (`kind="symbol"`) — raw, unfiltered, cross-file symbol search when this tool's
  local/noisy-symbol filtering or summary/budget shaping gets in the way.
- `fcs_symbol_at_word` — a single symbol at an exact position, no whole-file shaping at all.
- `fcs_public_api` — shares the same response-char-budget mechanism for a project's public
  surface.
