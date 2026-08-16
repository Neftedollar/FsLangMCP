# Detailed Tool Mechanics

The MCP description tells you *whether* to call a tool; this file tells you *how it works internally*.

**Start here.** `find` and `check` are the primary entry points in the 35-tool v0.15.0 surface.
The consolidation aliases below were removed in v0.13.1 and are no longer registered:

| Removed names | Current route |
|---|---|
| `workspace_symbol`, `fcs_find_symbol`, `fcs_project_symbol_uses`, `fcs_find_member_usages`, `fcs_record_field_audit`, `textDocument_references`, `textDocument_definition` | `find` (`kind` selects symbol/member/field/definition/position behavior) |
| `workspace_diagnostics`, `fsharp_compile`, `fcs_check_file`, `fcs_parse_and_check_file`, `fcs_validate_snippet` | `check` (`scope` selects file/project/workspace/snippet behavior) |
| `fcs_type_at_position` | `fcs_symbol_at_word`, or `find(kind="position")` when exact coordinates matter |
| `fcs_file_symbols` | `fcs_file_outline` |

---

## find

**Routing description:** Multi-project symbol search. Sweeps every member `.fsproj` of the
solution and unions definitions, references, record-field set sites, and member-usage sites.
Bare `find(query)` suffices; optional `kind`
(`auto`|`symbol`|`members`|`field`|`definition`|`position`) and `scope` narrow it. Every response
carries a top-level `scopeNote` naming how many projects were actually swept and how to
widen/narrow (#193 — see below). Prefer over text search for cross-project refactors.

**Signature:** `query` is the only required argument. `kind` (default `auto`) and `scope` (default
`auto`) shape the sweep. `exact` (default `true`) toggles exact-vs-substring matching. `member` /
`field` restrict the member-usage / record-field unions. `path` + `line` + `word` + `occurrence` +
`character` anchor `kind=position`. `contextLines` (default 0), `includeDeclaration` (default true),
`includeInfo` (default false), `projectPath` (falls back to active `set_project`), `maxResults`
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
What's new is that **every successful response now carries a top-level `scopeNote`** reporting the
real outcome (driven by `projectsSwept`, not by which `scope` string was requested) and the recipe
to change it:

- **One project swept** — however the sweep target arrived (an explicit `.fsproj` `projectPath`, a
  `path`-derived fallback, or a solution/directory that itself has only one member project): the
  note warns that cross-project usages in sibling projects are not visible, and names the actual
  widening recipe — pass the solution's `.sln`/`.slnx` as `projectPath` (or `set_project` it) with
  `scope='workspace'`. Requesting `scope='workspace'` alone, against an `.fsproj` `projectPath`,
  does **not** widen anything: `SolutionParsing.listProjects` on a bare `.fsproj` is always
  `[itself]`, so once the sweep target is a single project, no `scope` value can grow it — only a
  different (solution/directory) `projectPath` can.
- **More than one project swept**: the note reports how many member projects of which solution were
  swept, and names the narrowing recipe — pass a single `.fsproj` as `projectPath` to sweep just
  that one project (faster, but misses cross-project usages). This is the discoverability that
  answers the field report behind #193: a 40 s sweep against a solution/directory target now tells
  the caller, in the response, how to narrow it next time.

### kind and scope

| `kind` | What it resolves |
|--------|------------------|
| `auto` (default) | Union of `symbol` + `members` + `field` definitions/references/sites |
| `symbol` | Grouped definitions + references |
| `members` | Member-usage sites on a type; pair with `member` |
| `field` | Record construction/update sites; pair with `field` |
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
   record-field set sites (`{ Field = expr }` and `{ x with Field = expr }`), and member-usage sites.
3. De-duplicates the union by `(file, range)` and groups by symbol identity.
4. When FCS finds no sites, probes the project-bound FSAC `workspace/symbol` index without
   treating a warming, disconnected, or mismatched FSAC session as a valid zero.
5. Returns flat `sites` entries with `file`, `range`, `lineText`, plus scoped
   `projectDiagnostics` and explicit project-coverage counts.

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
5. Returns `verdict` + `errorCount` + `warningCount` + a `diagnostics` array filtered to `severity`.

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
3. **`speed=fast` is coverage-aware but still cached** — inspect `complete`, `expectedFiles`,
   `missingFiles`, `staleFiles`, and `sessionGeneration`. Use the default `trusted` when you need a
   fresh type-check of a just-written on-disk edit.

### Related tools

- `fcs_explain_diagnostic` — explain an F# diagnostic after `check` finds it.
- `fcs_diagnostic_fixes` / `fcs_suggest_open` — preview targeted fixes for reported diagnostics.

---

## fcs_nuget_types

**Routing description:** Enumerate all types in one referenced assembly
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
- `fcs_nuget_members` — drill into one specific type's members after finding it with this tool.

---

## fcs_nuget_members

**Routing description:** Enumerate members of one type from a referenced
assembly (matched by `packageId` + `typeName`). Use after `fcs_nuget_types` to discover type
names. Each entry: `name`, `kind`, `signature`, `accessibility`, `isObsolete`, `xmlDocSummary`.
Paginated; default 500, max 2000. Returns `matchedTypes=[]` on no type match.

### How it works internally

1. Resolves the project via `EnsureProjectResults` (same warm-cache path as `fcs_nuget_types`).
2. Matches assemblies whose `SimpleName` equals `packageId` (exact, case-insensitive).
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
2. **No type match → empty, not error** — when `matchedTypes=[]`, the tool did NOT fall back to a
   fuzzy match. Verify `packageId` (exact SimpleName) and `typeName`.
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

- `fcs_nuget_types` — enumerate all types in a specific assembly by exact SimpleName.
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
