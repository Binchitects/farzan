#!/usr/bin/env python3
"""
The images' own vulnerabilities, before a release: Trivy over each image the stack runs (its OS packages, its language
packages and the Go and .NET it carries), from a saved copy of the image (no engine's socket goes into Trivy).

    python3 tools/image-scan.py IMAGE...            these images
    python3 tools/image-scan.py --stack             every image of every feature (compose config --images, all profiles)
    python3 tools/image-scan.py --stack --json OUT  and each image's findings as JSON in OUT/

A critical finding fails it (exit 1) unless docs/compliance/accepted-vulnerabilities.yaml accepts its id, with why and
until when, as tools/release-scan.py does for the pinned packages. High ones with a fix are listed and do not fail it.
Trivy runs as aquasec/trivy (TRIVY_IMAGE), its database kept in the volume trivy-cache; HTTPS_PROXY is passed on.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ACCEPTED = ROOT / "docs" / "compliance" / "accepted-vulnerabilities.yaml"
TRIVY = os.environ.get("TRIVY_IMAGE", "aquasec/trivy:0.76.2")
ORDER = ["CRITICAL", "HIGH", "MEDIUM", "LOW", "UNKNOWN"]


def accepted_ids(today: date) -> dict[str, dict]:
    spec = importlib.util.spec_from_file_location("release_scan", ROOT / "tools" / "release-scan.py")
    rs = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(rs)  # type: ignore[union-attr]
    return rs.accepted(ACCEPTED, today)


def placeholders(deploy: Path) -> dict[str, str]:
    """Every variable the compose files name, as a placeholder where it is not set: compose lists the images only once
    each required one has a value, and no secret is needed to list them."""
    import re
    names = set()
    for f in deploy.glob("*.yml"):
        # Only those with no default (${X}, ${X:?...}): the rest take compose's own.
        names |= set(re.findall(r"\$\{([A-Z][A-Z0-9_]*)(?=\}|:?\?)", f.read_text(encoding="utf-8")))
    values = {n: ("/tmp/image-scan" if n.endswith("_DIR") else "placeholder") for n in names if n not in os.environ}
    values["COMPOSE_PROJECT_NAME"] = os.environ.get("COMPOSE_PROJECT_NAME", "arena")
    return values


def stack_images(engine: str) -> list[str]:
    """Every image compose would run, every feature on (the bundle carries them all)."""
    deploy = ROOT / "deploy"
    base = dict(os.environ, **({} if (deploy / ".env").exists() else placeholders(deploy)))
    profiles = subprocess.run([engine, "compose", "config", "--profiles"], cwd=deploy, env=base, capture_output=True, text=True)
    env = dict(base, COMPOSE_PROFILES=",".join(p for p in profiles.stdout.split() if p))
    out = subprocess.run([engine, "compose", "config", "--images"], cwd=deploy, env=env, capture_output=True, text=True)
    if out.returncode != 0:
        raise SystemExit(f"image-scan: compose config --images failed: {out.stderr.strip()[:400]}")
    return sorted(set(out.stdout.split()))


def scan(image: str, engine: str, work: Path) -> dict:
    tar = work / "image.tar"
    saved = subprocess.run([engine, "save", "-o", str(tar), image], capture_output=True, text=True)
    if saved.returncode != 0:
        return {"error": f"not on this host ({saved.stderr.strip()[:200]})"}
    try:
        subprocess.run([engine, "volume", "create", "trivy-cache"], capture_output=True)
        cmd = [engine, "run", "--rm", "-v", "trivy-cache:/root/.cache", "-v", f"{work}:/work"]
        for k in ("HTTPS_PROXY", "HTTP_PROXY", "NO_PROXY"):
            if os.environ.get(k):
                cmd += ["-e", f"{k}={os.environ[k]}"]
        cmd += [TRIVY, "image", "--quiet", "--input", "/work/image.tar", "--scanners", "vuln", "--format", "json",
                "--output", "/work/out.json", "--timeout", "30m"]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0:
            return {"error": r.stderr.strip()[-400:]}
        return json.loads((work / "out.json").read_text())
    finally:
        tar.unlink(missing_ok=True)


def findings(report: dict) -> list[dict]:
    out = []
    for res in report.get("Results") or []:
        for v in res.get("Vulnerabilities") or []:
            out.append({"id": v["VulnerabilityID"], "severity": v.get("Severity", "UNKNOWN"), "package": v["PkgName"],
                        "version": v.get("InstalledVersion", ""), "fixed": v.get("FixedVersion") or "", "where": res.get("Target", "")})
    return out


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("images", nargs="*")
    ap.add_argument("--stack", action="store_true", help="every image of every feature")
    ap.add_argument("--engine", default="docker", choices=["docker", "podman"])
    ap.add_argument("--json", type=Path, help="each image's findings as JSON in this folder")
    a = ap.parse_args(argv)
    images = sorted(set(a.images) | (set(stack_images(a.engine)) if a.stack else set()))
    if not images:
        ap.error("name images, or --stack")
    ok = accepted_ids(date.today())
    failed = 0
    with tempfile.TemporaryDirectory(prefix="image-scan-") as tmp:
        work = Path(tmp)
        os.chmod(work, 0o777)
        for image in images:
            report = scan(image, a.engine, work)
            if "error" in report:
                print(f"{image}: NOT SCANNED: {report['error']}")
                failed += 1
                continue
            found = findings(report)
            if a.json:
                a.json.mkdir(parents=True, exist_ok=True)
                (a.json / (image.replace("/", "_").replace(":", "_") + ".json")).write_text(json.dumps(found, indent=1))
            count = {s: sum(1 for f in found if f["severity"] == s) for s in ORDER}
            crit = sorted({f["id"]: f for f in found if f["severity"] == "CRITICAL"}.values(), key=lambda f: f["id"])
            open_crit = [f for f in crit if f["id"] not in ok]
            high_fix = sorted({(f["package"], f["version"], f["fixed"]) for f in found if f["severity"] == "HIGH" and f["fixed"]})
            print(f"{image}: critical {count['CRITICAL']} ({len(crit) - len(open_crit)} accepted), high {count['HIGH']} "
                  f"({len(high_fix)} packages with a fix), medium {count['MEDIUM']}, low {count['LOW']}")
            for f in open_crit:
                print(f"  CRITICAL {f['id']} {f['package']} {f['version']} -> {f['fixed'] or 'no fix'} ({f['where'][-60:]})")
            for pkg, ver, fix in high_fix:
                print(f"  high {pkg} {ver} -> {fix}")
            failed += 1 if open_crit else 0
    print(f"{len(images)} image(s): {'every critical fixed or accepted' if not failed else f'{failed} with a critical not accepted, or not scanned'}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
