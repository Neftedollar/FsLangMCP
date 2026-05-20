# Detailed Tool Mechanics

This file expands on the five tool descriptions that were compressed in `Program.fs`.
The MCP description tells you *whether* to call a tool; this file tells you *how it works internally*.

---

## fcs_validate_snippet

**Routing description:** `[FCS in-process]` Compile arbitrary F# (mode="fs"|"fsi") against the
active project's references without writing to the source tree. Useful for "does this signature
type-check?" probes before scaffolding. Returns FCS diagnostics + errorCount/warningCount.
`projectPath` falls back to active `set_project`.

### How it works internally

1. Accepts `content` (F# source text) and optional `mode` ("fs" or "fsi"; default "fs").
2. Writes the content to a uniquely-named temp file in the OS temp directory (does not touch the project tree).
3. Copies the active project options (cached options are NOT mutated) and splices the temp file
   path in as an additional source file.
4. Calls `FSharpChecker.ParseAndCheckFileInProject` on that copy.
5. Deletes the temp file before returning, regardless of success or failure.
6. Returns a diagnostics payload with `errorCount`, `warningCount`, and per-diagnostic `range`,
   `message`, `severity`, and `errorNumber`.

### Caveats

1. **`.fsi` without a paired `.fs`** — an `.fsi` snippet validated with `mode="fsi"` will usually
   produce "signature has no implementation" errors because FCS expects a matching implementation
   file. To validate signature *syntax* only, use `mode="fs"` and wrap the snippet in
   `module M = ...` with the declarations inside.
2. **Compile order** — the snippet is appended as the last file in the compile order. It can
   reference any type defined earlier in the project, but it cannot reference symbols defined
   further in the project's own compile order than the snippet's notional position.
3. **No assembly emit** — no `.dll` or `.pdb` is produced. This is a typecheck-only pass.
4. **Cache isolation** — the temp-file splice uses a copied options struct; the project's own
   cached parse results are unaffected.

### Related tools

- `fcs_parse_and_check_file` — typecheck a real file already on disk.
- `fcs_check_file` — cache-invalidating recheck of a real file.

---

## fcs_nuget_types

**Routing description:** `[FCS in-process]` Enumerate all types in one referenced assembly
matched by EXACT SimpleName (case-insensitive). When a package ships multiple assemblies, call
once per name. Each entry reports `displayName`, `fullName`, `kind`, `accessibility`, `isObsolete`.
Paginated; default 500, max 2000. Returns `matchedAssemblies=[]` on no match.

### How it works internally

1. Loads the project via `FSharpChecker.ParseAndCheckProject` (warm from cache if available).
2. Iterates over all referenced assemblies in the project options.
3. Matches assemblies whose `SimpleName` equals `packageId` (case-insensitive, exact match —
   not a prefix or contains check).
4. Walks the matched assembly's top-level and nested namespaces to collect `FSharpEntity` entries.
5. Filters by accessibility (public by default; set `includeNonPublic=true` for internal/private).
6. Returns a paginated list of type entries.

### Caveats

1. **Exact SimpleName match** — `packageId="System"` matches only the `System.dll` assembly,
   not `System.Text.Json.dll`, `System.Collections.dll`, etc. To discover which assembly names
   a package publishes, run `fcs_referenced_symbols` with a partial type-name query first.
2. **Multi-assembly packages** — packages like Spectre.Console ship `Spectre.Console.dll` and
   `Spectre.Console.Cli.dll` as separate assemblies. Each requires a separate `fcs_nuget_types`
   call with the exact assembly name.
3. **Silent no-match** — when `matchedAssemblies=[]`, the tool did NOT fall back to a
   fuzzy match. The assembly is not in the project's reference list. Check `fcs_referenced_symbols`
   to see what is loaded.
4. **Lazy warm-up** — first call after `set_project` triggers `ParseAndCheckProject`, which may
   take several seconds on a large project.

### Related tools

- `fcs_referenced_symbols` — substring search across all referenced assemblies; use to discover
  assembly names before calling this tool.

---

## fcs_record_field_audit

**Routing description:** `[FCS in-process]` Find every construction site for a record field —
literal form `{ Field = expr }` AND update form `{ x with Field = expr }`. Fills the gap left by
`fcs_find_symbol`, which misses field-set uses during widening refactors. Pass `typeName`
(DisplayName e.g. `TraderRole` or segment-boundary FullName) and `fieldName` (exact,
case-sensitive). Each site reports file, range, form, and 2 context lines. Paginated; default 200,
max 1000.

### How it works internally

1. Resolves the record type via FCS by matching `FSharpEntity.DisplayName` or a
   segment-boundary suffix of `FSharpEntity.FullName` against `typeName`.
2. Locates the `FSharpField` on the matched entity by exact `Name` match against `fieldName`.
3. Calls `FSharpChecker.ParseAndCheckProject` to get the project-wide symbol-use table.
4. Walks the parsed AST for every source file using `FieldFormClassifier`, which visits
   `SynExpr.Record` nodes. It tags each field reference as `literal` (no `copyInfo`) or
   `with-update` (has `copyInfo`) based on the parse tree — not a text scan.
5. Returns paginated sites with `file`, `range` (1-based LSP), `form`, and 2 lines of source
   context.

### Caveats

1. **`form` classification** — prior to v0.8.2, form was determined by a textual `with`
   lookback heuristic that produced false positives for fields more than 2 lines below `with`.
   From v0.8.2 the classifier uses the FCS AST directly (`SynExpr.Record.copyInfo.IsSome`),
   which is accurate. The value `"unknown"` in the form field indicates a fallback path.
2. **typeName matching is segment-boundary** — `"Domain.TraderRole"` matches
   `"MyApp.Domain.TraderRole"` but NOT `"MyApp.DomainX.TraderRole"`.
3. **fieldName is exact and case-sensitive** — `"Propose"` != `"propose"`.
4. **Cursor is best-effort** — if the project source changes between pages, the offset may
   drift. Treat cursors as ephemeral for mutable codebases.

### Related tools

- `fcs_find_symbol` — find type-level uses (instantiation, type annotation, pattern match).
  Does NOT find field-set sites.
- `fcs_find_member_usages` — find member call sites on a type.

---

## fcs_find_symbol

**Routing description:** `[FCS in-process]` Project-wide symbol search with grouped
definitions/references and source-line context in one call. Better than chaining
`workspace_symbol` + `fcs_project_symbol_uses` + shell reads. Caveat: misses record-field-set
sites — use `fcs_record_field_audit` for those. `projectDiagnostics` is scoped to matched files
(scope=`matched-files`) or errors-only when zero hits (scope=`errors-only-no-matches`).
Info/Hint diagnostics filtered by default; set `includeInfo=true` to include.

### How it works internally

1. Resolves the symbol via FCS name resolution against the project's symbol table.
2. Groups results by symbol identity (same definition range = same group).
3. Fetches source-line context (`contextLines` lines around each use; default 1).
4. Returns per-group entries with `definitions` and `references` arrays, each entry including
   `file`, `range`, and `lineText`.
5. `projectDiagnostics`: when matches exist, only files containing hits are scanned for
   diagnostics (scope = `matched-files`). When zero hits, the full project is scanned for
   `Error`-severity diagnostics to detect broken projects (scope = `errors-only-no-matches`).
6. `includeDeclaration` (default true) controls whether the definition site appears in `references`.

### Caveats

1. **Record field-set sites are NOT included** — FCS symbol resolution does not surface
   `{ Field = expr }` as a reference to the type. Use `fcs_record_field_audit` for those.
2. **`symbolQuery` substring matching** — with `exact=false` (default), the query is matched
   as a substring of `DisplayName` or `FullName`. Broad queries on common names ("Create",
   "Id") can return large result sets; prefer `exact=true` for narrow targets.
3. **Diagnostic scoping** — `projectDiagnosticsScope='errors-only-no-matches'` with an empty
   `projectDiagnostics` array means the project is clean, not that diagnostics were skipped.

### Related tools

- `fcs_record_field_audit` — find record field-set construction sites.
- `fcs_project_symbol_uses` — raw symbol-use list without grouping or context lines.
- `workspace_symbol` — FSAC-backed, IDE-shaped; less suitable for agent workflows.

---

## fcs_referenced_symbols

**Routing description:** `[FCS in-process]` Search across the project's referenced assemblies
(NuGet + framework) for types by DisplayName or FullName substring (case-insensitive).
Complements `workspace_symbol` (project-local). Each result reports assembly, kind,
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
   project-local symbols, use `fcs_find_symbol` or `workspace_symbol`.
4. **Broad queries** — a single-character query like "I" can return thousands of results;
   use `maxResults` and `cursor` to page through them.

### Related tools

- `fcs_nuget_types` — enumerate all types in a specific assembly by exact SimpleName.
- `fcs_find_symbol` — search project-local symbols with source context.
- `workspace_symbol` — FSAC-backed project-local symbol lookup.

---

## fcs_check_file

**Routing description:** `[FCS in-process]` Cache-invalidating parse+typecheck for one file. Use
when `workspace_diagnostics` looks stale right after Edit/Write. Surgically drops project-options +
project-results entries for THIS project and calls `InvalidateConfiguration` before re-running.
Caveat: FCS may still serve from its internal AST cache for transitively-referenced files; fall
back to `dotnet build` for ground truth.

### How it works internally

1. Resolves the project options for the file (via `set_project`'s active project or explicit
   `projectPath`).
2. Removes the cached `FSharpProjectOptions` and `ParseAndCheckProject` results for THIS project
   from the FCS bridge's in-process caches. Other loaded projects keep their warm caches.
3. Calls `checker.InvalidateConfiguration(options)` so FCS itself drops its internal options state
   for the project.
4. Reloads options and runs `ParseAndCheckFileInProject` from a cold start.
5. Returns a diagnostics-focused payload: `errorCount`, `warningCount`, `totalDiagnostics`, and
   the per-diagnostic list.

### Caveats

1. **Transitively-referenced files still cached** — FCS keeps an internal AST cache keyed by
   file timestamp + content. A stale ancestor file in another project can leak into the check
   if its modification time hasn't changed. For absolute ground truth, run `dotnet build`.
2. **Slower than `fcs_parse_and_check_file`** — that tool reuses warm caches and is the right
   default. Reach for `fcs_check_file` only when `workspace_diagnostics` clearly disagrees with
   what you just wrote to disk.
3. **Project-scoped invalidation, not workspace-wide** — sibling projects in the same .sln
   keep their caches. If you suspect cross-project staleness, prefer `dotnet build` over
   chaining multiple `fcs_check_file` calls.

### Related tools

- `fcs_parse_and_check_file` — non-invalidating typecheck; use first.
- `workspace_diagnostics` — cached LSP payload; fast but can lag behind Edit/Write.

---

## fcs_find_member_usages

**Routing description:** `[FCS in-process]` Find every usage site of a member on a specific type
— resolves via FCS so dotted access (`x.Foo`), pipeline application, and overload resolution are
handled correctly (unlike a textual `rg`). Pass `typeName` (DisplayName e.g. `Style` or FullName
e.g. `MyApp.Theme.Style`) and `memberName`. `projectPath` is optional after `set_project`; pass
`path`/`text` for unsaved buffers.

### How it works internally

1. Resolves the containing type via FCS: by exact `FSharpEntity.DisplayName` equality or, with
   `exact=false`, by a segment-boundary suffix match of `FSharpEntity.FullName`.
2. Locates the member on the resolved entity by exact `FSharpMemberOrFunctionOrValue.DisplayName`
   match against `memberName`.
3. Calls `FSharpChecker.ParseAndCheckProject` and walks `GetAllUsesOfAllSymbols` to collect every
   usage site of the resolved member.
4. Returns per-site `file`, `range` (1-based LSP), and 2 lines of source context.

### typeName matching rules

| Setting | Matches |
|---------|---------|
| `exact=true` (default) | `FSharpEntity.DisplayName` must equal `typeName`. `Style` matches type `Style` but NOT `StyleSheet`. |
| `exact=false` | `FSharpEntity.FullName` may match `typeName` at segment boundaries. `Theme.Style` matches `MyApp.Theme.Style` but NOT `MyApp.Theme.StyleSheet`. |

### Caveats

1. **Member name is exact, case-sensitive** — `OnClick` != `onclick`. Use `fcs_find_symbol`
   for fuzzy substring search instead.
2. **Overloaded members** — all overloads sharing the same `DisplayName` are returned together.
   To narrow to one overload, post-filter the results by the surrounding `lineText`.
3. **Type extension members** — extensions defined in another module are resolved correctly,
   but their `DisplayPath` may differ from the type's own path.

### Related tools

- `fcs_find_symbol` — fuzzy substring search across the project, grouped by symbol identity.
- `fcs_project_symbol_uses` — raw symbol-use list without member-on-type scoping.

---

## fcs_make_internal_visible

**Routing description:** `[FCS in-process]` Drop the `private` keyword from a declaration at
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

## fcs_type_at_position

**Routing description:** `[FCS in-process]` Low-level exact-position FCS type/symbol query.
Requires project context (a prior `set_project` or explicit `projectPath`/`projectOptions`); the
file at `path` must exist on disk. `line`/`character` are 0-based (LSP). Prefer `fcs_symbol_at_word`
for normal agent workflows — that tool accepts a line plus word/occurrence instead of exact coords.

### How it works internally

1. Reads project options (cached after `set_project`).
2. Calls `FSharpChecker.ParseAndCheckFileInProject` on the file.
3. Asks FCS for the symbol use at `(line, character)`. Returns symbol identity, kind, type
   string, and definition range.

### Fuzzy snapping

When `fuzzy=true`, if exact `(line, character)` produces no symbol the tool snaps to the nearest
symbol within ±2 lines and ±5 columns. The response then includes:

- `resolvedLine` / `resolvedCharacter` — the snapped position actually used
- `fuzzySnap = true` — flag indicating snapping occurred

### no_symbol recovery hints

When the position has no symbol AND `fuzzy=false` (or fuzzy snapping also missed), the response
includes:

- `lineText` — the full source line at `line`
- `surroundingLines` — context above and below

This makes 1-based-vs-0-based coordinate mistakes immediately visible. If `lineText` shows the
right code but at a different column, the agent's `character` arithmetic is off.

### Caveats

1. **Project context required** — without `set_project` (or explicit options), types are often
   reported as unresolved or `obj`. Always set the project first.
2. **File must exist on disk** — this tool reads the file by path. For unsaved buffers, use
   `fcs_parse_and_check_file` with `text` instead.
3. **0-based coordinates** — LSP convention. A common bug is passing 1-based line numbers from
   editor UI; the recovery hints exist specifically to surface this.

### Related tools

- `fcs_symbol_at_word` — tolerant lookup by word; preferred for agent workflows.
- `fcs_signature_help` — overload/parameter info at a call site.
