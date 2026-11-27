#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
"""ISO's catalogue of every deliverable, from ISO Open Data, as the work directory of code-intel's `iso` knowledge pack.

    python3 tools/iso-catalog.py --fetch DIR            # the three files below, into DIR
    python3 tools/iso-catalog.py --from DIR --out OUT   # OUT/index.json and a page per entry
    code-intel pack build --source iso --work-dir OUT --out packs/iso-1.0.kpack --version 1.0

ISO Open Data (https://www.iso.org/open-data.html) publishes, under the Open Data Commons Attribution License v1.0
(ODC-By 1.0), the metadata of every ISO deliverable (published, withdrawn, under development and cancelled projects),
its technical committees and the International Classification for Standards (ICS). What is taken:

- a page per deliverable: its reference, English and French titles, status and stage, type, edition, publication date,
  pages, languages, ICS codes, committee, what it replaces and what replaced it, its amendments and corrigenda, the other
  editions and drafts of the same number, and its abstract as ISO gives it in the open data;
- a page per committee (its title, status, secretariat, scope as given, and its deliverables);
- a page per ICS code (its title, scope and notes, and the deliverables classified under it).

The standards' own texts are ISO's and sold by ISO; they are not taken (nor ISO's Online Browsing Platform, whose terms
forbid scraping it). ISO's former Publicly Available Standards site (standards.iso.org/ittf) is closed, and the free
copies the ISO and IEC webstores now give are licensed for one person's own use, not to be shared, so none is in the pack:
each page links to the standard's page on iso.org. ISO/IEC 27001 and 27002 link, too, to Farzan Arena's own mapping of its
controls (docs/compliance/frameworks/iso27001.md), which names their controls.

A deliverable's reference is its symbol ("ISO/IEC 27001:2022", "ISO/DIS 9001", "ISO/IEC 27001:2022/Amd 1:2024"), and so
is its undated number ("ISO 9001", "ISO/IEC 27001", "ISO/TS 19115"), on the edition that is current (else the newest);
an ISO/IEC number answers to "ISO 27001" too where no ISO deliverable has that number. Committees ("ISO/TC 176/SC 2")
and ICS codes ("ICS 35.030", "35.030") are symbols of their pages. Each status is a facet a search can be kept to (current, withdrawn,
development, cancelled), and so are the committees and the ICS. The provenance is the three files' SHA-256s.
Standard library only.
"""
from __future__ import annotations

import argparse
import hashlib
import html.parser
import json
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

OPEN_DATA = "https://isopublicstorageprod.blob.core.windows.net/opendata/_latest"
SOURCES = {
    "iso_deliverables_metadata.jsonl": f"{OPEN_DATA}/iso_deliverables_metadata/json/iso_deliverables_metadata.jsonl",
    "iso_technical_committees.jsonl": f"{OPEN_DATA}/iso_technical_committees/json/iso_technical_committees.jsonl",
    "ICS-simple.xml": f"{OPEN_DATA}/iso_ics/xml/ICS-simple.xml",
}
SHORT = {"iso_deliverables_metadata.jsonl": "deliverables", "iso_technical_committees.jsonl": "committees", "ICS-simple.xml": "ics"}
SOURCE_LINE = ("Source: ISO Open Data (iso_deliverables_metadata, iso_technical_committees, iso_ics), © ISO, "
               "https://www.iso.org/open-data.html, under the Open Data Commons Attribution License v1.0 (ODC-By 1.0). "
               "The standard's text is ISO's and is not in this pack: ISO sells it.")
GRC_LINK = ("Farzan Arena's own controls are mapped to this standard in docs/compliance/frameworks/iso27001.md (its control "
            "identifiers and names); the grc knowledge pack holds the frameworks whose texts may be shared (NIST's, the EU AI Act's).")
GRC_BASES = {"ISO/IEC 27001", "ISO/IEC 27002"}

TYPES = {"IS": "International Standard", "TS": "Technical Specification", "TR": "Technical Report", "R": "ISO Recommendation",
         "ISP": "International Standardized Profile", "PAS": "Publicly Available Specification", "GUIDE": "Guide",
         "IWA": "International Workshop Agreement", "DATA": "Data", "TTA": "Technology Trends Assessment"}
SUPPLEMENTS = {"Amd": "Amendment", "Cor": "Technical Corrigendum", "Add": "Addendum", "Suppl": "Supplement", "Ext": "Extension"}

# ISO's harmonized stage codes.
STAGES = {
    0: "Preliminary stage: proposal for new project received", 20: "Proposal for new project under review",
    60: "Close of review", 98: "Proposal for new project abandoned", 99: "Approval to ballot proposal for new project",
    1000: "Proposal stage: proposal for new project registered", 1020: "New project ballot initiated", 1060: "Close of voting",
    1092: "Proposal returned to submitter for further definition", 1098: "New project rejected", 1099: "New project approved",
    2000: "Preparatory stage: new project registered in TC/SC work programme", 2020: "Working draft (WD) study initiated",
    2060: "Close of comment period", 2098: "Project deleted", 2099: "WD approved for registration as CD",
    3000: "Committee stage: committee draft (CD) registered", 3020: "CD study initiated", 3060: "Close of voting/comment period",
    3092: "CD referred back to working group", 3098: "Project deleted", 3099: "CD approved for registration as DIS",
    4000: "Enquiry stage: DIS registered", 4020: "DIS ballot initiated: 12 weeks", 4060: "Close of voting",
    4092: "Full report circulated: DIS referred back to TC or SC", 4093: "Full report circulated: decision for new DIS ballot",
    4098: "Project deleted", 4099: "Full report circulated: DIS approved for registration as FDIS",
    5000: "Approval stage: final text received or FDIS registered for formal approval", 5020: "Proof sent to secretariat or FDIS ballot initiated: 8 weeks",
    5060: "Close of voting; proof returned by secretariat", 5092: "Proof or FDIS referred back to TC or SC", 5098: "Project deleted",
    5099: "FDIS or proof approved for publication",
    6000: "Publication stage: International Standard under publication", 6060: "International Standard published",
    9000: "Review stage", 9020: "International Standard under systematic review", 9060: "Close of review",
    9092: "International Standard to be revised", 9093: "International Standard confirmed",
    9099: "Withdrawal of International Standard proposed by TC or SC",
    9500: "Withdrawal stage", 9520: "Withdrawal ballot initiated", 9560: "Close of voting", 9592: "Decision not to withdraw International Standard",
    9599: "Withdrawal of International Standard",
}
BRIEF = {"current": "current", "withdrawn": "withdrawn", "development": "under development", "cancelled": "cancelled"}
STATUS_NAMES = {"current": "Published (current)", "withdrawn": "Withdrawn", "development": "Under development", "cancelled": "Cancelled (project deleted or abandoned)"}
# Stage words a draft's reference carries, dropped for its undated number; a draft TS or TR becomes the TS or TR.
DRAFT = {"PWI", "NP", "AWI", "WD", "CD", "DIS", "FDIS", "PRF"}
DRAFT_TYPE = {"DTS": "TS", "DTR": "TR", "DPAS": "PAS", "FDTS": "TS", "FDTR": "TR", "DIWA": "IWA"}
TYPE_WORDS = {"TS", "TR", "PAS", "ISP", "Guide", "GUIDE", "IWA", "R", "TTA", "DATA"}


def status(stage: int) -> str:
    """current (published, under review, or a withdrawal not yet decided), withdrawn, development, or cancelled."""
    if stage == 9599:
        return "withdrawn"
    if stage >= 6060:
        return "current"
    return "cancelled" if stage % 100 == 98 else "development"


def stage_text(stage: int) -> str:
    code = f"{stage // 100:02d}.{stage % 100:02d}"
    return f"{code} {STAGES[stage]}" if stage in STAGES else code


def base(reference: str) -> str:
    """The undated number a reference belongs to: "ISO/IEC 27001:2022/Amd 1:2024" and "ISO/IEC DIS 27001" -> "ISO/IEC 27001",
    "ISO/NP TS 19115-3" -> "ISO/TS 19115-3"."""
    m = re.match(r"^(.*?)\s*(\d[^:/]*)", reference)
    if not m:
        return reference
    designation, number = m.group(1), m.group(2).strip()
    orgs, kinds = [], []
    for word in re.split(r"[/ ]+", designation.strip()):
        if not word or word in DRAFT:
            continue
        word = DRAFT_TYPE.get(word, word)
        (kinds if word in TYPE_WORDS else orgs).append(word)
    designation = "/".join(orgs)
    if kinds:
        designation += ("/" if len(orgs) == 1 else " ") + " ".join(kinds)
    return f"{designation} {number}".strip()


def parent(reference: str) -> str | None:
    """The edition a supplement amends: "ISO/IEC 27001:2022/Amd 1:2024" -> "ISO/IEC 27001:2022"."""
    m = re.match(r"^(.*?\d[^/]*?)/(?:[A-Z]+ )*(?:D|F|FD|PD|CD |DIS |WD |NP |AWI |PWI )?(?:Amd|Cor|Add|Suppl|Ext)\b", reference)
    return m.group(1) if m else None


class Text(html.parser.HTMLParser):
    """An abstract's HTML as text: paragraphs and list items on their own lines, Word's conditional comments left out."""

    BLOCK = {"p", "div", "br", "ul", "ol", "table", "tr", "h1", "h2", "h3", "h4", "h5", "h6"}

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.parts: list[str] = []
        self.skip = 0

    def handle_starttag(self, tag: str, attrs) -> None:
        if tag in ("style", "script"):
            self.skip += 1
        elif tag == "li":
            self.parts.append("\n- ")
        elif tag in self.BLOCK:
            self.parts.append("\n\n")
        elif tag in ("td", "th"):
            self.parts.append(" ")

    def handle_endtag(self, tag: str) -> None:
        if tag in ("style", "script"):
            self.skip = max(0, self.skip - 1)
        elif tag in self.BLOCK:
            self.parts.append("\n\n")

    def handle_data(self, data: str) -> None:
        if not self.skip:
            self.parts.append(data)


def text(markup: str | None) -> str:
    if not markup:
        return ""
    p = Text()
    p.feed(markup)
    p.close()
    lines = [re.sub(r"[ \t ​]+", " ", line).strip() for line in "".join(p.parts).split("\n")]
    out, blank = [], False
    for line in lines:
        # Word's list bullets ("—" alone, or "- —") are the item's dash already.
        line = re.sub(r"^(?:- )?[—–·•]\s+", "- ", line)
        if line in ("—", "–", "·", "•"):
            continue
        if not line:
            blank = bool(out)
            continue
        if blank and not (line.startswith("- ") and out and out[-1].startswith("- ")):
            out.append("")
        out.append(line)
        blank = False
    return "\n".join(out).strip()


def lang(value, want: str = "en") -> str:
    if isinstance(value, dict):
        return value.get(want) or next((v for v in value.values() if v), "") or ""
    return value or ""


def slug(s: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", s.lower()).strip("-")


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_ics(src: Path) -> dict[str, dict]:
    root = ET.parse(src / "ICS-simple.xml").getroot()
    xl = "{http://www.w3.org/XML/1998/namespace}lang"
    codes = {}
    for e in root:
        code = (e.findtext("identifier") or "").strip()
        if not code:
            continue

        def en(tag: str, node=e) -> str:
            got = [x.text.strip() for x in node.findall(tag) if x.text and x.get(xl, "en") == "en"]
            return " ".join(got)

        refs = []
        for r in e.findall("references/reference"):
            ident = (r.findtext("identifier") or "").strip()
            note = en("note", r)
            if ident:
                refs.append((ident, note))
        codes[code] = {"code": code, "title": en("title"), "scope": en("scope"), "note": en("note"),
                       "parent": (e.findtext("group") or e.findtext("field") or "").strip(), "see": refs}
    return codes


def deliverable_line(d: dict) -> str:
    return f"{d['reference']}: {lang(d['title'])}"


def write(src: Path, out: Path) -> dict:
    deliverables = [json.loads(line) for line in (src / "iso_deliverables_metadata.jsonl").open(encoding="utf-8") if line.strip()]
    committees = {c["reference"]: c for c in
                  (json.loads(line) for line in (src / "iso_technical_committees.jsonl").open(encoding="utf-8") if line.strip())}
    ics = read_ics(src)
    by_id = {d["id"]: d for d in deliverables}
    by_ref: dict[str, list[dict]] = defaultdict(list)
    groups: dict[str, list[dict]] = defaultdict(list)
    supplements: dict[str, list[dict]] = defaultdict(list)
    for d in deliverables:
        d["_status"] = status(d["currentStage"])
        d["_base"] = base(d["reference"])
        by_ref[d["reference"]].append(d)
        if d.get("supplementType"):
            if (p := parent(d["reference"])) is not None:
                supplements[p].append(d)
        else:
            groups[d["_base"]].append(d)

    def order(d: dict) -> tuple:
        return (d.get("publicationDate") or "9999", d["id"])

    # The undated number answers on the current edition, else the newest of what there is.
    rank = {"current": 0, "development": 1, "withdrawn": 2, "cancelled": 3}
    undated: dict[int, list[str]] = defaultdict(list)
    for b, members in groups.items():
        newest = sorted(members, key=lambda d: (d.get("publicationDate") or "", d["id"]), reverse=True)
        best = min(newest, key=lambda d: rank[d["_status"]])
        undated[best["id"]].append(b)
    # "ISO 27001" for ISO/IEC 27001 (and ISO/IEC/IEEE, ISO/ASTM...), where no ISO deliverable has the number itself.
    for i, bs in list(undated.items()):
        for b in list(bs):
            designation, _, number = b.partition(" ")
            orgs = designation.split("/")
            if len(orgs) > 1 and orgs[0] == "ISO" and not set(orgs) & TYPE_WORDS and " " not in number and f"ISO {number}" not in groups:
                bs.append(f"ISO {number}")

    def path_of(d: dict) -> str:
        return f"{d['_status']}/{d['id']}-{slug(d['reference'])}.md"

    def named(i: int) -> str:
        d = by_id.get(i)
        if d is None:
            return f"deliverable {i} (not in the open data)"
        return f"{d['reference']} ({BRIEF[d['_status']]})"

    def link(d: dict) -> str:
        return f"https://www.iso.org/standard/{d['id']}.html"

    out.mkdir(parents=True, exist_ok=True)
    docs = []
    for d in sorted(deliverables, key=lambda d: d["id"]):
        ref, title = d["reference"], lang(d["title"])
        kind = TYPES.get(d["deliverableType"], d["deliverableType"])
        if d.get("supplementType"):
            kind = f"{SUPPLEMENTS.get(d['supplementType'], d['supplementType'])} to a {kind}"
        committee = d.get("ownerCommittee") or ""
        c = committees.get(committee)
        lines = [f"# {ref}", "", title]
        if (fr := lang(d["title"], "fr")) and fr != title and isinstance(d["title"], dict) and d["title"].get("fr"):
            lines += ["", f"French title: {fr}"]
        lines += ["", f"Status: {STATUS_NAMES[d['_status']]}, stage {stage_text(d['currentStage'])}.", f"Type: {kind}."]
        facts = []
        if d.get("edition"):
            facts.append(f"Edition {d['edition']}")
        if d.get("publicationDate"):
            facts.append(f"published {d['publicationDate']}")
        if (pages := (d.get("pages") or {}).get("en")):
            facts.append(f"{pages} pages")
        if facts:
            lines.append(", ".join(facts) + ".")
        if d.get("languages"):
            lines.append(f"Languages: {', '.join(d['languages'])}.")
        if committee:
            lines.append(f"Committee: {committee}" + (f", {lang(c['title'])}" if c else "") + ".")
        if d.get("icsCode"):
            lines.append("ICS: " + "; ".join(f"{x} {ics[x]['title']}" if x in ics else x for x in d["icsCode"]) + ".")
        if d.get("replaces"):
            lines.append("Replaces: " + ", ".join(named(i) for i in d["replaces"]) + ".")
        if d.get("replacedBy"):
            lines.append("Replaced by: " + ", ".join(named(i) for i in d["replacedBy"]) + ".")
        elif d["_status"] == "withdrawn":
            lines.append("Replaced by: nothing (withdrawn without a successor in the open data).")
        if (sups := sorted(supplements.get(ref, []), key=order)):
            lines += ["", "## Amendments and corrigenda", ""]
            lines += [f"- {s['reference']} ({BRIEF[s['_status']]})" for s in sups]
        others = [o for o in sorted(groups.get(d["_base"], []), key=order) if o["id"] != d["id"]]
        if others:
            lines += ["", f"## Other editions and drafts of {d['_base']}", ""]
            lines += [f"- {o['reference']} ({BRIEF[o['_status']]}" +
                      (f", {o['publicationDate']}" if o.get("publicationDate") else "") + ")" for o in others]
        if (abstract := text(lang(d.get("scope")))):
            lines += ["", "## Abstract", "", abstract]
        if d["_base"] in GRC_BASES:
            lines += ["", GRC_LINK]
        lines += ["", f"ISO's page of it: {link(d)}", "", SOURCE_LINE]
        path = path_of(d)
        (out / path).parent.mkdir(parents=True, exist_ok=True)
        (out / path).write_text("\n".join(lines) + "\n", encoding="utf-8")
        signature = f"{ref} ({BRIEF[d['_status']]}): {title}"
        kind_word = (d.get("supplementType") or d["deliverableType"]).lower()
        symbols = [{"name": ref, "kind": kind_word, "namespace": committee, "signature": signature}]
        symbols += [{"name": b, "kind": kind_word, "namespace": committee, "signature": signature}
                    for b in undated.get(d["id"], []) if b != ref]
        docs.append({"path": path, "title": f"{ref} {title}".strip(), "url": link(d), "symbols": symbols})

    def listing(members: list[dict]) -> list[str]:
        lines = []
        for st, heading in (("current", "Current"), ("development", "Under development")):
            got = sorted((m for m in members if m["_status"] == st), key=lambda m: m["reference"])
            if got:
                lines += ["", f"## {heading} ({len(got)})", ""] + [f"- {deliverable_line(m)}" for m in got]
        gone = sorted((m for m in members if m["_status"] == "withdrawn"), key=lambda m: m["reference"])
        if gone:
            lines += ["", f"## Withdrawn ({len(gone)})", "", ", ".join(m["reference"] for m in gone)]
        if (n := sum(1 for m in members if m["_status"] == "cancelled")):
            lines += ["", f"Cancelled projects: {n}."]
        return lines

    by_committee: dict[str, list[dict]] = defaultdict(list)
    for d in deliverables:
        if d.get("ownerCommittee"):
            by_committee[d["ownerCommittee"]].append(d)
    for ref in sorted(set(committees) | set(by_committee)):
        c = committees.get(ref)
        title = lang(c["title"]) if c else ""
        lines = [f"# {ref}" + (f" {title}" if title else "")]
        if c:
            facts = [f"Status: {c.get('status') or 'unknown'}"]
            if (sec := (c.get("secretariat") or {}).get("acronym")):
                facts.append(f"secretariat: {sec}")
            if c.get("creationDate"):
                facts.append(f"created {c['creationDate']}")
            lines += ["", ", ".join(facts) + "."]
            if (scope := text(lang(c.get("scope")))):
                lines += ["", "## Scope", "", scope]
        else:
            lines += ["", "Not in ISO's list of committees today (disbanded or renamed); its deliverables are below."]
        lines += listing(by_committee.get(ref, []))
        lines += ["", SOURCE_LINE.replace(" The standard's text is ISO's and is not in this pack: ISO sells it.", "")]
        path = f"committees/{slug(ref)}.md"
        (out / path).parent.mkdir(parents=True, exist_ok=True)
        (out / path).write_text("\n".join(lines) + "\n", encoding="utf-8")
        cid = c["id"] if c else None
        docs.append({"path": path, "title": lines[0][2:], "url": f"https://www.iso.org/committee/{cid}.html" if cid else "https://www.iso.org/technical-committees.html",
                     "symbols": [{"name": ref, "kind": "committee", "namespace": "ISO committees",
                                  "signature": f"{ref}: {title or 'a former committee'}"}]})

    by_ics: dict[str, list[dict]] = defaultdict(list)
    for d in deliverables:
        for x in d.get("icsCode") or []:
            by_ics[x].append(d)
    for code in sorted(set(ics) | set(by_ics)):
        e = ics.get(code, {"title": "", "scope": "", "note": "", "parent": "", "see": []})
        lines = [f"# ICS {code} {e['title']}".rstrip()]
        chain, p = [], e["parent"]
        while p and p in ics and p != code:
            chain.append(f"{p} {ics[p]['title']}")
            p = ics[p]["parent"]
        if chain:
            lines += ["", "In: " + " > ".join(reversed(chain)) + "."]
        if e["scope"]:
            lines += ["", f"Scope: {e['scope']}."]
        if e["note"]:
            lines += ["", f"Note: {e['note']}."]
        if e["see"]:
            lines += ["", "See also: " + "; ".join(f"{i} {n}".strip() for i, n in e["see"]) + "."]
        subs = sorted(k for k, v in ics.items() if v["parent"] == code and k != code)
        if subs:
            lines += ["", "## Codes under it", ""] + [f"- {k} {ics[k]['title']}" for k in subs]
        lines += listing(by_ics.get(code, []))
        lines += ["", SOURCE_LINE.replace(" The standard's text is ISO's and is not in this pack: ISO sells it.", "")]
        path = f"ics/{code}.md"
        (out / path).parent.mkdir(parents=True, exist_ok=True)
        (out / path).write_text("\n".join(lines) + "\n", encoding="utf-8")
        docs.append({"path": path, "title": lines[0][2:], "url": f"https://www.iso.org/ics/{code}.html",
                     "symbols": [{"name": name, "kind": "ics", "namespace": "ICS", "signature": f"ICS {code}: {e['title']}".rstrip(": ")}
                                 for name in ([f"ICS {code}", code] if "." in code else [f"ICS {code}"])]})

    provenance = ",".join(f"{SHORT[f]}={sha(src / f)[:12]}" for f in SOURCES)
    index = {"provenance": provenance, "docs": docs}
    (out / "index.json").write_text(json.dumps(index, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    return index


def fetch(dest: Path) -> None:
    """The three files, each swapped in only when whole."""
    dest.mkdir(parents=True, exist_ok=True)
    for name, url in SOURCES.items():
        part = dest / (name + ".part")
        subprocess.run(["curl", "-fsSL", "--retry", "3", "-o", str(part), url], check=True)
        part.replace(dest / name)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("--fetch", type=Path, help="download ISO Open Data's three files into this directory")
    p.add_argument("--from", dest="src", type=Path, help="the directory holding the three files")
    p.add_argument("--out", type=Path, help="the pack's work directory to write (replaced)")
    a = p.parse_args()
    if a.fetch:
        fetch(a.fetch)
    if a.src and a.out:
        if a.out.exists():
            shutil.rmtree(a.out)
        index = write(a.src, a.out)
        kinds = defaultdict(int)
        for d in index["docs"]:
            kinds[d["path"].split("/", 1)[0]] += 1
        print(f"iso-catalog: {len(index['docs'])} pages ({', '.join(f'{k} {v}' for k, v in sorted(kinds.items()))}) in {a.out}")
    elif not a.fetch:
        p.error("--fetch DIR, or --from DIR --out OUT")
    return 0


if __name__ == "__main__":
    sys.exit(main())
