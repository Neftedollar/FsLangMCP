# Tool Description Schema

MCP tool descriptions in `Program.fs` follow this 5-slot schema so agents can
route reliably without reading source. Target length: 250-400 chars per
description; anything over 500 chars usually belongs in `docs/tools-detailed.md`.

## Slots

1. **Tag prefix** — `[FSAC]` for fsautocomplete-backed, `[FCS in-process]` for compiler-service
   tools, `[meta]` for version/status. Lets agents and humans group at a glance.
2. **What it does** — one sentence, declarative. Lead with the verb ("Compile X...",
   "Find every Y...", "Return Z...").
3. **When to prefer (or avoid)** — one sentence. Explicit "prefer X over Y" or "avoid for
   agent workflows — use W" callouts beat vague positives.
4. **Key params and defaults** — call out non-obvious defaults (pagination caps, opt-in flags).
   LSP 0-based positions noted here.
5. **Critical caveat / cross-reference** — schema-compatibility notes, known edge cases,
   fallback behavior, links to related tools or `docs/tools-detailed.md`.

## Good example

> [FCS in-process] Cache-invalidating parse+typecheck for one file. Use when
> `workspace_diagnostics` looks stale right after Edit/Write. Surgically drops project-options +
> project-results entries for THIS project and calls `InvalidateConfiguration` before re-running.
> Caveat: FCS may still serve from its internal AST cache for transitively-referenced files; fall
> back to `dotnet build` for ground truth.

(328 chars — `fcs_check_file`. All 5 slots present.)

## Anti-pattern: over-explanation

If a description exceeds 500 chars, implementation detail is competing with the routing decision.
Move implementation detail to `docs/tools-detailed.md` and keep the description focused on
"should I call this tool right now?".

## Anti-pattern: positive-only short descriptions

If a tool overlaps with another (e.g., `workspace_symbol` vs `fcs_find_symbol`), a positive-only
description ("useful for X") leaves the agent guessing. Add an explicit "prefer the alternative
when..." or "avoid for..." callout.

## Tag reference

| Tag | Meaning |
|-----|---------|
| `[FSAC]` | Proxies to the fsautocomplete child process via LSP JSON-RPC. Requires `set_project` to have been called first. |
| `[FCS in-process]` | Runs directly inside the MCP server process via `FSharp.Compiler.Services`. Requires project options (via `set_project` or explicit `projectPath`). |
| `[meta]` | Pure introspection — no project context required, no side effects. |

## Length budget

| Range | Verdict |
|-------|---------|
| < 150 chars | Under-specified — routing decisions missing. |
| 150-400 chars | Target zone. |
| 400-500 chars | Acceptable if all chars are routing-relevant. |
| > 500 chars | Move implementation detail to `docs/tools-detailed.md`. |

## Adding a new tool

1. Write the description following the 5-slot schema above.
2. Count characters. If > 500, split: keep the routing part in `Program.fs`, move mechanics
   to a new H2 section in `docs/tools-detailed.md`.
3. If the tool overlaps with an existing one, add an explicit "prefer X" or "avoid for" sentence.
4. Run the length analyzer (see `Justfile` or README) to confirm the budget.
