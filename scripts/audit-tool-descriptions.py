#!/usr/bin/env python3
"""Audit MCP tool descriptions in Program.fs against the 5-slot schema.

Schema reference: docs/tool-description-schema.md

Hard rules (enforced — non-zero exit on violation):
1. Every description must start with a recognized tag: [FSAC], [FCS in-process], or [meta].
2. Length must not exceed the OVER_BUDGET threshold (500 chars).

Soft signals (reported only — exit code unaffected):
3. Length below UNDER_BUDGET (150) — likely under-specified.
4. Missing explicit prefer/avoid callout for tools that overlap with another.
5. Missing cross-reference to docs/tools-detailed.md for long descriptions.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

UNDER_BUDGET = 150
TARGET_MAX = 400
ACCEPTABLE_MAX = 500
OVER_BUDGET = 500

TAGS = ("[FSAC]", "[FCS in-process]", "[meta]")

# Pairs of tools known to overlap — at least one description in each pair
# should contain a "prefer X over Y" callout. Order-independent.
OVERLAP_PAIRS: list[tuple[str, str]] = [
    ("workspace_symbol", "fcs_find_symbol"),
    ("fcs_referenced_symbols", "fcs_nuget_types"),
    ("fcs_validate_snippet", "fcs_parse_and_check_file"),
    ("fcs_signature_help", "fsharp_signature_data"),
    ("fcs_symbol_at_word", "fcs_type_at_position"),
    ("fcs_file_outline", "fcs_file_symbols"),
    ("workspace_diagnostics", "fcs_check_file"),
    ("textDocument_definition", "fcs_find_symbol"),
]

PREFER_PATTERNS = re.compile(
    r"\b(prefer\b|avoid\b|better than\b|use this when\b|use when\b|instead of\b|fall back\b|fallback to\b|use\s+`?[a-z_]+`?\s+(when|for|to)\b)",
    re.IGNORECASE,
)


def parse_descriptions(source: str) -> list[tuple[str, str]]:
    pat = re.compile(
        r'TypedTool\.define<[^>]+>\s*\n\s*"([^"]+)"\s*\n\s*"((?:[^"\\]|\\.)*)"',
        re.S,
    )
    rows: list[tuple[str, str]] = []
    for m in pat.finditer(source):
        name = m.group(1)
        raw = m.group(2)
        decoded = raw.replace('\\"', '"').replace("\\\\", "\\")
        rows.append((name, decoded))
    return rows


def verdict_label(n: int) -> str:
    if n < UNDER_BUDGET:
        return "UNDER"
    if n <= TARGET_MAX:
        return "TARGET"
    if n <= ACCEPTABLE_MAX:
        return "ACCEPT"
    return "OVER"


def has_tag(desc: str) -> bool:
    return any(desc.startswith(t) for t in TAGS)


def has_prefer_callout(desc: str) -> bool:
    return bool(PREFER_PATTERNS.search(desc))


def main(argv: list[str]) -> int:
    repo_root = Path(__file__).resolve().parent.parent
    program_fs = repo_root / "Program.fs"
    if not program_fs.exists():
        print(f"error: {program_fs} not found", file=sys.stderr)
        return 2

    rows = parse_descriptions(program_fs.read_text())
    if not rows:
        print("error: no tool descriptions parsed from Program.fs", file=sys.stderr)
        return 2

    rows_by_name = {n: d for n, d in rows}

    hard_failures: list[str] = []
    soft_warnings: list[str] = []

    print(f"Audited {len(rows)} tool descriptions in Program.fs\n")
    print(f"{'Tool':<35} {'len':>4}  {'verdict':<7} tag prefer")
    print("-" * 70)
    for name, desc in rows:
        L = len(desc)
        v = verdict_label(L)
        tag_ok = has_tag(desc)
        prefer_ok = has_prefer_callout(desc)
        print(
            f"{name:<35} {L:>4}  {v:<7} {'OK ' if tag_ok else 'MIS'} "
            f"{'OK ' if prefer_ok else '---'}"
        )
        if not tag_ok:
            hard_failures.append(f"{name}: missing tag prefix (expected one of {TAGS})")
        if L > OVER_BUDGET:
            hard_failures.append(
                f"{name}: length {L} exceeds {OVER_BUDGET}-char ceiling — split into "
                f"routing-only description + section in docs/tools-detailed.md"
            )
        if L < UNDER_BUDGET:
            soft_warnings.append(f"{name}: length {L} under {UNDER_BUDGET} — likely under-specified")

    # Overlap-pair check: at least one side of each pair should have a prefer callout.
    for a, b in OVERLAP_PAIRS:
        if a not in rows_by_name or b not in rows_by_name:
            continue
        a_has = has_prefer_callout(rows_by_name[a])
        b_has = has_prefer_callout(rows_by_name[b])
        if not (a_has or b_has):
            soft_warnings.append(
                f"overlap pair ({a}, {b}): neither side has a prefer/avoid callout"
            )

    print()
    if soft_warnings:
        print(f"SOFT WARNINGS ({len(soft_warnings)}):")
        for w in soft_warnings:
            print(f"  - {w}")
        print()

    if hard_failures:
        print(f"HARD FAILURES ({len(hard_failures)}):", file=sys.stderr)
        for f in hard_failures:
            print(f"  - {f}", file=sys.stderr)
        return 1

    print("OK — all descriptions within 500-char ceiling and tagged.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
