#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
"""Which tests run no line that another test does not: from coverage per test (tools/coverage-per-test.sh).

    python3 tools/coverage-redundant.py .cov/tests > .cov/redundant.md

Each test's lines are DIR/{class}.{method}.{n}.lines.gz ("file:line" each). For each test: how many lines it ran and
how many only it ran. A test that runs no line of its own is a candidate to cut, not a verdict: it may check what the
others only pass through (an answer, a refusal, a number), which coverage cannot see. Standard library only.
"""
import collections
import gzip
import sys
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    tests = {}
    for f in sorted(Path(sys.argv[1]).glob("*.lines.gz")):
        name = f.name[: -len(".lines.gz")]
        with gzip.open(f, "rt", encoding="utf-8") as g:
            # EF's generated migrations left out, should the settings not have.
            tests[name] = {line.strip() for line in g if line.strip() and "/Migrations/" not in line}
    if not tests:
        sys.exit(f"no test's lines in {sys.argv[1]}")
    runs = collections.Counter(line for lines in tests.values() for line in lines)
    rows = []
    for name, lines in tests.items():
        own = sum(1 for line in lines if runs[line] == 1)
        rows.append((own, len(lines), name))
    rows.sort(key=lambda r: (r[0], -r[1], r[2]))
    none = [r for r in rows if r[0] == 0]
    print("# Coverage per test\n")
    print(f"{len(tests)} tests ran {len(runs):,} lines of Llm.Api and Llm.Core. **{len(none)}** ran no line that no other test ran:")
    print("candidates to cut (a test may check what the others only pass through, which coverage cannot see).\n")
    # The fewest tests that run every line the suite runs (greedily: the one that adds most, again and again): every other
    # test runs only lines these do.
    left = set(runs)
    cover = []
    pool = dict(tests)
    while left:
        name, lines = max(pool.items(), key=lambda kv: (len(kv[1] & left), kv[0]))
        gain = lines & left
        if not gain:
            break
        cover.append(name)
        left -= gain
        del pool[name]
    print(f"**{len(cover)}** tests together run every one of those lines (chosen greedily); the other {len(tests) - len(cover)} run only lines")
    print("they run too. A test outside them is a candidate, never more: the rule it checks is not a line.\n")
    print("| Test | Lines it ran | Lines only it ran |\n|---|---|---|")
    for own, total, name in rows:
        print(f"| {name.rsplit('.', 1)[0]} | {total:,} | {own:,} |")
    return 0


if __name__ == "__main__":
    sys.exit(main())
