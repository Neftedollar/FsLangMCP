# Why your AI agent shouldn't grep F#

Your AI coding agent just grepped for a function name across an F# codebase.
Here's everything that went wrong — and what it should have done instead.

The agent ran something like `rg "describe"`. It got back the definition, a
doc-comment, two strings that happen to contain the word, and *none* of the
places the function is actually called. It then confidently edited the wrong
line. This isn't a bad agent. It's a good agent using the wrong tool, because
text search is the only code-navigation primitive most agents reach for by
default — and on F#, text search is quietly, structurally wrong.

FsLangMCP is an MCP stdio server that gives AI coding agents (Claude Code,
Cursor, Codex, Copilot CLI) the *real* F# compiler instead of grep: the F#
Compiler Service (FCS) in-process plus FsAutoComplete (FSAC) over LSP, exposed
as intent-shaped tools. This document explains why that matters specifically for
F#, what the approach is, and what happened when we measured it on our own usage.

---

## Why text search fails on F# specifically

Every language has some gap between "the characters on the line" and "what the
compiler resolves them to." F# widens that gap in several directions at once.
Here are the concrete failure modes — each one is a real call site a grep gets
wrong.

### 1. Partial application and point-free style

F# code passes functions around without ever writing a call with parentheses:

```fsharp
orders |> List.map Order.describe
```

`Order.describe` is *invoked here* — once per order — but there is no
`describe(` token to match. An agent grepping for `describe(` to find call sites
finds nothing. An agent grepping for `describe` over-matches the definition and
the doc-comment, and still can't tell a call from a mention. The compiler knows
this is a use of `Order.describe`; the regex can't.

### 2. `AutoOpen`, aliased, and bare opens

How a symbol is spelled at the call site depends on which `open` is in scope:

```fsharp
open Domain            // now `Order.describe order` — or, if Order is [<AutoOpen>], just `describe order`
module O = Order       // aliased: `O.describe order`
```

A grep for `Order.describe` misses the `[<AutoOpen>]` bare form *and* the
aliased `O.describe` form — both of which are the same symbol. `open` directives,
module aliases, and `[<AutoOpen>]` mean the textual spelling of a call carries
almost no reliable information about which symbol it resolves to.

### 3. Cross-project (cross-assembly) uses

A type defined in one project is constructed in another:

```fsharp
// Domain/Order.fs
type Order = { Id: int; Customer: string; Amount: decimal }

// App/Program.fs  (different .fsproj, different assembly)
let orders = [ { Id = 1; Customer = "Alice"; Amount = 250m } ]
```

`rg "Order" Domain/` returns only the definition; the construction in `App/` is
in a different project directory and a different compiled assembly. An agent that
scopes its search to "the project I'm working in" silently misses every
downstream consumer — exactly the sites that matter when you're about to change
the type.

### 4. Record-field set-sites vs. reads

Naming a record field doesn't tell you whether you're setting it or reading it:

```fsharp
{ Id = 1; Customer = "Alice"; Amount = 250m }   // Amount set (literal)
{ existing with Amount = 300m }                  // Amount set (copy-update)
if order.Amount > 1000m then ...                 // Amount read
```

A grep for `Amount` returns all three indiscriminately — plus the field
declaration and any comment that says "amount". If you're trying to find every
place a field is *assigned* (say, before tightening an invariant), text search
can't separate the set-sites from the reads.

### 5. Computation-expression custom operations

Custom operations look like keywords but are really members on a builder:

```fsharp
query {
    for o in orders do
    where (o.Amount > 1000m)
    select o.Customer
}
```

`where` and `select` here are `[<CustomOperation>]` members resolved against the
`query` builder. A grep for `select` matches LINQ in C# files, SQL in strings,
the word in comments, and unrelated identifiers — with no way to know which hits
are uses of *this* builder's operation. The compiler resolves it exactly; the
regex drowns in noise.

### 6. Shadowing

F# lets you rebind a name in the same scope:

```fsharp
let result = compute ()
let result = validate result   // shadows the first `result`
```

A textual rename of `result` corrupts both bindings; a grep for `result` can't
tell you which definition a given use refers to. Only a compiler that tracks
scopes can rename one without touching the other.

---

The through-line: in F#, **the characters on the line under-determine the
meaning.** Partial application hides calls, opens and aliases hide spelling,
project boundaries hide consumers, field syntax hides set-vs-read, custom
operations hide builder members, and shadowing hides scope. Text search operates
on the characters. The compiler operates on the meaning.

---

## The approach: compiler-resolved, intent-shaped tools

FsLangMCP doesn't try to make grep smarter. It resolves symbols through the same
machinery the compiler and IDE use — FCS in-process, FSAC over LSP — and exposes
the results as a small number of **intent-shaped** MCP tools, named for what an
agent is trying to *do* rather than for the underlying compiler API.

Two tools carry most of the load:

- **`find`** — multi-project semantic search. One call unions definitions,
  references, record-field set-sites, and member call-sites on a type
  (`x.Foo`), across *every* `.fsproj` in the solution, all FCS-resolved. It
  handles every failure mode above: it catches the point-free `List.map
  Order.describe`, the `[<AutoOpen>]` and aliased spellings, the cross-project
  construction in `App/`, and it tags each site with its `kind`
  (`definition | reference | field-set-literal | field-set-update | field-read |
  member-usage`) so set-sites and reads are distinguishable. Bare `find(query)`
  returns a compact one-line-per-site list; narrow with `kind` + `scope`, or get
  member call-sites with `kind=members` + `member=Name`.

- **`check`** — one trustworthy verdict for the active F# context. Bare
  `check()` runs a *fresh* in-process type-check and returns
  `verdict: clean | errors | unknown`, so it never reports a stale-cache
  false-clean and the agent never has to fall back to `dotnet build` to be sure.

Around those, the rest of the surface follows the same principle — named for
intent, not for the API: `fcs_rename_preview` (blast-radius of a rename before
you apply it), `fcs_refactor_impact` (uses + tests + compile-order + public-API
in one call), `fcs_dead_code` (likely-unused bindings as cleanup candidates),
`fcs_explain_diagnostic` (turn an FS error code into a plain-language fix),
`fcs_suggest_open` (the right `open` for an unresolved name), `fcs_file_outline`
and `fcs_project_outline` (structure without dumping every signature). Everything
is read-only or preview-by-default; the preview tools return workspace edits and
write nothing.

---

## What we measured (and how — honestly)

The interesting result here isn't only "compiler beats grep" — you'd expect
that. It's about **how we shaped the tool surface**, and a methodology lesson
that surprised us.

We ran a **live-execution A/B**: headless agent runs (`claude -p` with the
`--mcp-config` swapped between two tool surfaces), parsing each run's actual tool
calls to count semantic-tool use versus `Grep`/rg fallback. The tasks were
FsLangMCP's own dogfooding work on its own codebase.

**Be clear about what this is:** a modest-N, single-environment experiment on our
own usage — not a peer-reviewed, universal benchmark. Treat the numbers as "this
is what we saw when we measured ourselves," not "this is proven for everyone."

Comparing a **36-tool sprawl** against a **22-tool intent-shaped surface** (the
0.11.0 consolidation):

| Metric | 36-tool sprawl | 22-tool intent-shaped |
|---|---|---|
| Agent rg/grep fallback | 56% | **28%** (halved) |
| Steps to completion | 10.4 | **5.8** |
| Task success | 89% | **100%** |

The methodology lesson worth telling: before the live A/B, we ran a
**selection-only simulation** — just asking a model which tool it *would* pick
for each task. The simulation predicted the two surfaces were **neutral**: no
meaningful difference. The gain only appeared under **live execution**, where the
agent actually had to chain calls, recover from misses, and decide whether to
fall back to grep mid-task. The takeaway: **dogfood agent UX with real runs, not
simulated picks.** A model's stated tool preference and its behavior under a real
task are different measurements, and only the second one predicts the outcome.

---

## The design principle (stated precisely)

It would be easy to read the table above as "fewer tools is better." That's the
wrong lesson, and we want to be precise so it isn't overstated.

The principle is **shape the tool surface to agent intent**, not minimize the
count. Lead with a small number of high-intent verbs (`find`, `check`) that map
directly onto what an agent is trying to accomplish, and let the long tail of
specialized tools be discoverable rather than front-and-center.

The proof that count isn't the point: after the 22-tool consolidation, 0.12.0
**added 13 more tools** — refactor-impact preview, dead-code, explain-diagnostic,
signature-status, and others — reaching the current **35**. Every one of those is
intent-shaped: it answers a question an agent actually asks ("what breaks if I
rename this?", "what's unused here?", "what does FS0039 mean and how do I fix
it?"). Adding intent-shaped tools doesn't reintroduce the sprawl problem; adding
*API-shaped* tools does. The enemy was never the number — it was tools named for
the compiler's internals instead of the agent's goal.

---

## Try it in 60 seconds

```bash
dotnet tool install -g FsLangMcp
fslangmcp --bootstrap-tools   # one-time: fetches fsautocomplete + ionide.projinfo.tool
```

Add to your MCP client config:

```json
{ "mcpServers": { "fslangmcp": { "command": "fslangmcp" } } }
```

Then `set_project` with your `.fsproj`/`.sln`, and start with `find` or `check`.

A runnable two-project example — the cross-project `Order` case from this
document, with exact response shapes — lives in
[`../examples/`](../examples/README.md). Full setup and the complete tool
reference are in the [README](../README.md).

FsLangMCP is MIT-licensed, targets .NET 10, and is early-stage and open to
feedback: [github.com/Neftedollar/FsLangMCP](https://github.com/Neftedollar/FsLangMCP).
If your agent is still grepping F#, give it the compiler instead.
