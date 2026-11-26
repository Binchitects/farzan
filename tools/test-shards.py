#!/usr/bin/env python3
"""Run a .NET test project as N shards at once: built once, then N `dotnet test` processes side by side, each with
its classes (balanced by their measured time) and its own fixtures (its own Postgres and app). The run takes about the
longest shard's time instead of the sum. Separate processes, so nothing a test holds in memory is shared.

    tools/test-shards.py tests/Llm.Tests                     # 3 shards, Release
    tools/test-shards.py tests/Llm.Tests --shards 4 --coverage --out TestResults/shards
    tools/test-shards.py tests/CodeArena.Tests --times TestResults/baseline/codearena

The times come from a run's TRX files (--times DIR, default the project's tests/<name>/test-times.json, written by
--save-times after a run); a class with no time counts as the average. Exit status: the number of shards that failed.
"""
import argparse
import json
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
CLASS = re.compile(r"^\s*(?:public\s+|internal\s+)?(?:sealed\s+|static\s+|abstract\s+|partial\s+)*class\s+(\w+)", re.M)
NAMESPACE = re.compile(r"^\s*namespace\s+([\w.]+)", re.M)
# [Fact], [Theory] and their kin ([RealFact], [SkippableTheory]...).
TEST = re.compile(r"\[\w*(?:Fact|Theory)\b")


def test_classes(project: Path) -> list[str]:
    """The classes that hold tests (a [Fact] or [Theory] in their file), by their full name."""
    found = []
    for f in sorted(project.rglob("*.cs")):
        if "/bin/" in str(f) or "/obj/" in str(f):
            continue
        text = f.read_text(encoding="utf-8", errors="replace")
        if not TEST.search(text):
            continue
        ns = NAMESPACE.search(text)
        # Each class in the file that has a test before the next class starts.
        starts = [(m.start(), m.group(1)) for m in CLASS.finditer(text)]
        for i, (at, name) in enumerate(starts):
            end = starts[i + 1][0] if i + 1 < len(starts) else len(text)
            if TEST.search(text, at, end):
                found.append(f"{ns.group(1)}.{name}" if ns else name)
    return sorted(set(found))


def times_from_trx(folder: Path) -> dict[str, float]:
    times: dict[str, float] = {}
    for path in folder.rglob("*.trx"):
        root = ET.parse(path).getroot()
        names = {t.get("id"): t.find(TRX + "TestMethod").get("className").split(",")[0] for t in root.iter(TRX + "UnitTest")}
        for r in root.iter(TRX + "UnitTestResult"):
            h, m, s = r.get("duration", "0:0:0").split(":")
            cls = names.get(r.get("testId"), "?")
            times[cls] = times.get(cls, 0) + int(h) * 3600 + int(m) * 60 + float(s)
    return times


def balance(classes: list[str], times: dict[str, float], shards: int) -> list[list[str]]:
    """The slowest class first into the lightest shard (longest-processing-time first)."""
    known = [t for c, t in times.items() if c in classes]
    average = sum(known) / len(known) if known else 1.0
    bins: list[tuple[float, list[str]]] = [(0.0, []) for _ in range(shards)]
    for c in sorted(classes, key=lambda c: -times.get(c, average)):
        i = min(range(shards), key=lambda k: bins[k][0])
        bins[i] = (bins[i][0] + times.get(c, average), bins[i][1] + [c])
    return [b[1] for b in bins if b[1]]


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("project")
    ap.add_argument("--shards", type=int, default=3)
    ap.add_argument("--times", help="a folder of TRX files to balance by (default: the project's test-times.json)")
    ap.add_argument("--out", default="TestResults/shards")
    ap.add_argument("--coverage", action="store_true")
    ap.add_argument("--configuration", default="Release")
    ap.add_argument("--save-times", action="store_true", help="write the run's class times to the project's test-times.json")
    ap.add_argument("--dotnet", default=str(ROOT / "tools" / "dn"), help="the dotnet to run (tools/dn: the SDK in a container)")
    a = ap.parse_args(argv)
    project = (ROOT / a.project).resolve()
    rel = project.relative_to(ROOT)
    saved = project / "test-times.json"
    times = times_from_trx(ROOT / a.times) if a.times else json.loads(saved.read_text()) if saved.exists() else {}
    classes = test_classes(project)
    shards = balance(classes, times, a.shards)
    out = ROOT / a.out
    out.mkdir(parents=True, exist_ok=True)
    print(f"{len(classes)} classes in {len(shards)} shards" + (", balanced by their times" if times else " (no times: by count)"))
    build = subprocess.run([a.dotnet, "build", str(rel), "-c", a.configuration], cwd=ROOT, capture_output=True, text=True)
    if build.returncode != 0:
        print(build.stdout[-4000:] + build.stderr[-2000:])
        return len(shards)

    # Coverage instruments the test's assemblies where they are: each shard gets its own copy of the build, else three
    # processes rewrite the same files at once and a test host crashes at its start.
    built = project / "bin" / a.configuration
    target = next(built.glob(f"*/{project.name}.dll"))

    def run(i: int) -> tuple[int, int, float, str]:
        # A class by its full name and the dot after it, so one is never another's prefix.
        flt = "|".join(f"FullyQualifiedName~{c}." for c in shards[i])
        what = str(rel)
        if a.coverage:
            copy = out / f"bin{i + 1}"
            shutil.rmtree(copy, ignore_errors=True)
            shutil.copytree(target.parent, copy)
            what = str((copy / target.name).relative_to(ROOT))
        args = [a.dotnet, "test", what, "--filter", flt] + ([] if a.coverage else ["-c", a.configuration, "--no-build"]) + [
                "--results-directory", f"{a.out}/shard{i + 1}", "--logger", f"trx;LogFileName=shard{i + 1}.trx",
                # A host that crashes leaves the tests it was running (Sequence_*.xml), to tell which.
                "--blame"]
        if a.coverage:
            # The collector from the shard's own copy: a dll run directly finds it only where it is told to look.
            args += ["--collect:XPlat Code Coverage", "--test-adapter-path", str(Path(what).parent)]
        start = time.time()
        with open(out / f"shard{i + 1}.log", "w") as log:
            code = subprocess.run(args, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT).returncode
        tail = (out / f"shard{i + 1}.log").read_text(errors="replace").strip().splitlines()[-1:] or [""]
        return i, code, time.time() - start, tail[0]

    start = time.time()
    with ThreadPoolExecutor(len(shards)) as pool:
        results = list(pool.map(run, range(len(shards))))
    failed = 0
    for i, code, took, tail in results:
        failed += code != 0
        print(f"shard {i + 1}: {len(shards[i])} classes, {took / 60:.1f} min, {'ok' if code == 0 else 'FAILED'} :: {tail}")
    print(f"wall {(time.time() - start) / 60:.1f} min (build not counted)")
    if a.save_times:
        saved.write_text(json.dumps({c: round(t, 1) for c, t in sorted(times_from_trx(out).items())}, indent=1) + "\n")
        print(f"times saved to {saved.relative_to(ROOT)}")
    return failed


if __name__ == "__main__":
    sys.exit(main())
