# Tools Reference

FsLangMCP exposes 35 tools over MCP stdio, grouped below by intent. Start with `find` and `check` — they cover the two most common agent questions ("where is X?" and "did my edit compile?"). The other 33 tools provide deeper access to specific compiler, LSP, and project capabilities.

All positions (`line`, `character`) are **0-based**. `projectPath` is optional on most tools after `set_project` — it falls back to the active project.

Arguments are **strictly typed JSON** (since v0.15.0): string-encoded scalars such as `"42"` or `"true"` are not coerced into numeric or boolean fields and fail with a structured invalid-arguments error. Send `42` and `true`, not their quoted forms.

---

## Headline tools

These two tools replace the legacy search/check entry points removed in v0.13.1. Reach for them first.

### `find`

**Purpose:** Multi-project semantic search — definitions, references, record-field set sites, and member call-sites — resolved by FCS, unioned across every solution `.fsproj`.

**Key args:**
- `query` (required) — symbol name, dotted suffix, or qualified name
- `kind` — `auto` | `symbol` | `members` | `field` | `definition` | `position` (default: `auto`; unions symbol/member/field sites)
- `member` — narrow to a specific member name when `kind=members`
- `scope` — `auto` | `file` | `project` | `workspace` (default: `auto`); file requires `path`, project requires one member `.fsproj`
- `contextLines` — non-negative surrounding source-line count (default: `0`)
- `includeSiteTypes` — add `siteType` (the field's type as resolved TODAY) to every record-field row (default: `false`)
- `maxResults` — page size from 1 to 1000 (default: `80`)
- `timeoutMs` — non-negative whole-sweep budget; `0` returns an immediate typed timeout (default: `120000`)

**Use when:** "Where is `X` defined?", "What calls `OrderId`?", "Which files set this record field?"

**Sweep breadth:** every response that completes a sweep carries `resolution.scopeResolved`,
top-level `projectsSwept`, and a `scopeNote` explaining how many projects were actually swept
and the exact recipe to widen (`scope='workspace'` with a `.sln`/`.slnx` `projectPath`) or narrow
(a single `.fsproj` `projectPath`) it — so you don't pay for a whole-workspace sweep just to
discover which recipe would have been faster. See `docs/tools-detailed.md`.

**Field-impact mode:** `kind=field` classifies each site as `field-set-literal` | `field-set-update`
| `field-set-mutation` | `field-pattern` | `field-read` — the five shapes a field-type change edits
differently. Add `includeSiteTypes=true` for a `siteType` column carrying the field's type as the
CURRENT typecheck resolves it at that site, plus a `siteTypes` ledger (`typed + degraded ==
fieldSites`). It never predicts the post-edit type: edit the sites, then run `check`. A site
compiled by several swept projects that resolve it differently keeps the first project's answer in
`siteType`/`project` and lists the rest in `siteTypeAlternatives`, each entry naming the type and the
projects that resolved it (counted by `siteTypes.typedDifferentlyByAnotherProject`, a subset of
`typed`). That column is capped per row and per page; anything left out is reported as
`siteTypeAlternativesOmitted` / `projectsOmitted` / `siteTypes.alternativesTruncatedRows`, never
dropped silently. See `docs/tools-detailed.md`.

**Changed in v0.16.0:** `field-set-mutation` (`x.Field <- v`) and `field-pattern`
(`| { Field = x } ->`) are new kinds — both used to be reported as `field-read`, mislabeling a
write as a read. `fieldRead` counts in `breakdown` drop accordingly; the new
`fieldSetMutation` / `fieldPattern` counters make up the difference.

**Absence contract:** inspect `outcome` and `coverage.complete`. A complete miss returns
`outcome="not_found"` and `resolution.matched=false`. A failed/timed-out project makes absence
indeterminate (`status="unknown"`, `outcome="indeterminate"`, `matched=null`); positive results
from an incomplete sweep return `status="partial"`.

---

### `check`

**Purpose:** One trustworthy compile verdict — `clean` | `errors` | `unknown`. The default uses a
fresh in-process type-check; fast mode requires complete, current, project-bound FSAC coverage
before it can return `clean`.

**Key args:**
- `scope` — `auto` | `file` | `project` | `workspace` | `snippet` (default: `auto`)
- `path` — file path when `scope=file`
- `snippet` — inline source when `scope=snippet`
- `speed` — `trusted` (default; fresh check) | `fast` (cached FSAC snapshot)
- `severity` — filter results by severity level

**Use when:** "Did my edit compile?", "Are there errors in this file?", "Is the workspace clean?"

**Snippet scope:** diagnostics describe the snippet's *content* only. Bare expression code
without a `module` header is valid — the missing-module FS0222 and source-file-bookkeeping
FS0225 are wrapper artifacts and are filtered out, along with diagnostics belonging to other
project files. Each surviving diagnostic reports `file: "snippet"` (the caller sent text, not
a file), and duplicates are collapsed.

In `speed=fast`, inspect `complete`, `expectedFiles`, `missingFiles`, `staleFiles`, and
`sessionGeneration`. Current errors remain actionable with `complete=false`; an incomplete
zero-error snapshot is `unknown`, never `clean`.

**Project-scope coverage boundary:** a resolved `scope=project` response always includes
`downstreamProjectsChecked: false`, `recommendedScope: "workspace"`, and `coverageNote`.
A clean project verdict says only that the selected project is clean; apps/tests that reference
it were not analyzed. Use `scope=workspace` with a solution or directory `projectPath` to cover
those consumers. `scope=workspace` with a single `.fsproj` is rejected rather than returning a
misleading one-project workspace verdict. The tool deliberately does not start another MSBuild
sweep merely to count downstream projects.

**Diagnostic counts:** `totalDiagnostics = errorCount + warningCount + infoCount`, always —
`infoCount` tallies the Info/Hidden diagnostics in the full set, and `belowSeverityFloorCount`
(plus a `diagnosticsNote` when nonzero) explains how many of them the `severity` floor excluded
from the `diagnostics` array. `scope='workspace'` carries the same `infoCount` on every
**successfully analyzed** `perProject[]` entry, not just the workspace total — a timed-out,
unrestored, or errored entry carries no counts at all.

---

## Navigate / understand

### `set_project`

**Purpose:** Initialize or switch the FSAC/LSP project context. Must be called before raw LSP-proxy tools.

**Key args:**
- `projectPath` (required) — `.fsproj`, `.sln`, `.slnx`, or directory path; a directory recursively selects one candidate and reports ambiguity for multiple candidates
- `restartLsp` — request an FSAC restart (default `true`)

**Response includes:** `loadedProjects`, `readiness` (`lsp` / `projectOptions` / `symbolIndex` flags plus `symbolIndexState` / `symbolIndexHint`), `lspLifecycleState`, `sessionGeneration`, `lspRestartRequested`, legacy `lspRestarted`, `lspReplacedExistingProcess`, and `fslangmcpVersion`. `lspRestarted` keeps mirroring the request for compatibility; `lspReplacedExistingProcess=true` means a pre-existing FSAC process was actually replaced, so first launch reports `false` there. Switching to a different context with `restartLsp=false` while FSAC is live returns `status="restart_required"` and leaves the active context unchanged.

`symbolIndexState` includes a `not_warmed` state — the warm-up window elapsed with no warm signal *observed*, which is not proof the index failed to warm (`check` and every FCS-derived `find` site are unaffected either way; `find`'s zero-hit fallback IS the symbol index, so read its `fsacFallbackState` before reading a zero-hit `find` as absence — see `docs/tools-detailed.md`). On an unsatisfiable `global.json` SDK pin (`rollForward: "disable"` pinning a version not installed), `set_project` instead returns a typed `{status: "infrastructure_error", errorKind: "sdk_not_found", ...}` envelope carrying the requested version, the `global.json` path, and the installed SDK list — not a readiness payload.

**Use when:** Starting a session or switching to a different project. Call once; context persists.

---

### `project_health`

**Purpose:** Fast read-only preflight for one F# project. Reports FCS trust status, project options availability, source file readability, analyzer setup, test project discovery, and current LSP readiness. Project files, properties, package/project references, and imported configuration come from the same MSBuild-evaluated ProjInfo snapshot used by project inspection, so SDK default items, `Condition`, and `Directory.Build.*` imports are applied. Does not start FSAC, build, or run tests.

**Key args:**
- `projectPath` — optional after `set_project`

The `evaluation` object identifies the evaluated source and restore state. LSP readiness is context-bound: a live, ready session for project A is not reported as ready while inspecting unrelated project B; use `workspace.lspContextMatched` and `workspace.lspLoadedProjects` to diagnose that case.

**Test-discovery honesty:** reverse test discovery evaluates every candidate `.fsproj` under the workspace root, and a candidate that fails to evaluate (commonly `project evaluation busy` — MSBuild evaluation is admitted one project at a time) is no longer dropped. `tests.discoveryComplete` says whether every candidate was read; failures are listed in `tests.unevaluatedProjects` (with `tests.unevaluatedProjectCount`), and a sweep that found nothing while some candidate failed reports `tests.status="test_discovery_incomplete"` rather than `no_test_projects_found`. Read `no_test_projects_found` as conclusive only with `discoveryComplete: true`.

**Use when:** Diagnosing why FCS tools return incomplete data, or before starting a long agent run.

---

### `fcs_project_outline`

**Purpose:** Agent-friendly compact outline over all filtered compile files in the project. Skips generated/build artifacts.

**Key args:**
- `projectPath` — optional after `set_project`
- `maxFiles` / `maxResultsPerFile` — cap output on large projects

**Use when:** Getting a structural overview of the whole project before editing or reviewing it.

---

### `fcs_file_outline`

**Purpose:** Compact F# outline for one file. `summaryOnly=true` (default) returns module/type headers, attributes, per-kind member counts, a bounded CustomOperation index, and bounded parse/check diagnostics while keeping token cost low. Full counts and explicit array/budget truncation fields distinguish complete from partial output; the fully serialized response is capped at 60,000 characters.

**Key args:**
- `path` (required) — absolute path to `.fs` file
- `summaryOnly` — `true` (default) for headers+counts+attributes; `false` for full name/kind/range/signature/accessibility/attributes entries

**Use when:** Understanding a single file's structure before editing it.

**`downgradedToSummary`:** a `summaryOnly=false` request whose full per-member entries would
cross the same response-size budget `fcs_public_api` uses is downgraded to the header-only
shape instead of ever returning the oversized payload, flagged by `downgradedToSummary: true`
plus a `hint` naming `maxResults` as the narrowing knob. `summaryOnly` in the response always
echoes what was requested; check `downgradedToSummary` for what was actually returned. The final
size guard also accounts for CustomOperation rows, parse/check diagnostics, and the response
envelope; `responseTruncatedByBudget` plus per-array fields report any additional cuts.

---

### `fsharp_project_inspect`

**Purpose:** Read-only project inspection backed by an evaluated ProjInfo/MSBuild snapshot. Returns project identity, SDK-default and imported compile items in compiler order, conditional/imported package and project references, and signature/implementation pairing. Raw XML is not treated as the effective project model. Does not build, restore, or edit files.

**Key args:**
- `projectPath` — optional after `set_project`
- `includeGeneratedFiles` — include build-generated sources (`*.AssemblyInfo.fs`, the SDK's
  `*.AssemblyAttributes.fs` stub) in the compile order (default `false`). When excluded, they
  are counted in `filterSummary.exclusionsByReason` under `assembly_info_file`, so the
  exclusion names what the file is — and which flag surfaces it.

The response includes `evaluation.status`, `evaluation.source`, `evaluation.imports`, and per-section `evaluationSource` metadata. An evaluation failure is explicit (`evaluation.status = "unavailable"`) instead of silently falling back to an incomplete XML interpretation.

**Use when:** Checking compile order, package references, or `.fsi` pairing without reading raw XML.

---

### `fcs_symbol_at_word`

**Purpose:** Tolerant FCS symbol lookup by line plus word/occurrence. Returns symbol identity, kind, type string, definition range, and optional documentation. Does not require an exact cursor column.

**Key args:**
- `path` (required) — absolute path to `.fs` file
- `line` (required) — 0-based line number
- `word` (required) — the word/identifier to look up
- `occurrence` — which occurrence on the line (1-based, default 1)

**Use when:** Inspecting what a specific identifier means without knowing its exact column.

---

### `fcs_get_project_options`

**Purpose:** Diagnostic helper to retrieve FSharp compiler `OtherOptions` for a `.fsproj` via proj-info.

**Key args:**
- `projectPath` — optional after `set_project`

**Use when:** Debugging compilation flag issues or verifying what compiler options are in effect.

---

## Diagnose / fix

### `fcs_explain_diagnostic`

**Purpose:** Explain a compiler diagnostic in plain language with repair context: title, explanation, likely causes, repair hints, and related tools. Curated for ~25 common diagnostics.

**Key args:**
- `code` — diagnostic code string (`"FS0039"`)
- `errorNumber` — numeric code (39) — alternative to `code`
- `path` + `line` + `character` — auto-fetch the diagnostic via FCS (alternative to code)
- `message` — raw error message to enrich hints

**Use when:** `check` returns an FS error you need to turn into a fix. Feed it `check`'s `errorNumberText`. Unknown codes return `status=unknown_code`.

---

### `fcs_diagnostic_fixes`

**Purpose:** Fetch a file's diagnostics, then request code-action fixes for each and group them per diagnostic: range, severity, code, message, and available fixes with titles and edit summaries.

**Key args:**
- `path` (required) — absolute path to the file
- `line` / `character` — narrow to one position (otherwise all diagnostics)
- `text` — pass unsaved buffer content

**Use when:** Asking "what fixes are available for this file's errors?" in one call instead of round-tripping through raw LSP.

---

### `fcs_check_compile_order`

**Purpose:** Detect when FS0039 "not defined" is a compile-order problem — a symbol used in a file that appears *before* the defining file in `<Compile>` order. Returns `{ symbol, definedIn, usedIn, fix }`.

**Key args:**
- `projectPath` — optional after `set_project`
- `symbol` — narrow to one symbol name

**Use when:** `check` reports FS0039 and `fcs_suggest_open` finds no missing `open`. This distinguishes a compile-order problem from a namespace problem.

---

### `fcs_suggest_open`

**Purpose:** Given an unresolved symbol name (FS0039), return ranked `open` directive candidates — project-local first, then referenced assemblies.

**Key args:**
- `symbolName` (required) — the unresolved name
- `includeReferences` — include referenced assemblies (default `true`)

**Use when:** `check` reports "X is not defined" and you need the right namespace or module to open.

---

## Refactor preview

### `fcs_rename_preview`

**Purpose:** Preview a semantic rename's full impact without writing anything. Returns edits grouped by file with `originalLineText` / `previewLineText`, plus `totalEdits`, `fileCount`, and a `crossProject` flag.

**Key args:**
- `path` (required) — file containing the symbol to rename
- `line` / `character` (required, 0-based) — position of the symbol
- `newName` (required) — the new name
- `text` — pass unsaved buffer content

**Use when:** Checking blast radius before committing a rename. Use `textDocument_rename` to apply the actual change.

---

### `fcs_refactor_impact`

**Purpose:** Full blast-radius preview — uses, tests, compile order (for moves), public API surface (for signature/delete changes) — orchestrated in one call into `{ target, impact, tests, compileOrder?, apiSurface?, verify[] }`.

**Key args:**
- `symbol` — symbol name, OR `path` + `line` + `character` for exact position
- `kind` — `rename` | `signature` | `move` | `delete` | `auto`

**Use when:** Before any rename, move, or delete. Gives the project-wide picture; use `fcs_rename_preview` for the exact edits.

---

### `fcs_make_internal_visible`

**Purpose:** Drop the `private` keyword from a declaration at a position. Returns a non-destructive workspace edit `{ status, edits, appliedPreview, originalLineText }` — does not write the file.

**Key args:**
- `path` (required)
- `line` / `character` (required, 0-based)

**Use when:** Tests need to call internal members. Apply the returned edit, then verify with `check`.

---

### `fcs_tests_for_symbol`

**Purpose:** List tests that likely cover a symbol. Sweeps test projects (detected via `<IsTestProject>` or xunit/nunit/expecto refs), filters FCS symbol uses to test files, and tags each with its enclosing test name.

**Key args:**
- `symbolQuery` (required) — symbol name to look for in test files
- `projectPath` — optional after `set_project`

**Use when:** "What tests cover this function?" — gives the test-coverage slice that `find` (which returns all uses) doesn't directly filter.

---

## Review / cleanup

### `fcs_dead_code`

**Purpose:** List likely-unused `private` / `internal` bindings as cleanup candidates. Sweeps the project via `GetAllUsesOfAllSymbols` and flags symbols whose only use is their own definition. Public symbols excluded by default.

**Key args:**
- `projectPath` — optional after `set_project`
- `includePublic` — extend scan to public symbols

**Use when:** Identifying dead code before a cleanup pass. Always verify candidates with `find` before removing.

---

### `fcs_review_scan`

**Purpose:** Scan source for AST-level review candidates — interesting spots to eyeball, not a linter. Categories: `match_wildcard`, `try_with`, `raise_or_failwith`, `mutable_binding`, `blocking_call`, `cast_or_box`, `reflection`, `large_function`.

**Key args:**
- `path` — one file, OR `projectPath` — whole project (falls back to `set_project`)
- `categories` — narrow to specific categories
- `maxResults` — cap output

**Use when:** Code review or audit passes. Parse-only, writes nothing.

**Coverage honesty:** in project mode, in-scope compiled files that cannot be found on disk are listed in `unresolvedFiles` and downgrade `status` from `succeeded` to `partial` — never trust a scan's coverage without checking `status` and `scanned`. Filter-excluded entries (generated, obj/bin, tests) are not counted: their absence is expected pre-build; use `project_health` (`files.missingFiles`) for unfiltered fsproj hygiene.

---

### `fcs_public_api`

**Purpose:** Emit the project's full public API surface — every public type and member with signatures — sorted stably by `fullName` then member name. Two snapshots diff cleanly.

**Key args:**
- `projectPath` — optional after `set_project`
- `includeInternal` — add internal symbols
- `namespaceFilter` — substring filter on `FullName`
- `maxResults` / `cursor` — pagination

**Use when:** API-stability diffs, breaking-change detection, or generating a changelog. Prefer over `fcs_project_outline` for API work.

**Page budget:** a page also closes on a shared response-size budget, not `maxResults` alone —
API-dense types (long member lists) can cross it before the count cap does. `truncatedByBudget:
true` flags a budget-close specifically; either close reason adds a `hint` naming
`namespaceFilter` with real remainder namespaces to narrow the next call. See
`docs/tools-detailed.md`.

---

### `fcs_signature_status`

**Purpose:** Report `.fsi`-vs-impl drift for one `.fs` file: members public in the impl but missing from the `.fsi` (`missingFromSig`) and `.fsi` entries with no impl match (`staleInSig`), each with a signature preview.

**Key args:**
- `path` (required) — the `.fs` implementation file
- `projectPath` — optional after `set_project`

**Use when:** Maintaining `.fsi` signature files; detecting silently hidden members.

---

### `fcs_create_file_plan`

**Purpose:** Plan WHERE a new `.fs` file belongs in compile order, without creating it. Recommends an insertion index, infers the namespace/module convention from neighbours, and emits the exact `<Compile Include=...>` edit plus a dependency note.

**Key args:**
- `fileName` (required) — the proposed new file name
- `projectPath` — optional after `set_project`

**Use when:** Adding a new source file. Pair with `fcs_check_compile_order` after adding the file.

---

## Analyzers

### `fcs_analyzer_diagnostics`

**Purpose:** Report F# analyzer diagnostics (not compiler diagnostics), grouped by analyzer and severity. Runs the `fsharp-analyzers` CLI when available and parses its SARIF output.

**Key args:**
- `projectPath` — optional after `set_project`
- `severity` — filter by severity

**Use when:** Reading analyzer findings after wiring up analyzers. `project_health` reports whether analyzers are configured; this reports the actual diagnostics.

---

### `fcs_analyzer_setup_preview`

**Purpose:** Plan what to add to enable F# analyzers — analyzer package refs, `GeneratePathProperty`, `FSharp.Analyzers.Build`, `FSharpAnalyzersOtherFlags`, local manifest — without applying anything.

**Key args:**
- `projectPath` — optional after `set_project`

**Use when:** Setting up analyzers from scratch. Pair with `fcs_analyzer_diagnostics` to read diagnostics after applying the plan.

---

## NuGet / types

### `fcs_nuget_types`

**Purpose:** Enumerate all types in one referenced assembly. Returns display name, full name, kind, accessibility, and obsolete status.

**Key args:**
- `packageId` (required) — the NuGet package id OR an assembly simple name it ships, matched exactly and case-insensitively (e.g. `"Spectre.Console"`, or `"Microsoft.Orleans.Core.Abstractions"` for the assembly `Orleans.Core.Abstractions`)
- `maxResults` / `cursor` — pagination (default 500, max 2000)

**Use when:** Discovering what types a specific NuGet package exposes. A package whose assembly is named differently resolves under either spelling, and a package shipping several assemblies returns all of them in one call (#191). On a miss the response carries `hint` + `candidatePackages`. Prefix matching is still rejected: `Spectre.Console` never resolves to `Spectre.Console.Cli`.

---

### `fcs_nuget_members`

**Purpose:** Enumerate members of one type from a referenced assembly. Returns name, kind, signature (including generic constraints), exact metadata accessibility when available, abstractness, structured generic parameters, obsolete status, and XML doc summary.

**Key args:**
- `packageId` (required) — the NuGet package id OR an assembly simple name it ships (same matching as `fcs_nuget_types`)
- `typeName` (required) — type name to enumerate
- `maxResults` / `cursor` — pagination (default 500, max 2000)

**Use when:** Inspecting a specific type's API after `fcs_nuget_types` identified it.

Protected members remain in the default externally visible surface. Set
`includeNonPublic=true` for internal/private rows. Each method row includes
`isAbstract` and `genericParameters: [{ name, constraints }]`; the same constraints
are appended to `signature` (for example `where T : class, new()`). Metadata is read
without loading or executing the target assembly; exact accessibility and abstractness
come from ECMA-335 when the row is unambiguous. Otherwise they fall back to FCS's
source-language/dispatch-slot view.

---

### `fcs_referenced_symbols`

**Purpose:** Substring search across all referenced assemblies (NuGet + framework) by `DisplayName` or `FullName`. Paginated. First call triggers `ParseAndCheckProject`.

**Key args:**
- `query` (required) — substring to search
- `includeNonPublic` — include non-public symbols
- `maxResults` — default 200, max 1000

**Use when:** "What assemblies define something like `IMemoryCache`?" — cross-assembly search when you don't know the exact assembly. Prefer `fcs_nuget_types` when you already know the package id or the assembly name.

---

## Exact-position helpers

The `textDocument_*` tools and `fsharp_signature_data` are direct proxies to
fsautocomplete and require `set_project` plus LSP readiness. `fcs_signature_help`
runs in-process and instead needs FCS project context via `projectPath`,
`projectOptions`, or a source path inside a project. Prefer the semantic tools
above for free-form agent flows.

### `textDocument_completion`

**Purpose:** Raw LSP `textDocument/completion` at an exact position.

**Key args:** `path`, `line`, `character` (0-based), `text` (unsaved content)

**Use when:** Exact-position IDE completion. For symbol semantics, use `fcs_symbol_at_word` instead.

---

### `textDocument_formatting`

**Purpose:** Raw LSP formatting via Fantomas. Returns formatted text and edits; does not write to disk.

**Key args:** `path`, `text` (unsaved content)

**Use when:** Formatting a file or buffer via Fantomas through the LSP layer.

---

### `textDocument_codeAction`

**Purpose:** Raw LSP `codeAction` at an exact position with empty diagnostic context. Useful for debugging FSAC.

**Key args:** `path`, `line`, `character` (0-based), `text` (unsaved content)

**Use when:** FSAC debugging. For agent repair flows, prefer `fcs_diagnostic_fixes` which supplies diagnostic context automatically.

---

### `textDocument_rename`

**Purpose:** Raw LSP semantic rename at an exact position. Returns a raw `WorkspaceEdit`.

**Key args:** `path`, `line`, `character` (0-based), `newName`, `text` (unsaved content)

**Use when:** Applying a rename after `fcs_rename_preview` confirms the blast radius. Handles shadowing and aliased opens safely.

---

### `fcs_signature_help`

**Purpose:** Exact-position FCS signature help around a call site. Returns overloads and parameters.

**Key args:** `path`, `line`, `character` (0-based), `text` (unsaved content), `projectPath` / `projectOptions`

**Use when:** Low-level call-site signature inspection when FSAC's view isn't sufficient.

---

### `fsharp_signature_data`

**Purpose:** Structured FSAC `fsharp/signatureData` at an exact call-site position.

**Key args:** `path`, `line`, `character` (0-based)

**Use when:** Validating FSAC's current workspace view of a call site's signature. Requires `set_project` and `readiness.lsp=true`.

---

## Meta

### `fslangmcp_version`

**Purpose:** Returns the installed FsLangMCP product version and name. Zero-arg (`{}`). No project context required; no side effects.

**Use when:** Filing UX feedback (include the version so reports can be matched to a release). Also surfaced in `set_project` and `fsharp_runtime_status` responses.

---

### `fsharp_runtime_status`

**Purpose:** Read-only snapshot of the FsLangMCP process runtime state: managed-heap sizes by generation/LOH/POH, GC collection counts, `isServerGC` flag, assembly load count, FCS checker/cache state, project-options load/reload telemetry, FSAC child-process working set, and `process.threads` (OS process/ThreadPool counts plus pending/completed work items).

`fcs.projectOptions` distinguishes all load attempts from true stale-cache reloads,
cache validations, and in-flight evaluations. At an OS-visible thread count of 128 or
more, `process.threads.health` becomes `warning` and recommends restarting the parent
MCP process. This is a safety signal, not proof of a leak: retained ProjInfo/MSBuild
nodes are one known cause, but thread count alone cannot identify ownership.

**Use when:** Memory/thread growth monitoring during long multi-agent sessions. Never triggers a GC collection, walks the heap, suspends threads, or forces a restart.
