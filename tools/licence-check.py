#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
"""The licensing guard: what the repository says about its licences, kept true.

    python3 tools/licence-check.py              every check; exits 1, naming each problem
    python3 tools/licence-check.py --fix        adds the SPDX and copyright lines to source files without them
    python3 tools/licence-check.py --write      writes THIRD-PARTY-NOTICES.md (asks PyPI for a Python package it does
                                                not list yet; NuGet's come from a restore: run ./tools/dn restore first)
    python3 tools/licence-check.py --nuget      fails, too, when no restore is here to read NuGet's packages from

What it checks:
- headers: every source file of ours (git ls-files: code, scripts, Dockerfiles) names its licence and its copyright
  holder in its first lines, as LICENSING.md says, unless docs/compliance/licence-policy.yaml exempts it, with why;
- packages: every package the release ships (the web apps' npm packages, the .NET packages, the Python packages and
  programs the images install, the base images) has a licence the policy allows, or one it accepts by name with why.
  Strong copyleft (GPL, AGPL, SSPL...), a licence nobody stated, and anything else unknown fail. THIRD-PARTY-NOTICES.md
  lists them all, and must be current;
- files and metadata: LICENSE.md is the AGPL unchanged; LICENSING.md, COMMERCIAL-LICENSE.md and the notices are there;
  the package.json files, Directory.Build.props and the Dockerfiles' labels name the same licence;
- authors: every commit's author holds the copyright (the policy's owners), or signed the commit off (DCO,
  CONTRIBUTING.md), so nobody else's code is offered under the commercial licence without their grant.
"""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import re
import subprocess
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from datetime import date
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
POLICY = ROOT / "docs" / "compliance" / "licence-policy.yaml"
NOTICES = ROOT / "THIRD-PARTY-NOTICES.md"
SPDX = "AGPL-3.0-only OR LicenseRef-Farzan-Commercial"
HOLDER = "Binchitects"
LOCKS = ("src/web/package-lock.json", "src/code-intel-web/package-lock.json")
PACKAGE_JSONS = ("src/web/package.json", "src/code-intel-web/package.json")
DOTNET_PROJECTS = ("src/Llm.Api", "src/CodeIntel")

# A source file's comment, by its extension; a file with none of these but a shebang is a script, commented with #.
COMMENT = {
    ".cs": "//", ".ts": "//", ".tsx": "//", ".js": "//", ".mjs": "//", ".cjs": "//", ".rs": "//", ".go": "//",
    ".py": "#", ".sh": "#", ".ps1": "#", ".sql": "--", ".lua": "--", ".css": "/*", ".scss": "/*",
}
HEADER_LINES = 8  # the lines the SPDX and copyright lines must be within
SPDX_RE = re.compile(r"SPDX-License-Identifier:\s*(.+?)\s*(?:\*/)?\s*$")
COPYRIGHT_RE = re.compile(r"Copyright \(C\) (\d{4})(?:-(\d{4}))? (.+?)\s*(?:\*/)?\s*$")


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, check=True).stdout


def policy() -> dict:
    return yaml.safe_load(POLICY.read_text())


# ---------------------------------------------------------------- headers

def comment_of(path: Path, first: str) -> str | None:
    if path.name == "Dockerfile" or path.name.endswith(".Dockerfile"):
        return "#"
    if path.suffix in COMMENT:
        return COMMENT[path.suffix]
    if not path.suffix and first.startswith("#!"):
        return "#"
    return None


def exempt(rel: str, pol: dict) -> bool:
    return any(fnmatch.fnmatch(rel, e["glob"]) for e in pol.get("exempt_files", []))


def source_files(pol: dict) -> list[tuple[str, str]]:
    """(path, comment) for each tracked source file the headers apply to."""
    out = []
    for rel in git("ls-files", "-z").split("\0"):
        if not rel or exempt(rel, pol):
            continue
        p = ROOT / rel
        if p.is_symlink() or not p.is_file():
            continue
        try:
            with p.open(encoding="utf-8") as f:
                first = f.readline()
        except (UnicodeDecodeError, OSError):
            continue
        c = comment_of(p, first)
        if c:
            out.append((rel, c))
    return out


def header_problem(text: str, year_now: int) -> str | None:
    head = text.splitlines()[:HEADER_LINES]
    spdx = next((m.group(1) for line in head if (m := SPDX_RE.search(line))), None)
    if spdx is None:
        return "no SPDX-License-Identifier line"
    if spdx != SPDX:
        return f"SPDX-License-Identifier is {spdx!r}, not {SPDX!r}"
    cr = next((m for line in head if (m := COPYRIGHT_RE.search(line))), None)
    if cr is None:
        return f"no \"Copyright (C) YEAR {HOLDER}\" line"
    if cr.group(3) != HOLDER:
        return f"the copyright line names {cr.group(3)!r}, not {HOLDER}"
    last = int(cr.group(2) or cr.group(1))
    if int(cr.group(1)) < 2020 or last > year_now:
        return f"the copyright year {cr.group(0).split()[2]} is not a real one"
    return None


def with_header(text: str, comment: str, year: int) -> str:
    """The text with the two lines put in after a shebang or an encoding line, which must stay first."""
    if comment == "/*":
        block = f"/* SPDX-License-Identifier: {SPDX} */\n/* Copyright (C) {year} {HOLDER} */\n"
    else:
        block = f"{comment} SPDX-License-Identifier: {SPDX}\n{comment} Copyright (C) {year} {HOLDER}\n"
    if "\r\n" in text:
        block = block.replace("\n", "\r\n")
    lines = text.splitlines(keepends=True)
    at = 0
    if lines and lines[0].startswith("#!"):
        at = 1
    if len(lines) > at and re.match(r"^#.*coding[:=]", lines[at]):
        at += 1
    if lines and at and not lines[at - 1].endswith("\n"):
        lines[at - 1] += "\r\n" if "\r\n" in text else "\n"
    return "".join(lines[:at]) + block + "".join(lines[at:])


def check_headers(pol: dict, fix: bool, year: int) -> list[str]:
    problems = []
    for rel, comment in source_files(pol):
        p = ROOT / rel
        with p.open(encoding="utf-8", newline="") as f:  # as it is: a CRLF file keeps its CRLF
            text = f.read()
        why = header_problem(text, year)
        if why is None:
            continue
        if fix and why.startswith("no SPDX") and "Copyright (C)" not in "\n".join(text.splitlines()[:HEADER_LINES]):
            with p.open("w", encoding="utf-8", newline="") as f:
                f.write(with_header(text, comment, year))
            continue
        problems.append(f"{rel}: {why} (python3 tools/licence-check.py --fix adds them)")
    return problems


# ---------------------------------------------------------------- licences

ALIASES_BUILTIN = {
    "mit license": "MIT", "the mit license": "MIT", "mit": "MIT", "expat": "MIT",
    "apache 2.0": "Apache-2.0", "apache-2": "Apache-2.0", "apache license 2.0": "Apache-2.0",
    "apache license, version 2.0": "Apache-2.0", "apache software license": "Apache-2.0", "apache2": "Apache-2.0",
    "bsd": "BSD-3-Clause", "bsd license": "BSD-3-Clause", "new bsd license": "BSD-3-Clause",
    "3-clause bsd license": "BSD-3-Clause", "bsd-3": "BSD-3-Clause", "bsd 3-clause": "BSD-3-Clause",
    "apache 2.0 license": "Apache-2.0", "apache 2": "Apache-2.0", "apache2.0": "Apache-2.0", "apache": "Apache-2.0",
    "apache-2.0 license": "Apache-2.0", "bsd 3-clause license": "BSD-3-Clause", "bsd-3-clause license": "BSD-3-Clause",
    "isc license": "ISC", "psf": "PSF-2.0", "python software foundation license": "PSF-2.0",
    "https://licenses.nuget.org/mit": "MIT", "https://licenses.nuget.org/apache-2.0": "Apache-2.0",
}
CLASSIFIERS = {
    "License :: OSI Approved :: MIT License": "MIT", "License :: OSI Approved :: Apache Software License": "Apache-2.0",
    "License :: OSI Approved :: BSD License": "BSD-3-Clause", "License :: OSI Approved :: ISC License (ISCL)": "ISC",
    "License :: OSI Approved :: Python Software Foundation License": "PSF-2.0",
    "License :: OSI Approved :: Mozilla Public License 2.0 (MPL 2.0)": "MPL-2.0",
    "License :: OSI Approved :: GNU Lesser General Public License v3 (LGPLv3)": "LGPL-3.0-only",
    "License :: OSI Approved :: GNU General Public License v3 (GPLv3)": "GPL-3.0-only",
    "License :: OSI Approved :: GNU General Public License v2 (GPLv2)": "GPL-2.0-only",
    "License :: OSI Approved :: The Unlicense (Unlicense)": "Unlicense",
    "License :: Public Domain": "LicenseRef-Public-Domain",
}


def normalise(raw: str | None, pol: dict) -> str:
    """An SPDX expression for what a package states, "NOASSERTION" when it states nothing."""
    if raw is None or not str(raw).strip():
        return "NOASSERTION"
    s = " ".join(str(raw).split())
    aliases = {**ALIASES_BUILTIN, **{k.lower(): v for k, v in (pol.get("aliases") or {}).items()}}
    if s.lower() in aliases:
        return aliases[s.lower()]
    if len(s) > 80:  # a licence's whole text pasted into a metadata field
        first = s[:200].lower()
        for key, spdx in (("mit license", "MIT"), ("apache license", "Apache-2.0"), ("bsd", "BSD-3-Clause")):
            if key in first:
                return spdx
        return "NOASSERTION"
    if s.startswith("(") and s.endswith(")") and s.count("(") == 1:
        s = s[1:-1]
    return s


LICENCE_TEXTS = (  # a licence file's words, to its SPDX identifier
    ("Permission is hereby granted, free of charge, to any person obtaining", "MIT"),
    ("Apache License\n", "Apache-2.0"), ("Apache License, Version 2.0", "Apache-2.0"),
    ("Redistribution and use in source and binary forms", "BSD-3-Clause"),
    ("is Public Domain", "LicenseRef-Public-Domain"), ("public domain", "LicenseRef-Public-Domain"),
    ("MICROSOFT SOFTWARE LICENSE TERMS", "LicenseRef-Microsoft-Software-License"),
)


def licence_of_text(text: str) -> str:
    head = text[:3000]
    for words, spdx in LICENCE_TEXTS:
        if words in head:
            if spdx == "BSD-3-Clause" and "3." not in head and "Neither the name" not in head:
                return "BSD-2-Clause"
            return spdx
    return "NOASSERTION"


def overridden(c: dict, pol: dict) -> dict:
    """A package whose metadata leaves out its licence, or misstates it, as the policy records it (with evidence)."""
    for o in pol.get("overrides", []):
        if o["ecosystem"] == c["ecosystem"] and fnmatch.fnmatch(c["name"], o["name"]):
            return {**c, "licence": o["licence"]}
    return c


def verdict(expr: str, pol: dict) -> str:
    """allowed, review (weak copyleft: a separate, unmodified library) or denied, for an SPDX expression."""
    allowed, review = set(pol["allowed"]), set(pol["review"])

    def one(term: str) -> str:
        term = term.strip().strip("()").strip()
        term = re.sub(r"\s+WITH\s+.*$", "", term)
        if term in allowed:
            return "allowed"
        if term in review:
            return "review"
        return "denied"

    rank = {"allowed": 0, "review": 1, "denied": 2}
    best = "denied"
    for alt in re.split(r"\s+OR\s+", expr):
        worst = max((one(t) for t in re.split(r"\s+AND\s+", alt)), key=rank.get)
        best = min(best, worst, key=rank.get)
    return best


def accepted(c: dict, pol: dict, today: date) -> dict | None:
    for a in pol.get("accepted", []):
        if a["ecosystem"] == c["ecosystem"] and fnmatch.fnmatch(c["name"], a["name"]) and fnmatch.fnmatch(c["licence"], a["licence"]):
            until = a.get("until")
            if until and date.fromisoformat(str(until)) < today:
                continue
            return a
    return None


# ---------------------------------------------------------------- what the release ships

def npm_components(pol: dict) -> list[dict]:
    found = {}
    for lock in LOCKS:
        data = json.loads((ROOT / lock).read_text())
        for path, pkg in data.get("packages", {}).items():
            if not path or pkg.get("dev") or pkg.get("link") or "version" not in pkg:
                continue
            name = pkg.get("name") or path.rsplit("node_modules/", 1)[-1]
            found[(name, pkg["version"])] = {"ecosystem": "npm", "name": name, "version": pkg["version"],
                                             "licence": normalise(pkg.get("license"), pol)}
    return list(found.values())


def nuspec_licence(folder: Path, pol: dict) -> str:
    specs = list(folder.glob("*.nuspec"))
    if not specs:
        return "NOASSERTION"
    meta = ET.parse(specs[0]).getroot()
    ns = {"n": meta.tag.split("}")[0].strip("{")} if meta.tag.startswith("{") else {"n": ""}
    q = (lambda t: f"n:{t}") if ns["n"] else (lambda t: t)
    lic = meta.find(f".//{q('license')}", ns) if ns["n"] else meta.find(".//license")
    if lic is not None and (lic.get("type") == "expression") and lic.text:
        return normalise(lic.text, pol)
    if lic is not None and lic.get("type") == "file" and lic.text and (folder / lic.text).is_file():
        return licence_of_text((folder / lic.text).read_text(errors="replace"))
    url = meta.find(f".//{q('licenseUrl')}", ns) if ns["n"] else meta.find(".//licenseUrl")
    if url is not None and url.text:
        return normalise(url.text.strip(), pol)
    return "NOASSERTION"


def nuget_components(pol: dict) -> list[dict] | None:
    """From each project's obj/project.assets.json (a restore), None when no restore is here."""
    found = {}
    for project in DOTNET_PROJECTS:
        assets = ROOT / project / "obj" / "project.assets.json"
        if not assets.is_file():
            return None
        data = json.loads(assets.read_text())
        folders = [Path(f) for f in data.get("packageFolders", {})]
        for key, lib in data.get("libraries", {}).items():
            if lib.get("type") != "package":
                continue
            name, version = key.split("/", 1)
            licence = "NOASSERTION"
            for base in folders:
                for cand in (base / lib.get("path", f"{name.lower()}/{version.lower()}"),
                             Path.home() / ".cache/llm-app-dotnet/.nuget/packages" / lib.get("path", "")):
                    if cand.is_dir():
                        licence = nuspec_licence(cand, pol)
                        break
                if licence != "NOASSERTION":
                    break
            found[(name, version)] = {"ecosystem": "nuget", "name": name, "version": version, "licence": licence}
    return list(found.values())


def release_scan():
    import importlib.util
    spec = importlib.util.spec_from_file_location("release_scan", ROOT / "tools" / "release-scan.py")
    rs = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(rs)  # type: ignore[union-attr]
    return rs


def dockerfiles() -> list[Path]:
    return sorted([*ROOT.glob("src/*/Dockerfile"), *ROOT.glob("deploy/services/*/Dockerfile"),
                   *ROOT.glob("deploy/services/*.Dockerfile")])


def pypi_licence(name: str, version: str, pol: dict, opener=urllib.request.urlopen) -> str:
    try:
        with opener(f"https://pypi.org/pypi/{name}/{version.split('+')[0]}/json", timeout=30) as r:
            info = json.load(r)["info"]
    except (urllib.error.URLError, TimeoutError, KeyError, ValueError):
        return "NOASSERTION"
    expr = info.get("license_expression")
    if expr:
        return normalise(expr, pol)
    lic = normalise(info.get("license"), pol)
    if lic != "NOASSERTION":
        return lic
    for c in info.get("classifiers", []):
        if c in CLASSIFIERS:
            return CLASSIFIERS[c]
    return "NOASSERTION"


def python_components(pol: dict, known: dict, ask: bool, opener=urllib.request.urlopen) -> list[dict]:
    out = []
    for c in release_scan().python_packages(dockerfiles()):
        key = ("pypi", c["name"], c["version"])
        licence = known.get(key)
        if licence is None:
            licence = pypi_licence(c["name"], c["version"], pol, opener) if ask else "NOT LISTED"
        out.append({**c, "licence": licence})
    return out


ARG = re.compile(r"^\s*ARG\s+([A-Z0-9_]+)_VERSION=v?([0-9][A-Za-z0-9.+-]*)\s*$", re.M)


def program_components(pol: dict) -> list[dict]:
    """The programs the images download by version (ARG NAME_VERSION=...), with the licence the policy gives each."""
    known = {p["arg"]: p for p in pol.get("programs", [])}
    found = {}
    for f in dockerfiles():
        for arg, version in ARG.findall(f.read_text()):
            p = known.get(arg)
            if p:
                found[(p["name"], version)] = {"ecosystem": "program", "name": p["name"], "version": version,
                                               "licence": p["licence"]}
    return list(found.values())


FROM = re.compile(r"^\s*FROM\s+(?:--platform=\S+\s+)?(\S+)", re.M | re.I)
ARG_DEFAULT = re.compile(r"^\s*ARG\s+([A-Za-z0-9_]+)=(\S+)\s*$", re.M)


def image_components(pol: dict) -> list[dict]:
    """Each image a Dockerfile builds on (its ARG's default where FROM names one), not another of its own stages."""
    found = {}
    special = pol.get("images") or {}
    for f in dockerfiles():
        text = f.read_text()
        args = dict(ARG_DEFAULT.findall(text))
        stages = set(re.findall(r"^\s*FROM\s+\S+\s+AS\s+(\S+)", text, re.M | re.I))
        for ref in FROM.findall(text):
            ref = re.sub(r"\$\{?([A-Za-z0-9_]+)\}?", lambda m: args.get(m.group(1), m.group(0)), ref)
            if "$" in ref or ref in stages or ":" not in ref.split("/")[-1]:
                continue
            name, version = ref.rsplit(":", 1)
            found[ref] = {"ecosystem": "image", "name": name, "version": version,
                          "licence": special.get(name, pol["base_image_licence"])}
    return list(found.values())


def label_expected(rel: str, pol: dict) -> str:
    extra = (pol.get("image_labels") or {}).get(rel)
    return f"({SPDX}) AND {extra}" if extra else SPDX


# ---------------------------------------------------------------- THIRD-PARTY-NOTICES.md

TITLES = {"npm": "npm packages (the web apps)", "nuget": ".NET packages (the app and Farzan Graph)",
          "pypi": "Python packages (the images)", "program": "Programs the images install",
          "image": "Base images"}
ROW = re.compile(r"^\| `(?P<name>[^`]+)` \| (?P<version>[^|]+?) \| (?P<licence>[^|]+?) \|$")


def read_notices() -> dict:
    """{(ecosystem, name, version): licence} from the notices file as it stands."""
    out: dict = {}
    if not NOTICES.is_file():
        return out
    eco = None
    by_title = {v: k for k, v in TITLES.items()}
    for line in NOTICES.read_text().splitlines():
        if line.startswith("## "):
            eco = by_title.get(line[3:].strip())
        elif eco and (m := ROW.match(line)):
            out[(eco, m["name"], m["version"].strip())] = m["licence"].strip()
    return out


def render_notices(components: list[dict]) -> str:
    lines = [
        "<!-- Written by tools/licence-check.py --write; the check fails when it is not current. -->",
        "# Third-party notices",
        "",
        "Farzan Arena uses the components below. Each is owned by its authors and is under its own licence, named here",
        "by its SPDX identifier; Farzan Arena's licences (LICENSING.md) do not cover them. Each licence's full text is in",
        "the component's own package (its LICENSE file), at <https://spdx.org/licenses/>, or both. The container images",
        "also carry their base distribution's packages, each under its own licence: LICENSING.md says where their source is.",
        "",
    ]
    for eco in TITLES:
        rows = sorted((c for c in components if c["ecosystem"] == eco), key=lambda c: (c["name"].lower(), c["version"]))
        if not rows:
            continue
        lines += [f"## {TITLES[eco]}", "", "| Component | Version | Licence |", "|---|---|---|"]
        lines += [f"| `{c['name']}` | {c['version']} | {c['licence']} |" for c in rows]
        lines.append("")
    return "\n".join(lines)


def components(pol: dict, ask_pypi: bool, need_nuget: bool, problems: list[str]) -> list[dict]:
    listed = read_notices()
    out = npm_components(pol)
    nuget = nuget_components(pol)
    if nuget is None:
        if need_nuget:
            problems.append("no restore here to read the .NET packages from: run ./tools/dn restore " + " and ".join(DOTNET_PROJECTS))
        nuget = [{"ecosystem": "nuget", "name": n, "version": v, "licence": lic} for (e, n, v), lic in listed.items() if e == "nuget"]
    out += nuget
    out += python_components(pol, {k: v for k, v in listed.items() if k[0] == "pypi"}, ask_pypi)
    out += program_components(pol)
    out += image_components(pol)
    return [overridden({**c, "licence": c["licence"] if c["licence"] == "NOT LISTED" else normalise(c["licence"], pol)}, pol)
            for c in out]


def check_components(comps: list[dict], pol: dict, today: date) -> list[str]:
    problems = []
    for c in comps:
        if c["licence"] == "NOT LISTED":
            problems.append(f"{c['ecosystem']} {c['name']} {c['version']}: not in THIRD-PARTY-NOTICES.md "
                            "(python3 tools/licence-check.py --write asks PyPI for its licence)")
            continue
        v = verdict(c["licence"], pol)
        if v != "denied" or accepted(c, pol, today):
            continue
        problems.append(f"{c['ecosystem']} {c['name']} {c['version']}: licence {c['licence']} is not allowed "
                        "(replace the package, or accept it in docs/compliance/licence-policy.yaml with why)")
    return problems


# ---------------------------------------------------------------- files, metadata, authors

def check_files(pol: dict) -> list[str]:
    problems = []
    for name in ("LICENSE.md", "LICENSING.md", "COMMERCIAL-LICENSE.md", "THIRD-PARTY-NOTICES.md", "CONTRIBUTING.md"):
        if not (ROOT / name).is_file():
            problems.append(f"{name} is missing")
    lic = ROOT / "LICENSE.md"
    if lic.is_file() and hashlib.sha256(lic.read_bytes()).hexdigest() != pol["agpl_sha256"]:
        problems.append("LICENSE.md is not the AGPL-3.0's text unchanged (its SHA-256 differs from the policy's agpl_sha256)")
    licensing = (ROOT / "LICENSING.md").read_text() if (ROOT / "LICENSING.md").is_file() else ""
    for must in ("AGPL-3.0-only", "COMMERCIAL-LICENSE.md", "THIRD-PARTY-NOTICES.md", SPDX):
        if must not in licensing:
            problems.append(f"LICENSING.md does not mention {must}")
    for pj in PACKAGE_JSONS:
        data = json.loads((ROOT / pj).read_text())
        if data.get("license") != SPDX:
            problems.append(f"{pj}: \"license\" is {data.get('license')!r}, not {SPDX!r}")
    for lock in LOCKS:
        root = json.loads((ROOT / lock).read_text()).get("packages", {}).get("", {})
        if root.get("license") != SPDX:
            problems.append(f"{lock}: the root package's \"license\" is {root.get('license')!r}, not {SPDX!r} (npm install updates it)")
    props = (ROOT / "Directory.Build.props").read_text()
    for want in (f"<Company>{HOLDER}</Company>", f"<Authors>{HOLDER}</Authors>",
                 f'<AssemblyMetadata Include="License" Value="{SPDX}" />'):
        if want not in props:
            problems.append(f"Directory.Build.props: no {want}")
    if not re.search(r"<Copyright>Copyright \(C\) \d{4}(-\d{4})? " + HOLDER, props):
        problems.append(f"Directory.Build.props: no <Copyright> naming {HOLDER}")
    for f in dockerfiles():
        if exempt(str(f.relative_to(ROOT)), pol):
            continue
        text = f.read_text()
        want = label_expected(str(f.relative_to(ROOT)), pol)
        if f'org.opencontainers.image.licenses="{want}"' not in text:
            problems.append(f"{f.relative_to(ROOT)}: no LABEL org.opencontainers.image.licenses=\"{want}\"")
    return problems


def check_authors(pol: dict) -> list[str]:
    owners = set(pol["owners"])
    problems = []
    log = git("log", "--all", "--format=%H%x1f%an%x1f%(trailers:key=Signed-off-by,valueonly,separator=;)%x1e")
    for rec in log.split("\x1e"):
        rec = rec.strip()
        if not rec:
            continue
        sha, author, signed = (rec.split("\x1f") + ["", ""])[:3]
        if author in owners:
            continue
        if signed.strip():
            continue
        problems.append(f"commit {sha[:12]} by {author}: not by an owner and not signed off (CONTRIBUTING.md, DCO)")
    return problems


def main(argv: list[str] | None = None, today: date | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--fix", action="store_true", help="add the SPDX and copyright lines where missing")
    ap.add_argument("--write", action="store_true", help="write THIRD-PARTY-NOTICES.md")
    ap.add_argument("--nuget", action="store_true", help="fail when no restore is here for the .NET packages")
    a = ap.parse_args(argv)
    today = today or date.today()
    pol = policy()
    problems: list[str] = []
    problems += check_headers(pol, a.fix, today.year)
    comps = components(pol, ask_pypi=a.write, need_nuget=a.nuget or a.write, problems=problems)
    rendered = render_notices(comps)
    if a.write:
        NOTICES.write_text(rendered)
    elif not NOTICES.is_file() or NOTICES.read_text() != rendered:
        problems.append("THIRD-PARTY-NOTICES.md is not current (python3 tools/licence-check.py --write)")
    problems += check_components(comps, pol, today)
    problems += check_files(pol)
    problems += check_authors(pol)
    review = sorted({f"{c['name']} ({c['licence']})" for c in comps if verdict(c["licence"], pol) == "review"})
    for p in problems:
        print(f"licence-check: {p}", file=sys.stderr)
    print(f"licence-check: {len(comps)} components ({len(review)} under weak copyleft, as separate libraries), "
          f"{len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
