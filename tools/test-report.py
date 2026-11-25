#!/usr/bin/env python3
"""What a test run cost and what it covered: per class time from TRX files, line and branch coverage per feature
(a source folder) from Cobertura files.

    tools/test-report.py TestResults/baseline/llm                 # every .trx and coverage.cobertura.xml under it
    tools/test-report.py TestResults/baseline/llm --json out.json
    tools/test-report.py --vitest src/web/coverage/coverage-final.json --vitest-times times.json

A feature is the first folder under the project's root (src/Llm.Api/Gateway/X.cs -> Llm.Api/Gateway); a file at a
project's root counts under the project.
"""
import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def seconds(text: str) -> float:
    h, m, s = text.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def trx_times(paths):
    """Each test's outcome and seconds, by its class."""
    tests = []
    for path in paths:
        root = ET.parse(path).getroot()
        names = {}
        for t in root.iter(TRX + "UnitTest"):
            m = t.find(TRX + "TestMethod")
            names[t.get("id")] = (m.get("className") or "").split(",")[0], m.get("name")
        for r in root.iter(TRX + "UnitTestResult"):
            cls, method = names.get(r.get("testId"), ("?", r.get("testName")))
            tests.append({"class": cls, "test": r.get("testName"), "outcome": r.get("outcome"),
                          "seconds": seconds(r.get("duration", "0:0:0"))})
    return tests


def feature(filename: str) -> str:
    parts = Path(filename.replace("\\", "/")).parts
    if "src" in parts:
        parts = parts[parts.index("src") + 1:]
    elif len(parts) and parts[0] == "repo":
        parts = parts[1:]
    return "/".join(parts[:2]) if len(parts) > 2 else (parts[0] if parts else filename)


def cobertura(paths):
    """Lines and branches per file, merged across the files given (a line covered in any is covered)."""
    lines = defaultdict(dict)  # file -> line -> hits
    branches = defaultdict(dict)  # file -> line -> (covered, total)
    for path in paths:
        root = ET.parse(path).getroot()
        sources = [s.text or "" for s in root.iter("source")]
        for c in root.iter("class"):
            name = c.get("filename")
            if sources and not name.startswith("/"):
                name = str(Path(sources[0]) / name)
            for ln in c.iter("line"):
                n = int(ln.get("number"))
                lines[name][n] = max(lines[name].get(n, 0), int(ln.get("hits", "0")))
                cond = ln.get("condition-coverage")
                if cond:
                    m = re.search(r"\((\d+)/(\d+)\)", cond)
                    if m:
                        got, total = int(m.group(1)), int(m.group(2))
                        old = branches[name].get(n, (0, total))
                        branches[name][n] = (max(old[0], got), total)
    return lines, branches


def summarize_coverage(lines, branches):
    per = defaultdict(lambda: [0, 0, 0, 0])
    for f, ls in lines.items():
        k = feature(f)
        per[k][0] += sum(1 for h in ls.values() if h > 0)
        per[k][1] += len(ls)
    for f, bs in branches.items():
        k = feature(f)
        per[k][2] += sum(c for c, _ in bs.values())
        per[k][3] += sum(t for _, t in bs.values())
    return per


def pct(a, b):
    return 100.0 * a / b if b else 0.0


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("dir", nargs="?")
    ap.add_argument("--json")
    ap.add_argument("--top", type=int, default=25)
    a = ap.parse_args(argv)
    out = {}
    if a.dir:
        d = Path(a.dir)
        tests = trx_times(sorted(d.rglob("*.trx")))
        by = defaultdict(lambda: [0, 0.0, 0])
        for t in tests:
            by[t["class"]][0] += 1
            by[t["class"]][1] += t["seconds"]
            by[t["class"]][2] += t["outcome"] != "Passed" and t["outcome"] != "NotExecuted"
        total = sum(t["seconds"] for t in tests)
        outcomes = defaultdict(int)
        for t in tests:
            outcomes[t["outcome"]] += 1
        print(f"{len(tests)} tests, {dict(outcomes)}, {total / 60:.1f} test-minutes (summed, not wall)")
        print(f"\nSlowest {a.top} classes (tests, seconds, share):")
        for cls, (n, s, bad) in sorted(by.items(), key=lambda x: -x[1][1])[: a.top]:
            print(f"  {s:8.1f}s {pct(s, total):5.1f}%  {n:4d}  {cls}{'  FAILED ' + str(bad) if bad else ''}")
        print(f"\nSlowest {a.top} tests:")
        for t in sorted(tests, key=lambda t: -t["seconds"])[: a.top]:
            print(f"  {t['seconds']:7.1f}s  {t['test'][:150]}")
        lines, branches = cobertura(sorted(d.rglob("coverage.cobertura.xml")))
        per = summarize_coverage(lines, branches)
        tl = sum(v[0] for v in per.values()), sum(v[1] for v in per.values())
        tb = sum(v[2] for v in per.values()), sum(v[3] for v in per.values())
        print(f"\nCoverage: lines {tl[0]}/{tl[1]} = {pct(*tl):.1f}%, branches {tb[0]}/{tb[1]} = {pct(*tb):.1f}%")
        for k, (lc, lt, bc, bt) in sorted(per.items(), key=lambda x: -x[1][1]):
            print(f"  {pct(lc, lt):5.1f}% of {lt:6d} lines  {pct(bc, bt):5.1f}% of {bt:6d} branches  {k}")
        out = {"tests": tests, "classes": {k: {"tests": v[0], "seconds": v[1]} for k, v in by.items()},
               "coverage": {k: {"lines": v[0], "lineTotal": v[1], "branches": v[2], "branchTotal": v[3]} for k, v in per.items()}}
    if a.json:
        Path(a.json).write_text(json.dumps(out, indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
