# ADR 0002 — Bind `find` continuations to the query and result snapshot

## Status

**Proposed for v0.18.0.** This ADR defines the compatibility contract. Implementation
and the public schema/version change remain a separate change set tracked by #259.
It is deliberately excluded from the v0.17.1 correctness patch.

Implementation is blocked on three v0.17.1 prerequisites:

- #165 must preserve declared `.sln`/`.slnx` members, including missing members, in
  `find` coverage.
- #255 must enforce one end-to-end deadline across resolution, discovery, semantic
  work, fallback, snapshot validation, and response construction.
- #258 must define bounded site/context shaping and the exact zero-row response
  budget behavior before a continuation is minted.

## Context

`find` currently uses the repository-wide offset cursor from `Cursor.fs`:

```json
{"offset": 100}
```

The JSON is Base64-encoded and opaque to callers, but opacity does not provide
identity. A continuation is accepted for any query, scope, or project. On every
request `find` rebuilds and sorts the current result set, then applies the old
offset. If source is inserted, deleted, or retyped between pages, the same offset
can silently skip or repeat sites.

The failure is not malformed Base64. It is a valid continuation applied to a
different logical stream. Therefore decoding and range-checking the integer cannot
make the current format safe.

Other tools also use the shared offset cursor. Their contracts are not part of
#259, so changing `Cursor.encode` / `Cursor.tryDecode` globally would create an
unbounded compatibility migration. The first implementation is intentionally
specific to `find`.

## Decision drivers

1. A continuation must never silently mix queries or result streams.
2. Stale/mismatched cursors must produce a typed restart route.
3. Cursor validation must add no per-cursor retained result state.
4. Tokens must not embed plaintext source, symbol names, or local paths.
5. A process restart may preserve a cursor only after the same project context is
   re-established and the recomputed result stream is identical.
6. The migration must distinguish legacy cursors from corruption.
7. Existing non-`find` pagination remains compatible until separately designed.

## Decision

Introduce a stateless, versioned `find` cursor:

```json
{
  "v": 2,
  "tool": "find",
  "offset": 100,
  "query": "<base64url sha256>",
  "snapshot": "<base64url sha256>"
}
```

The full payload remains Base64-encoded. The hashes are correlatable identities,
not secrets, signatures, or authorization tokens.

### Canonical query identity

Define an internal, explicitly ordered canonical model after `Program` has applied
the active-project fallback and `Find` has materialized defaults:

```fsharp
type FindPositionRequestV2 =
    { Path: string
      Line: int option
      Character: int option
      Word: string option
      Occurrence: int option }

type FindQueryV2 =
    { Schema: int
      Query: string
      RequestedKind: string
      RequestedScope: string
      Target: string
      Path: string option
      Position: FindPositionRequestV2 option
      Exact: bool
      Member: string option
      Field: string option
      ContextLines: int
      IncludeDeclaration: bool
      IncludeInfo: bool
      IncludePerProject: bool
      IncludeSiteTypes: bool }
```

`query` is SHA-256 over an ordinal UTF-8 encoding of those fields in the declared
order. Paths use the same normalized absolute form as the sweep. Strings preserve
the matching semantics used by `find`; normalization must not trim or case-fold a
value unless `find` itself does so. Option values have explicit tags, so `None`,
empty, and omitted/defaulted values cannot collapse accidentally.

`cursor`, `timeoutMs`, and `maxResults` are excluded. A caller may change the
remaining time budget or page size while walking the same logical stream. Omitted
and explicitly supplied defaults become identical before hashing. The canonical
encoding is tested directly and must not depend on JSON object property order.

Only stable request inputs belong to this identity. `ResolvedQuery`,
`ResolvedKind`, `ResolvedScope`, and the symbol found at a position are derived
from current source/workspace state and are deliberately excluded. A caller
changing an input produces `cursor_query_mismatch`; the same input resolving
differently after a source or solution change produces `cursor_stale` through the
snapshot identity below.

### Result snapshot identity

`snapshot` identifies the complete canonical result stream that pagination slices,
not an allegedly atomic filesystem snapshot. Define a `FindResultSnapshotV2`
containing:

- the ordered declared project set and each stable coverage category (`analyzed`,
  `failed`, `timed_out`, `busy`, `missing`, or `not_started`);
- resolved query, kind, scope, and position-symbol identity produced by this
  sweep;
- normalized diagnostic identities that affect the response (project, code,
  severity, normalized range, and bounded message identity);
- every sorted site before pagination, after #258's deterministic snippet/context
  shaping, including normalized file, exact range, symbol/kind/declaration
  identity, project, site type/degradation fields, the canonical bounded
  `siteTypeAlternatives` rows plus their omitted count/offset, bounded line/context
  text, and all other truncation offsets;
- total site count and result-affecting resolution/degradation state.

Hash the canonical full stream with SHA-256. Do not hash only the current page,
mtime, or result count. Free-form retry messages, wall-clock elapsed values, and
other current-attempt telemetry are excluded and documented as per-response
telemetry that callers must not merge across pages.

This binds pages to what the caller can actually combine. It deliberately does
**not** claim that the current FCS/cache pipeline reads an atomic filesystem
snapshot. If source mutates during a sweep, a later continuation either reproduces
the same canonical stream or gets `cursor_stale`; semantic cache atomicity is a
separate correctness concern. The implementation must not publish a cursor until
the complete pre-pagination stream and coverage ledger are available.

Partial sweeps may return useful positive sites and a cursor. Their coverage ledger
is part of the snapshot. If a retry later analyzes an additional project, the old
cursor becomes stale and the caller restarts from page zero instead of mixing the
partial and expanded streams.

### Page-invariant canonical rows

For one `FindQueryV2`, each canonical site row is independent of `cursor`, page
offset, `maxResults`, and the serialized-response budget. In particular,
`siteTypeAlternatives` uses a deterministic per-site cap; it must not consume a
shared allowance that resets or changes at page boundaries. The canonical row
records the alternatives that fit that per-site cap and the exact omitted count
and continuation offset.

#258 may choose only a prefix of these already-canonical rows that fits the current
response envelope. It must not shorten fields inside a row based on the other rows
selected for that page. Changing `maxResults` can therefore change the page cut,
but cannot change the bytes or meaning of any site that appears. This invariant is
required for excluding `maxResults` from query identity and for hashing one complete
pre-pagination stream.

### Strict cursor decoding

Add `Cursor.encodeFindV2`, `Cursor.tryDecodeFind`, and
`Cursor.findPaginationFieldsV2` alongside the existing generic offset helpers.
`find` must not call `Cursor.paginationFields` after v2 ships.

The decoder:

- caps encoded input before Base64 allocation and decoded JSON at 1 KiB;
- requires one JSON object with exactly `v`, `tool`, `offset`, `query`, and
  `snapshot`;
- rejects duplicate and unknown properties;
- requires `v = 2`, `tool = "find"`, a non-negative Int32 offset, and exactly
  32-byte Base64URL-without-padding hashes in canonical spelling;
- distinguishes legacy `{ "offset": n }`, unknown version, malformed Base64/JSON,
  and malformed v2 fields with stable error kinds.

Tool binding is enforced in both directions. Every non-`find` legacy cursor
consumer continues to mint and accept its existing offset-only payload, but its
decoder must require exactly the legacy `offset` property. It rejects a tagged v2
`find` object (and any other unknown properties) instead of extracting its offset.
The response uses that tool's existing invalid-cursor envelope and a stable
`cursor_tool_mismatch` cause when the `tool` tag is present. This is decoder
hardening, not a v2 migration for the other tools.

### Continuation validation

For a continuation request:

1. Validate `timeoutMs` and start #255's single end-to-end deadline at handler
   entry, before active-project fallback, target discovery, or `kind="position"`
   symbol resolution.
2. Strictly decode and validate the bounded cursor envelope within that deadline.
3. Apply request defaults and normalize its stable target/path inputs within the
   remaining budget, then recompute canonical query identity.
   A mismatch returns `cursor_query_mismatch`.
4. Continue under the same deadline through position resolution, discovery, and
   semantic work, construct the complete canonical result stream, and recompute
   snapshot identity.
5. A snapshot mismatch returns `cursor_stale`.
6. Reject an offset beyond the current site count as `cursor_out_of_range`.
7. Apply #258's production-serializer response budget and slice the page.
8. Generate the next v2 cursor at `offset + deliveredSiteCount`.

A rejected continuation returns the following common envelope, but its
`errorKind` and message are failure-specific:

| Condition | `errorKind` | Message route |
| --- | --- | --- |
| canonical query differs | `cursor_query_mismatch` | repeat this query without a cursor; do not imply the result changed |
| canonical result stream differs | `cursor_stale` | repeat without a cursor because the result changed |
| offset exceeds the matching stream | `cursor_out_of_range` | restart because the continuation no longer identifies a page |
| legacy or unsupported version | `cursor_version_unsupported` | restart without the cursor and use the current protocol |
| malformed bounded payload/hash/schema | `cursor_malformed` | discard the cursor and restart |
| valid cursor for another tool | `cursor_tool_mismatch` | use the cursor only with the tool that issued it |

For example, a snapshot mismatch returns:

```json
{
  "status": "invalid_cursor",
  "errorKind": "cursor_stale",
  "paginationRestartRequired": true,
  "retrySameCursor": false,
  "nextCursor": null,
  "message": "The find result changed; repeat the request without cursor."
}
```

Every row in the table sets `paginationRestartRequired = true`,
`retrySameCursor = false`, and returns neither sites nor a new cursor. The
deadline-specific incomplete-validation outcome below is the only continuation
validation failure that permits retrying the same cursor.

If the deadline expires during target/position resolution, discovery, semantic
work, response construction, or before the full result stream can otherwise be
reconstructed and validated, return no sites and no new cursor with
`errorKind = "cursor_validation_incomplete"`,
`paginationRestartRequired = false`, and `retrySameCursor = true`. The caller may
retry the same cursor with a larger `timeoutMs`; the incomplete attempt contributes
no page to the stream.

If #258 cannot fit even one bounded site plus the mandatory envelope, return
`errorKind = "find_site_exceeds_response_budget"`, no sites, no cursor,
`paginationRestartRequired = true`, and a concrete narrowing recipe. Metadata-only
overflow uses a distinct typed error. A same-offset continuation is never emitted.

### Legacy compatibility

An offset-only payload is recognized as a **legacy find cursor**, not malformed
input. In v0.18.0 it returns:

- `status = "invalid_cursor"`;
- `errorKind = "cursor_version_unsupported"`;
- `paginationRestartRequired = true`;
- `retrySameCursor = false`;
- an instruction to repeat the same request without `cursor`.

Silently accepting a legacy cursor would preserve the exact consistency bug this
ADR closes. Reject-and-restart is the only safe compatibility path. Because
FsLangMCP is pre-1.0 and the wire behavior changes, the feature ships in a minor
release and is called out in the changelog.

Existing cursors for project outline, NuGet, public API, and other tools remain
unchanged. A later ADR can migrate them independently.

### State, restart, and privacy

No server-side result snapshot is retained for a cursor. This adds zero
**per-cursor** state; existing bounded project/options/analysis caches remain.
There is no wall-clock expiry. A cursor is valid precisely while its canonical
query and recomputed result stream match.

After process restart, a request that supplied `projectPath` can validate directly.
A request that relied on active context must first repeat `set_project`; without
either, it returns the normal missing-context error and does not claim the cursor
is valid. The restart test must use a fresh host and cover explicit `projectPath`,
re-established active context, and neither.

The token contains no plaintext query, path, source text, or diagnostic. Unkeyed
SHA-256 values are nevertheless dictionary-guessable for low-entropy inputs and
correlatable across transcripts; this design is not a confidentiality mechanism.
That is acceptable because the local cursor is not an access-control boundary and
the same caller already supplies the query/project to `find`. A forged token cannot
grant access beyond executing the request and is rejected unless both identities
match.

## Options rejected

### Keep offset-only cursors and document them as ephemeral

Rejected because documentation cannot prevent silent skipping/repetition or
cross-query reuse.

### Retain full result snapshots in a bounded server cache

This gives cheap continuation reads but introduces memory sizing, expiry races,
multi-client ownership, process-restart invalidation, and retention of source
snippets. It can be reconsidered only if stateless recomputation is measured to be
unacceptable after v2 ships.

### Treat cache/source stamps as an atomic semantic snapshot

Rejected. Current content-sensitive cache stamps are stronger than mtimes but are
computed around mutable files and do not prove one atomic FCS view. The cursor
instead hashes the actual canonical result stream it promises to continue.

### Embed raw canonical query/source metadata in the token

Rejected because Base64 is not encryption and would place local paths and symbol
queries directly in logs/transcripts.

### Sign or encrypt the cursor

Rejected for the first implementation. The cursor is not an authorization token;
an ephemeral signing key breaks restart compatibility, while a persistent secret
adds installation lifecycle without solving result-stream consistency.

## Required tests

- unchanged traversal across several pages, including a different `maxResults`;
- a linked file with enough `siteTypeAlternatives` to hit the per-site cap,
  traversed with different `maxResults`; each canonical row is byte-identical and
  appears exactly once;
- changed query, kind, scope, project, path, and every result-shaping option;
- unchanged request after the solution gains/loses a member or the symbol at a
  requested position changes; these are `cursor_stale`, not query mismatches;
- omitted versus explicit defaults and path normalization in canonical query bytes;
- insertion and deletion before the previous offset;
- edit with unchanged site count/ranges but changed bounded site/context content;
- controlled mid-sweep mutation followed by continuation, proving either identical
  stream reproduction or a typed stale/incomplete result, never mixed sites;
- missing declared `.sln` and `.slnx` members before any cursor is issued;
- partial sweep followed by newly available project evidence;
- continuation timeout, then retry of the same cursor with a larger `timeoutMs`;
- continuation timeout during cold `kind="position"` resolution, before any sweep,
  then retry of the same cursor with a larger `timeoutMs`;
- fresh-host restart with explicit project, repeated `set_project`, and neither;
- legacy offset-only cursor, unknown version/tool, duplicate/unknown JSON property,
  malformed/noncanonical hash, oversized encoded/decoded input, and
  negative/out-of-range offset;
- passing a v2 `find` cursor to every legacy cursor decoder/consumer, proving none
  can extract and apply its offset;
- one-site and metadata-only response-budget exhaustion with no non-advancing cursor;
- response-budget page reduction followed by a continuation with no skipped site.

## Consequences

- `find` continuation calls still pay the semantic sweep cost; the cursor validates
  consistency rather than serving as a result cache.
- A result or coverage change forces a restart from page zero, which is explicit
  and safe but may repeat work.
- The cursor payload grows by two SHA-256 values while remaining small and bounded.
- Other tools keep their legacy cursor contract in v0.18.0.
- #165, #255, and #258 must land before implementation.
- The design does not pull any cursor contract change into v0.17.1.

## Cross-references

- Umbrella review: #254 item 5
- Implementation/design tracker: #259
- Required v0.17.1 discovery fix: #165
- Required v0.17.1 deadline work: #255
- Required v0.17.1 response-budget work: #258
