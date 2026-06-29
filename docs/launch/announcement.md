# FsLangMCP — launch post drafts

Three ready-to-post variants of the same announcement, tuned per channel, plus a
posting checklist. Pick the variant for the channel, paste, and edit lightly for
voice. All claims here are defensible against the repo; do not add metrics beyond
the A/B numbers below.

Canonical links to use in every post:

- Repo: https://github.com/Neftedollar/FsLangMCP
- Narrative ("Why your AI agent shouldn't grep F#"): https://github.com/Neftedollar/FsLangMCP/blob/main/docs/why-agents-grep-fsharp.md
- Runnable example: https://github.com/Neftedollar/FsLangMCP/tree/main/examples
- NuGet: https://www.nuget.org/packages/FsLangMcp/

---

## 1. r/fsharp

**Title:**

> FsLangMCP — give your AI coding agent real F# compiler semantics instead of grep

**Body:**

Hi r/fsharp — I built a small open-source tool I want to put in front of people
who actually write F#, because the failure mode it fixes is F#-specific.

If you use an AI coding agent (Claude Code, Cursor, Copilot CLI, Codex) on an F#
codebase, you've probably watched it `rg`/`grep` for a function name and then
edit the wrong thing. Text search is structurally wrong on F#: it misses
point-free / partially-applied calls (`List.map Order.describe` has no
`describe(` token), it misses `[<AutoOpen>]` and aliased `open` spellings, it
can't cross project/assembly boundaries, it can't tell a record-field *set* from
a *read*, and it drowns computation-expression custom operations in noise.

FsLangMCP is an MCP stdio server that resolves symbols through the real compiler
instead — F# Compiler Service in-process + FsAutoComplete over LSP — and exposes
it as intent-shaped tools. The two you'll use most:

- **`find`** — one cross-project semantic search: definitions, references,
  record-field set-sites, member call-sites, unioned across every `.fsproj` in
  the solution, FCS-resolved. Catches the sites grep can't see.
- **`check`** — one trustworthy `clean | errors | unknown` verdict from a fresh
  in-process type-check, so the agent doesn't fall back to `dotnet build` to be
  sure.

I dogfooded it on its own codebase and measured a live-execution A/B (headless
`claude -p` runs, counting real tool calls vs grep fallback). Shaping the tool
surface to agent intent cut grep-fallback 56% → 28% and steps-to-completion
10.4 → 5.8. Honest caveat: that's modest-N on my own usage, not a universal
benchmark — I'm reporting what I saw measuring myself, not claiming it's proven
for everyone.

It's early-stage (genuinely — handful of stars), MIT, .NET 10, on NuGet as
`FsLangMcp`. I'd love feedback from people who'd actually use this, especially on
where `find`/`check` give wrong or surprising results on your projects.

- Repo: https://github.com/Neftedollar/FsLangMCP
- The longer "why grep fails on F#" writeup: https://github.com/Neftedollar/FsLangMCP/blob/main/docs/why-agents-grep-fsharp.md
- Runnable 3-minute example: https://github.com/Neftedollar/FsLangMCP/tree/main/examples

(Author here — happy to answer anything in the comments.)

---

## 2. Hacker News — Show HN

**Title (≤ 80 chars):**

> Show HN: FsLangMCP – real F# compiler semantics for AI coding agents

**First comment (post immediately after submitting):**

Author here. FsLangMCP is an MCP stdio server that gives AI coding agents
(Claude Code, Cursor, Codex, Copilot CLI) the real F# compiler instead of grep —
F# Compiler Service in-process plus FsAutoComplete over LSP, exposed as
intent-shaped tools.

The motivation is narrow and concrete: agents default to `rg`/`grep` for code
navigation, and on F# that's structurally wrong. Partial application hides calls
(`List.map Order.describe` — no `describe(` to match), `[<AutoOpen>]` and module
aliases hide a symbol's spelling, project/assembly boundaries hide consumers,
record-field syntax hides set-vs-read, and computation-expression custom
operations look like keywords. Text search operates on the characters; you need
something that operates on what the compiler resolves them to. So `find` does one
FCS-resolved, cross-`.fsproj` semantic search (definitions / references /
field-set-sites / member call-sites, tagged by kind), and `check` gives one
fresh-type-checked `clean | errors | unknown` verdict so the agent doesn't fall
back to `dotnet build`.

The part I think is most generally interesting is a methodology note, not the F#.
I A/B'd two tool surfaces two ways. A *selection-only simulation* — asking a model
which tool it would pick per task — predicted the surfaces were neutral. A
*live-execution* A/B — headless `claude -p` runs, parsing the actual tool calls —
showed grep-fallback dropping 56% → 28% and steps-to-completion 10.4 → 5.8 going
from a 36-tool sprawl to a 22-tool intent-shaped surface. The lesson I took:
dogfood agent UX with real runs, not simulated picks; a model's stated tool
preference and its behavior under a real task are different measurements. Caveat,
stated plainly: this is modest-N on my own dogfooding, one environment — not a
peer-reviewed benchmark.

A corollary that's easy to misread: it is *not* "fewer tools is better." After
the consolidation I added 13 more intent-shaped tools (refactor-impact preview,
dead-code, explain-diagnostic, ...) up to 35. The principle is shape tools to
agent *intent*, not minimize count — adding intent-shaped tools is fine; adding
API-shaped tools is what hurts.

Early-stage, MIT, .NET 10, `dotnet tool install -g FsLangMcp`. Repo and a longer
writeup:

- https://github.com/Neftedollar/FsLangMCP
- https://github.com/Neftedollar/FsLangMCP/blob/main/docs/why-agents-grep-fsharp.md

Feedback very welcome — especially cases where `find`/`check` give wrong answers
on real codebases.

---

## 3. F# Software Foundation Slack / Discord (short version)

> Built a small open-source thing for the agentic-coding crowd: **FsLangMCP** — an
> MCP server that gives AI coding agents (Claude Code, Cursor, Copilot CLI, Codex)
> real F# compiler semantics instead of grep.
>
> The pitch in one line: your agent greps for a function name, and on F# that
> quietly breaks — point-free calls have no `name(` token, `[<AutoOpen>]`/aliased
> opens change the spelling, uses cross project boundaries, record-field sets look
> like reads. FsLangMCP resolves through FCS in-process + FsAutoComplete (LSP) and
> exposes it as intent-shaped tools — mainly `find` (cross-`.fsproj` semantic
> search) and `check` (one fresh-type-checked verdict).
>
> Dogfooded it on its own repo; a live-execution A/B cut agent grep-fallback
> 56% → 28% (modest-N on my own usage — reporting what I measured, not a universal
> benchmark). Early-stage, MIT, .NET 10, `dotnet tool install -g FsLangMcp`.
>
> Repo: https://github.com/Neftedollar/FsLangMCP
> Why grep fails on F#: https://github.com/Neftedollar/FsLangMCP/blob/main/docs/why-agents-grep-fsharp.md
> 3-min runnable example: https://github.com/Neftedollar/FsLangMCP/tree/main/examples
>
> (I'm the author — would genuinely value feedback on where it gives wrong answers.)

---

## Posting checklist

- **Disclose authorship up front.** Every post above says "author here" / "I
  built." Keep it. Communities forgive self-promotion when it's honest; they
  punish it when it's hidden.
- **Respond within hours, not days.** Be at a keyboard for the first few hours
  after posting. The launch's value is the conversation, not the link.
- **Don't astroturf.** No alt accounts, no asking friends for upvotes, no fake
  "I've been using this for months" testimonials. One honest post per channel.
- **Lead with the problem, not the product.** The hook is "grep breaks on F#,"
  not "look at my tool." If a reader doesn't share the problem, the tool isn't
  for them — and that's fine.
- **Hold the line on the numbers.** Only the A/B figures (56% → 28%,
  10.4 → 5.8 steps, 89% → 100% success), always with the modest-N / own-usage
  caveat attached. No invented metrics, no "10x," no benchmarks we didn't run.
- **Stay humble about stage.** It's early and small. Say so. Under-promising and
  inviting "tell me where it's wrong" beats overclaiming and getting corrected.
- **Match each community's norms.** r/fsharp and the F# Slack are practitioner
  spaces — be specific and technical. HN rewards the generalizable lesson (the
  simulation-vs-live methodology point), so lead with that there.
