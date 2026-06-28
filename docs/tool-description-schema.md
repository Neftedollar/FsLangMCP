# Tool Description Schema

MCP tool descriptions in `Program.fs` follow this 4-slot schema so agents can
route reliably without reading source. Target length: 250-400 chars per
description; anything over 500 chars usually belongs in `docs/tools-detailed.md`.

**Descriptions must NOT start with a `[`-bracket tag.** The `[FSAC]` /
`[FCS in-process]` / `[meta]` implementation-layer prefixes were stripped in
#136 — they leaked an internal detail into the agent-facing surface without
helping routing. Lead with the action verb instead.

## Slots

1. **What it does** — one sentence, declarative. Lead with the verb ("Compile X...",
   "Find every Y...", "Return Z..."). This is the first thing the agent reads — no
   bracket tag in front of it.
2. **When to prefer (or avoid)** — one sentence. Explicit "prefer X over Y" or "avoid for
   agent workflows — use W" callouts beat vague positives.
3. **Key params and defaults** — call out non-obvious defaults (pagination caps, opt-in flags).
   LSP 0-based positions noted here.
4. **Critical caveat / cross-reference** — schema-compatibility notes, known edge cases,
   fallback behavior, links to related tools or `docs/tools-detailed.md`.

## Good example

> Cache-invalidating parse+typecheck for one file. Use when
> `check` looks stale right after Edit/Write. Surgically drops project-options +
> project-results entries for THIS project and calls `InvalidateConfiguration` before re-running.
> Caveat: FCS may still serve from its internal AST cache for transitively-referenced files; fall
> back to `dotnet build` for ground truth.

(Illustrative — all 4 slots present, no leading bracket tag.)

## Anti-pattern: leading bracket tag

A description that opens with `[FSAC]`, `[FCS in-process]`, `[meta]`, or any other
`[...]` tag wastes the most valuable position — the first token the agent reads —
on an implementation-layer marker that does not help it decide whether to call the
tool. Lead with the verb. The `ToolDescriptionSchemaTests` / `audit-tool-descriptions.py`
gate fails the build on any description that starts with `[`.

## Anti-pattern: over-explanation

If a description exceeds 500 chars, implementation detail is competing with the routing decision.
Move implementation detail to `docs/tools-detailed.md` and keep the description focused on
"should I call this tool right now?".

## Anti-pattern: positive-only short descriptions

If a tool overlaps with another (e.g., `fcs_referenced_symbols` vs `fcs_nuget_types`), a
positive-only description ("useful for X") leaves the agent guessing. Add an explicit "prefer the
alternative when..." or "avoid for..." callout.

## Overlap set

These tool pairs overlap; at least one description in each pair must carry an explicit
prefer/avoid/instead-of callout so an agent reading either one can disambiguate:

| Pair | Disambiguator lives in |
|------|------------------------|
| `fcs_referenced_symbols` ↔ `fcs_nuget_types` | both ("prefer `fcs_nuget_types` when you know the assembly" / "prefer `fcs_referenced_symbols` for substring search") |
| `fcs_nuget_types` ↔ `fcs_nuget_members` | `fcs_nuget_members` ("prefer `fcs_nuget_types` to discover type names first") |
| `fcs_signature_help` ↔ `fsharp_signature_data` | `fsharp_signature_data` ("use this when FCS fallback is insufficient") |

The pre-#136 overlap pairs that referenced the 14 removed cluster tools
(`workspace_symbol`, `fcs_find_symbol`, `textDocument_definition`, `fcs_validate_snippet`,
`fcs_parse_and_check_file`, `fcs_symbol_at_word`/`fcs_type_at_position`,
`fcs_file_outline`/`fcs_file_symbols`, `workspace_diagnostics`/`fcs_check_file`) were
dropped — those tools no longer exist; every capability is reachable via `find` / `check` /
`fcs_symbol_at_word` / `fcs_file_outline`.

## Implementation layers (internal — no longer surfaced as prefixes)

The underlying backend distinction still exists in the code, but it is no longer
exposed in the description text. For reference:

| Layer | Meaning |
|-----|---------|
| FSAC | Proxies to the fsautocomplete child process via LSP JSON-RPC. Requires `set_project` to have been called first. |
| FCS in-process | Runs directly inside the MCP server process via `FSharp.Compiler.Services`. Requires project options (via `set_project` or explicit `projectPath`). |
| meta | Pure introspection — no project context required, no side effects. |

## Length budget

| Range | Verdict |
|-------|---------|
| < 150 chars | Under-specified — routing decisions missing. |
| 150-400 chars | Target zone. |
| 400-500 chars | Acceptable if all chars are routing-relevant. |
| > 500 chars | Move implementation detail to `docs/tools-detailed.md`. |

## Adding a new tool

1. Write the description following the 4-slot schema above. Lead with the verb — no bracket tag.
2. Count characters. If > 500, split: keep the routing part in `Program.fs`, move mechanics
   to a new H2 section in `docs/tools-detailed.md`.
3. If the tool overlaps with an existing one, add an explicit "prefer X" or "avoid for" sentence.
4. Run the length analyzer (see `Justfile` or README) to confirm the budget.
