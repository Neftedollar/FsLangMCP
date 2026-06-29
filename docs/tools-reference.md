# Tools Reference

FsLangMCP exposes 35 tools over MCP stdio, grouped below by intent. Start with `find` and `check` — they cover the two most common agent questions ("where is X?" and "did my edit compile?"). The other 33 tools provide deeper access to specific compiler, LSP, and project capabilities.

All positions (`line`, `character`) are **0-based**. `projectPath` is optional on most tools after `set_project` — it falls back to the active project.

---

## Headline tools

These two tools supersede a cluster of lower-level primitives. Reach for them first.

### `find`

**Purpose:** Multi-project semantic search — definitions, references, record-field set sites, and member call-sites — resolved by FCS, unioned across every solution `.fsproj`.

**Key args:**
- `query` (required) — symbol name, dotted suffix, or qualified name
- `kind` — `symbol` | `members` | `field` | `definition` | `position` (default: `symbol`)
- `member` — narrow to a specific member name when `kind=members`
- `scope` — narrow to a file or project
- `contextLines` — include surrounding source lines in the response

**Use when:** "Where is `X` defined?", "What calls `OrderId`?", "Which files set this record field?"

---

### `check`

**Purpose:** One trustworthy compile verdict — `clean` | `errors` | `unknown` — from a fresh in-process type-check. Never returns a stale-cache false-clean.

**Key args:**
- `scope` — `auto` | `file` | `project` | `workspace` | `snippet` (default: `auto`)
- `path` — file path when `scope=file`
- `snippet` — inline source when `scope=snippet`
- `speed` — `trusted default` (fresh check) | `fast` (cached FSAC snapshot)
- `severity` — filter results by severity level

**Use when:** "Did my edit compile?", "Are there errors in this file?", "Is the workspace clean?"

---

## Navigate / understand

### `set_project`

**Purpose:** Initialize or switch the FSAC/LSP project context. Must be called before `textDocument_*` and `workspace_*` tools.

**Key args:**
- `projectPath` (required) — `.fsproj`, `.sln`, `.slnx`, or directory path

**Response includes:** `loadedProjects`, `readiness` (`lsp` / `projectOptions` / `symbolIndex` flags), `fslangmcpVersion`.

**Use when:** Starting a session or switching to a different project. Call once; context persists.

---

### `project_health`

**Purpose:** Fast read-only preflight for one F# project. Reports FCS trust status, project options availability, source file readability, analyzer setup, test project discovery, and current LSP readiness. Does not start FSAC, build, or run tests.

**Key args:**
- `projectPath` — optional after `set_project`

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

**Purpose:** Compact F# outline for one file. `summaryOnly=true` (default) returns module/type headers and per-kind member counts, keeping token cost low.

**Key args:**
- `path` (required) — absolute path to `.fs` file
- `summaryOnly` — `true` (default) for headers+counts; `false` for full name/kind/range/signature entries

**Use when:** Understanding a single file's structure before editing it.

---

### `fsharp_project_inspect`

**Purpose:** Read-only `.fsproj` inspection with MSBuild evaluation. Returns project identity, compile order, package/project references, and signature/implementation pairing. Does not build, restore, or edit files.

**Key args:**
- `projectPath` — optional after `set_project`

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
- `symbol` (required) — the unresolved name
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
- `symbol` (required) — symbol name to look for in test files
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

---

### `fcs_public_api`

**Purpose:** Emit the project's full public API surface — every public type and member with signatures — sorted stably by `fullName` then member name. Two snapshots diff cleanly.

**Key args:**
- `projectPath` — optional after `set_project`
- `includeInternal` — add internal symbols
- `namespaceFilter` — substring filter on `FullName`
- `maxResults` / `cursor` — pagination

**Use when:** API-stability diffs, breaking-change detection, or generating a changelog. Prefer over `fcs_project_outline` for API work.

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

**Purpose:** Enumerate all types in one referenced assembly matched by exact `SimpleName` (case-insensitive). Returns display name, full name, kind, accessibility, and obsolete status.

**Key args:**
- `assemblyName` (required) — exact simple name (e.g. `"Spectre.Console"`)
- `maxResults` / `cursor` — pagination (default 500, max 2000)

**Use when:** Discovering what types a specific NuGet package exposes. Note: `Spectre.Console` resolves only to that assembly, not `Spectre.Console.Cli` — call once per assembly.

---

### `fcs_nuget_members`

**Purpose:** Enumerate members of one type from a referenced assembly. Returns name, kind, signature, accessibility, obsolete status, and XML doc summary.

**Key args:**
- `packageId` (required) — assembly simple name
- `typeName` (required) — type name to enumerate
- `maxResults` / `cursor` — pagination (default 500, max 2000)

**Use when:** Inspecting a specific type's API after `fcs_nuget_types` identified it.

---

### `fcs_referenced_symbols`

**Purpose:** Substring search across all referenced assemblies (NuGet + framework) by `DisplayName` or `FullName`. Paginated. First call triggers `ParseAndCheckProject`.

**Key args:**
- `query` (required) — substring to search
- `includeNonPublic` — include non-public symbols
- `maxResults` — default 200, max 1000

**Use when:** "What assemblies define something like `IMemoryCache`?" — cross-assembly search when you don't know the exact assembly. Prefer `fcs_nuget_types` when you already know the assembly name.

---

## Raw LSP proxies

These tools are direct proxies to fsautocomplete LSP messages. They require `set_project` first and an exact 0-based position. Prefer the semantic tools above for agent flows; use these for exact-position editor operations and FSAC debugging.

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

**Purpose:** Read-only snapshot of the FsLangMCP process runtime state: managed-heap sizes by generation/LOH/POH, GC collection counts, `isServerGC` flag, assembly load count, FCS checker configuration flags and project-results cache size, and the FSAC child-process working set.

**Use when:** Memory growth monitoring during long multi-agent sessions. Never triggers a GC collection or walks the heap.
