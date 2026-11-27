#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
"""The Common Criteria knowledge pack (commoncriteria): its sources fetched, and its pages written from them.

    python3 tools/cc-texts.py --fetch DIR                 # the sources into DIR (.packwork/cc-sources)
    python3 tools/cc-texts.py --from DIR --out OUT        # the pack's work directory from them

--fetch takes, from the Common Criteria portal (commoncriteriaportal.org): the CC and CEM (CC:2022 Parts 1 to 5,
CEM:2022, CC and CEM 3.1 Revision 5, their errata and the CC's XML), the CCRA's supporting documents and operating
procedures, and every Protection Profile, PP-Module, PP-Configuration and Supporting Document its lists of certified,
archived and collaborative PPs name (their certification and maintenance reports are linked, not fetched). From
NIAP (niap-ccevs.org): every PP record with its files (PPs, PP-Modules, Packages, PP-Configurations, Supporting
Documents, the HTML editions; not the certificates, validation reports, control mappings or DoD annexes) and every
Technical Decision. A listing that fails fails the fetch, so a half listing never replaces a whole one; a file
already fetched (its URL, or NIAP's file id) is not fetched again. DIR/manifest.json records each file: where it came
from, its SHA-256 and size.

--from writes, from what was fetched, a page per CC and CEM section, per PP section, per component (FCS_COP.1,
ALC_FLR.2: its definition, and every PP that requires it, with how: mandatory, optional, selection-based or
objective), and per Technical Decision; OUT/index.json lists them with their symbols, OUT/requirements.json holds the
requirements as data (component -> the PPs), and each page ends with its document's terms. A PP whose notice does not
allow its text to be shared is a page of its metadata and link only (OUT/index.json's "metadata_only" names them).
This is the work directory of code-intel's `commoncriteria` pack:

    code-intel pack build --source commoncriteria --work-dir OUT --out packs/commoncriteria-1.0.kpack --version 1.0

tools/grc-catalogs.py keeps its catalog of the CC's families (docs/compliance/catalogs/commoncriteria.yaml) for the
company's documents; this pack holds the texts. Needs pdftotext (poppler). Standard library only.
"""
from __future__ import annotations

import argparse
import hashlib
import html
import json
import re
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

PORTAL = "https://www.commoncriteriaportal.org"
NIAP = "https://www.niap-ccevs.org"
NIAP_API = NIAP + "/api"
UA = "Mozilla/5.0 (compatible; farzan-arena-packs; +https://github.com/Binchitects/farzan)"
# The portal's pages whose lists are taken: name -> path.
PORTAL_PAGES = {"cc": "/cc/index.cfm", "pps": "/pps/index.cfm", "archived": "/pps/index.cfm?archived=1",
                "cpp": "/pps/collaborativePP.cfm?cpp=1"}
# Of the CC page's documents, the older revisions and the marked-up copies are left out (3.1 R5 is kept).
CC_SKIP = re.compile(r"marked|V3\.1R[1234]|cc3R[34]|R3\.pdf$|R4\.pdf$|\.dtd$", re.I)
# NIAP's file labels that are not a PP's text.
NIAP_SKIP = re.compile(r"certificate|validation report|control mapping|dod annex", re.I)
# Requests at once: NIAP takes some seconds for each.
WORKERS = 6


def say(*parts: object) -> None:
    print(*parts, flush=True)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def quote_url(url: str) -> str:
    """A URL as a request needs it: its path's spaces and brackets escaped, what is escaped already left as it is."""
    s = urllib.parse.urlsplit(url.strip())
    return urllib.parse.urlunsplit((s.scheme, s.netloc, urllib.parse.quote(urllib.parse.unquote(s.path)), s.query, ""))


def get(url: str, tries: int = 5) -> bytes:
    req = urllib.request.Request(quote_url(url), headers={"User-Agent": UA})
    for attempt in range(1, tries + 1):
        try:
            with urllib.request.urlopen(req, timeout=180) as r:
                return r.read()
        except (urllib.error.URLError, TimeoutError, ConnectionError) as exc:
            if isinstance(exc, urllib.error.HTTPError) and exc.code in (300, 400, 401, 403, 404, 410):
                raise
            if attempt == tries:
                raise
            time.sleep(3 * attempt)
    raise AssertionError


def get_json(url: str):
    return json.loads(get(url))


def page_var(page: str, name: str):
    """A JavaScript variable of a portal page that holds JSON (its ppList, maintList)."""
    m = re.search(r"var " + name + r"\s*=\s*", page)
    if not m:
        raise RuntimeError(f"the portal's page has no {name}: its layout changed")
    return json.JSONDecoder().raw_decode(page[m.end():])[0]


def text(s: str) -> str:
    return " ".join(html.unescape(re.sub(r"<[^>]+>", " ", s or "")).split())


# ------------------------------------------------------------------ the listings

def portal_listing() -> dict:
    """What the portal lists: the CC page's documents, and each PP of the certified, archived and collaborative lists."""
    pages = {k: get(PORTAL + p).decode("utf-8", "replace") for k, p in PORTAL_PAGES.items()}
    docs = []
    for m in re.finditer(r'<a\s+href="([^"]+)"[^>]*>(.*?)</a>', pages["cc"], re.I | re.S):
        href, label = html.unescape(m.group(1)).strip(), text(m.group(2))
        url = urllib.parse.urljoin(PORTAL + "/cc/", href)
        if CC_SKIP.search(href) or not re.search(r"\.(pdf|zip|xml)$|filename=[^&]*\.pdf", url, re.I):
            continue
        if url not in {d["url"] for d in docs}:
            docs.append({"url": url, "label": label})
    files = PORTAL + "/nfs/ccpfiles/files/ppfiles/"
    pps, cats, augs = [], {}, {}
    for key in ("pps", "archived", "cpp"):
        page = pages[key]
        if key != "cpp":
            cats.update({c["ID"]: c["Name"] for c in page_var(page, "categoriesList")})
            augs.update({str(x["ID"]): x["Abbr"].strip() for x in page_var(page, "augsList")})
        for p in page_var(page, "ppList"):
            entry = {"id": p["ID"], "list": key, "name": text(p["Name"]), "abbr": text(p.get("Abbr")), "version": text(p.get("Version")),
                     "eal": p.get("eal_name") or "", "scheme": p.get("Scheme") or "", "cpp": bool(p.get("cPP")),
                     "category": cats.get(p.get("Categories") or "", p.get("Categories") or ""), "issued": p.get("Issue_Date") or "",
                     "archived": p.get("Archived") or "", "files": [],
                     "augmented": [augs[a] for a in re.split(r"[,\s]+", str(p.get("Augs") or "")) if a in augs]}
            for field, kind in (("PDF_PP", "pp"), ("PDF_SD", "sd"), ("PDF_ES", "es"), ("PDF_TB", "toolbox"), ("PDF_Cert", "report")):
                name = (p.get(field) or "").strip()
                if name:
                    entry["files"].append({"kind": kind, "url": files + name})
            pps.append(entry)
        # The collaborative PPs' page also links their PP-Configurations, which ppList does not name.
        if key == "cpp":
            known = {f["url"] for e in pps for f in e["files"]}
            for m in re.finditer(r'<a\s+href="([^"]*/ppfiles/[^"]+\.pdf)"[^>]*>(.*?)</a>', page, re.I | re.S):
                url, label = urllib.parse.urljoin(PORTAL + "/pps/", html.unescape(m.group(1)).strip()), text(m.group(2))
                if url in known or re.search(r"report|-vr\b|_vr\b|inf-|ppcr", (url + " " + label).lower()):
                    continue
                known.add(url)
                kind = "sd" if "support" in (url + label).lower() else "pp"
                pps.append({"id": "cfg:" + Path(urllib.parse.unquote(url)).stem, "list": "cpp", "name": label if len(label) > 20 else
                            Path(urllib.parse.unquote(url)).stem, "abbr": Path(urllib.parse.unquote(url)).stem, "version": "", "eal": "",
                            "scheme": "", "cpp": True, "category": "Collaborative Protection Profiles", "issued": "", "archived": "",
                            "files": [{"kind": kind, "url": url}]})
    for key in ("pps", "archived"):
        if not any(p["list"] == key for p in pps):
            raise RuntimeError(f"the portal's {key} list is empty: its layout changed")
    maintenance = [{"pp": m.get("PP_ID"), "name": text(m.get("Name")), "date": m.get("Date") or "", "url": files + m["PDF_Report"]}
                   for m in page_var(pages["pps"], "maintList") if m.get("PDF_Report")]
    return {"docs": docs, "pps": pps, "maintenance": maintenance}


def niap_listing() -> dict:
    """NIAP's PP records, each with its public files, and every Technical Decision."""
    pps = get_json(NIAP_API + "/protection-profile/public_pps_all/")
    if len(pps) < 100:
        raise RuntimeError(f"NIAP listed {len(pps)} protection profiles: fewer than it has")
    def files_of(p):
        try:
            return get_json(f"{NIAP_API}/file/get_public_files_by_type_and_type_id/?file_type=protection-profile&file_type_id={p['pp_id']}")
        except urllib.error.HTTPError as exc:
            # NIAP answers 400 for a record that has no files.
            if exc.code in (400, 404):
                return []
            raise

    with ThreadPoolExecutor(WORKERS) as pool:
        lists = list(pool.map(files_of, pps))
    for p, got in zip(pps, lists):
        p["files"] = []
        for f in got if isinstance(got, list) else []:
            if NIAP_SKIP.search(f.get("file_label") or ""):
                continue
            if f.get("isFolder"):
                url = f"{NIAP}/static_html/protection-profile/{p['pp_id']}/{f['file_name']}/index.html"
            else:
                url = f"{NIAP_API}/file/get_public_file/?file_id={f['file_id']}"
            p["files"].append({"kind": f.get("file_label") or "", "url": url, "name": f.get("file_new_name") or f.get("file_name") or "",
                               "file_id": f.get("file_id"), "html": bool(f.get("isFolder")) or f.get("file_mime_type") == "text/html"})
    tds = get_json(NIAP_API + "/technical-decision/frontend_tds/")
    if len(tds) < 500:
        raise RuntimeError(f"NIAP listed {len(tds)} technical decisions: fewer than it has")
    return {"pps": pps, "tds": tds}


# ------------------------------------------------------------------ fetching

def local_name(url: str, niap_name: str = "") -> str:
    """Where a fetched file is kept under DIR/files: by its site and its own name."""
    s = urllib.parse.urlsplit(url)
    if "niap-ccevs.org" in s.netloc:
        q = urllib.parse.parse_qs(s.query)
        if "file_id" in q:
            safe = re.sub(r"[^A-Za-z0-9._-]+", "_", niap_name or "file")
            return f"niap/{q['file_id'][0]}-{safe}"
        return "niap/" + urllib.parse.unquote(s.path).strip("/").replace("static_html/protection-profile/", "html/")
    if "commoncriteriaportal.org" in s.netloc:
        path = urllib.parse.unquote(s.path).split("/files/", 1)[-1]
        return "portal/" + re.sub(r"[^A-Za-z0-9._/()\[\] -]+", "_", path)
    q = urllib.parse.parse_qs(s.query)
    name = q.get("filename", [Path(urllib.parse.unquote(s.path)).name])[0]
    return f"other/{s.netloc}/" + re.sub(r"[^A-Za-z0-9._()\[\] -]+", "_", name)


def fetch(dest: Path) -> int:
    dest.mkdir(parents=True, exist_ok=True)
    manifest_file = dest / "manifest.json"
    manifest = json.loads(manifest_file.read_text()) if manifest_file.exists() else {}
    say("cc-texts: listing the portal ...")
    portal = portal_listing()
    say(f"  {len(portal['docs'])} CC and CCRA documents, {len(portal['pps'])} PP records")
    say("cc-texts: listing NIAP ...")
    niap = niap_listing()
    say(f"  {len(niap['pps'])} PP records, {len(niap['tds'])} technical decisions")
    wanted = [(d["url"], "") for d in portal["docs"]]
    wanted += [(f["url"], "") for p in portal["pps"] for f in p["files"] if f["kind"] != "report"]
    wanted += [(f["url"], f["name"]) for p in niap["pps"] for f in p["files"]]
    todo = [(url, name) for url, name in dict.fromkeys(wanted)
            if not (manifest.get(url) and (dest / "files" / manifest[url]["file"]).is_file())]
    say(f"cc-texts: {len(todo)} files to fetch ({len(dict.fromkeys(wanted)) - len(todo)} fetched before)")

    def one(item):
        url, name = item
        target = dest / "files" / local_name(url, name)
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            data = get(url)
        except urllib.error.HTTPError as exc:
            # The portal's server answers a name it holds in another case with its choices (300): take the PDF it offers.
            choice = re.search(r'href="([^"]+\.pdf)"', exc.read().decode("utf-8", "replace"), re.I) if exc.code == 300 else None
            if not choice:
                return url, None, f"{url}: {exc}"
            try:
                data = get(urllib.parse.urljoin(url, choice.group(1)))
            except (urllib.error.URLError, TimeoutError, ConnectionError) as exc2:
                return url, None, f"{url}: {exc2}"
        except (urllib.error.URLError, TimeoutError, ConnectionError) as exc:
            return url, None, f"{url}: {exc}"
        part = target.with_name(target.name + ".part")
        part.write_bytes(data)
        part.rename(target)
        return url, {"file": str(target.relative_to(dest / "files")), "sha256": sha256(target), "size": target.stat().st_size}, None

    failed = []
    with ThreadPoolExecutor(WORKERS) as pool:
        for i, (url, entry, error) in enumerate(pool.map(one, todo), 1):
            if error:
                failed.append(error)
            else:
                manifest[url] = entry
            if i % 50 == 0:
                say(f"  {i} of {len(todo)}")
                manifest_file.write_text(json.dumps(manifest, indent=1, sort_keys=True) + "\n")
    # An HTML edition whose index.html only sends the browser on ("self.location=..."): the page it sends to.
    for url, entry in list(manifest.items()):
        if not url.endswith("/index.html") or entry["size"] > 2000:
            continue
        m = re.search(r'location\s*=\s*["\']([^"\']+)["\']', (dest / "files" / entry["file"]).read_text(errors="replace"))
        target_url = urllib.parse.urljoin(url, m.group(1)) if m else None
        if not target_url or (manifest.get(target_url) and (dest / "files" / manifest[target_url]["file"]).is_file()):
            continue
        got = one((target_url, ""))
        if got[2]:
            failed.append(got[2])
        else:
            manifest[target_url] = dict(got[1], edition_of=url)
    manifest_file.write_text(json.dumps(manifest, indent=1, sort_keys=True) + "\n")
    # Each Technical Decision's text (the list has only its title and status), kept: a decision's text does not change.
    tds = dest / "tds"
    tds.mkdir(exist_ok=True)
    missing = [t["identifier"] for t in niap["tds"] if not (tds / f"{t['identifier']}.json").is_file()]
    say(f"cc-texts: {len(missing)} technical decisions to fetch")

    def td(identifier):
        try:
            data = get(f"{NIAP_API}/technical-decision/frontend_by_identifier/{identifier}/")
        except (urllib.error.URLError, TimeoutError, ConnectionError) as exc:
            return f"{identifier}: {exc}"
        json.loads(data)
        (tds / f"{identifier}.json.part").write_bytes(data)
        (tds / f"{identifier}.json.part").rename(tds / f"{identifier}.json")
        return None

    with ThreadPoolExecutor(WORKERS) as pool:
        failed += [e for e in pool.map(td, missing) if e]
    # The listings last, whole: what --from reads.
    for name, value in (("portal.json", portal), ("niap.json", niap)):
        part = dest / (name + ".part")
        part.write_text(json.dumps(value, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        part.rename(dest / name)
    total = sum(v["size"] for v in manifest.values())
    say(f"cc-texts: {len(manifest)} files, {total / 2**20:.0f} MB in {dest / 'files'}; {len(failed)} not fetched")
    for f in failed:
        say("  not fetched: " + f)
    return 0


# ------------------------------------------------------------------ the CC's components, from its XML

CC_XML = {"cc2022": ("ccfiles/cc_2022.xml", "CC:2022"), "cc31": ("ccfiles/cc3R5.xml.zip", "CC 3.1 Revision 5")}
COMPONENT_ID = re.compile(r"^([A-Z]{3})_([A-Z0-9]{2,7})(?:_EXT)?\.(\d+)$")


def upper_id(xml_id: str) -> str:
    """An XML id as the CC prints it: fia_uau.1 -> FIA_UAU.1, alc_flr.2.1d -> ALC_FLR.2.1D, ace_ccl.1-1 -> ACE_CCL.1-1."""
    xml_id = xml_id or ""
    return xml_id.upper() if re.match(r"^[a-z]{3}(_[a-z0-9_]+)?", xml_id) else xml_id


def render(e, notes: list[str] | None = None) -> str:
    """An XML element's text as the CC prints it: [assignment: ...] and [selection: ...], references by their ids, lists
    as items. An assignment's or selection's notes go to `notes` (the component page prints them after its elements)."""
    tag = e.tag
    if tag == "xref":
        return upper_id(e.get("id", "")) if not e.get("id", "").startswith("fig") else "(figure)"
    if tag in ("assignment", "fe-assignment"):
        item = next((render(c, notes) for c in e if c.tag in ("assignmentitem", "fe-assignmentitem")), "")
        for c in e:
            if c.tag in ("assignmentnotes", "fe-assignmentnotes") and notes is not None:
                notes.append(f"Assignment ({item}): " + inner(c))
        return f"[assignment: {item}]"
    if tag in ("selection", "fe-selection"):
        items = [render(c, notes) for c in e if c.tag in ("selectionitem", "fe-selectionitem")]
        for c in e:
            if c.tag in ("selectionnotes", "fe-selectionnotes") and notes is not None:
                notes.append("Selection: " + inner(c))
        return "[selection: " + ", ".join(items) + "]"
    if tag in ("assignmentnotes", "selectionnotes", "fe-assignmentnotes", "fe-selectionnotes"):
        return ""
    return inner(e, notes)


def flat(s: str | None) -> str:
    """A text node's runs of white space as one space (the XML's indentation is not the text's)."""
    return re.sub(r"\s+", " ", s or "")


def inner(e, notes: list[str] | None = None) -> str:
    parts = [flat(e.text)]
    for c in e:
        if c.tag in ("list", "fe-list"):
            parts.append("\n" + "\n".join("- " + " ".join(render(i, notes).split()) for i in c if i.tag in ("item", "fe-item")) + "\n")
        elif c.tag in ("para",):
            parts.append("\n\n" + render(c, notes).strip() + "\n\n")
        elif c.tag == "row":
            parts.append("\n" + " | ".join(" ".join(render(x, notes).split()) for x in c) + "\n")
        elif c.tag in ("footnote", "ae-dc-element"):
            pass
        else:
            parts.append(render(c, notes))
        parts.append(flat(c.tail))
    s = "".join(parts)
    lines = [re.sub(r" ([.,;:])(?=\s|$)", r"\1", " ".join(x.split())) for x in s.split("\n")]
    return re.sub(r"\n{3,}", "\n\n", "\n".join(lines)).strip()


def paras(e, notes: list[str] | None = None) -> str:
    return render(e, notes) if e is not None else ""


def read_xml(path: Path):
    import xml.etree.ElementTree as ET
    import zipfile
    if path.suffix == ".zip":
        with zipfile.ZipFile(path) as z:
            name = next(n for n in z.namelist() if n.endswith(".xml"))
            return ET.fromstring(z.read(name))
    return ET.parse(path).getroot()


def catalogue(xml_root, edition: str) -> dict:
    """Each class, family and component of one edition: {id: {...}}, components with their elements, dependencies,
    hierarchy, audit and management (functional) or developer, content and evaluator elements with the CEM's work units
    (assurance), and the 3.1 XML's EALs as their components."""
    out = {"classes": {}, "families": {}, "components": {}, "eals": {}}
    for cls in xml_root:
        if cls.tag not in ("f-class", "a-class"):
            if cls.tag == "eal":
                out["eals"][cls.get("id").upper()] = [upper_id(c.get("acomponent") or c.get("id") or "") for c in cls.iter("eal-component")]
            continue
        kind = "functional" if cls.tag == "f-class" else "assurance"
        cid = cls.get("id").upper()
        intro = cls.find("fc-introduction") if kind == "functional" else cls.find("ac-introduction")
        out["classes"][cid] = {"id": cid, "name": cls.get("name"), "kind": kind, "text": paras(intro), "families": []}
        for fam in cls:
            if fam.tag not in ("f-family", "a-family"):
                continue
            fid = fam.get("id").upper()
            out["classes"][cid]["families"].append(fid)
            sections = []
            for tag, title in (("ff-behaviour", "Family behaviour"), ("af-objectives", "Objectives"), ("ff-application-notes", "Application notes"),
                               ("af-application-notes", "Application notes"), ("af-levelling-criteria", "Component levelling"),
                               ("ff-user-notes", "User notes"), ("ff-evaluator-notes", "Evaluator notes"), ("af-overview", "Overview")):
                for x in fam.findall(tag):
                    sections.append((title, paras(x)))
            out["families"][fid] = {"id": fid, "name": fam.get("name"), "class": cid, "sections": sections, "components": []}
            for comp in fam:
                if comp.tag not in ("f-component", "a-component"):
                    continue
                pid = comp.get("id").upper()
                out["families"][fid]["components"].append(pid)
                notes: list[str] = []
                c = {"id": pid, "name": comp.get("name"), "family": fid, "class": cid, "kind": kind, "edition": edition,
                     "hierarchical": [upper_id(h.get("fcomponent") or h.get("acomponent") or "") for h in comp.iter()
                                      if h.tag in ("fco-hierarchical", "aco-hierarchical")],
                     "dependencies": [], "elements": [], "sections": [], "audit": [], "management": [], "workunits": []}
                for dep in comp.iter():
                    if dep.tag in ("fco-dependencies", "aco-dependencies"):
                        alts = []
                        for d in dep:
                            if d.tag in ("fco-dependsoncomponent", "aco-dependsoncomponent"):
                                alts.append(upper_id(d.get("fcomponent") or d.get("acomponent") or ""))
                            elif d.tag in ("fco-or", "aco-or"):
                                alts.append(" or ".join(upper_id(x.get("fcomponent") or x.get("acomponent") or "") for x in d))
                        c["dependencies"] += [a for a in alts if a]
                    elif dep.tag == "aco-dependsoncomponent" and comp.find("aco-dependencies") is None:
                        c["dependencies"].append(upper_id(dep.get("acomponent") or ""))
                for x in comp:
                    if x.tag == "f-element":
                        c["elements"].append((upper_id(x.get("id")), render(x, notes)))
                    elif x.tag in ("ae-developer", "ae-content", "ae-evaluator"):
                        text_ = render(x, notes) if x.tag != "ae-evaluator" else " ".join((x.text or "").split())
                        if x.tag == "ae-evaluator":
                            text_ = " ".join(t for t in [text_] + [" ".join((y.text or "").split()) for y in x if y.tag == "para"] if t)
                        c["elements"].append((upper_id(x.get("id")), text_))
                        for w in x.iter("m-workunit"):
                            c["workunits"].append((upper_id(w.get("id")), "\n\n".join(render(p) for p in w if p.tag == "para")))
                    elif x.tag == "fco-audit":
                        c["audit"].append(((x.get("level") or "").strip(), render(x)))
                    elif x.tag == "fco-management":
                        c["management"].append(render(x))
                    elif x.tag in ("fco-rationale", "fco-user-notes", "fco-levelling", "aco-objectives", "aco-application-notes",
                                   "msa-objectives", "msa-input", "msa-application-notes"):
                        title = {"fco-rationale": "Rationale", "fco-user-notes": "User notes", "fco-levelling": "Levelling",
                                 "aco-objectives": "Objectives", "aco-application-notes": "Application notes",
                                 "msa-objectives": "Objectives of the evaluation (CEM)", "msa-input": "Input (CEM)",
                                 "msa-application-notes": "Application notes (CEM)"}[x.tag]
                        c["sections"].append((title, render(x)))
                c["notes"] = notes
                out["components"][pid] = c
    return out


def base_component(sfr: str) -> str:
    """The component an SFR or SAR names, without its iteration or element: FCS_COP.1/Hash, FCS_COP.1(1) and
    FCS_COP.1.1 -> FCS_COP.1; FCS_CKM_EXT.4 stays (an extended component is its own); ALC_FLR.2.1D -> ALC_FLR.2."""
    s = re.split(r"[/(\s]", sfr.strip(), maxsplit=1)[0].rstrip(".:")
    m = re.match(r"^([A-Z]{3}_[A-Z0-9]{2,7}(?:_EXT)?)\.(\d+)", s)
    return f"{m.group(1)}.{m.group(2)}" if m else s


# ------------------------------------------------------------------ a document's text, in sections

TOC_LINE = re.compile(r"^\s*((?:\d+(?:\.\d+)*|[A-Z](?:\.\d+)+|Annex [A-Z]|Appendix [A-Z]|[A-Z]))(?:[.:]|\s[-–—])?\s+(\S.*?)\s*(?:(?:\.\s*){4,}|\s{3,}|…+\s*)([0-9ivxlc]+)\s*$")
# A contents entry's page number at the end of its line (after leaders, or after a run of spaces).
PAGE_AT_END = re.compile(r"(?:\.{3,}|…|\s{3,})\s*[0-9ivxlc]+\s*$")
HEADING_LINE = re.compile(r"^\s{0,4}((?:\d+(?:\.\d+){0,4}|[A-Z](?:\.\d+){1,4}|(?:Annex|Appendix) [A-Z](?:\.\d+)*))(?:[.:]|\s[-–—])?\s{1,12}([A-Z][^\n]{1,120}?)\s*$")


def pdf_text(pdf: Path) -> str:
    """pdftotext's layout text; "" for a PDF with no text layer (a scan) or one pdftotext cannot read."""
    got = subprocess.run(["pdftotext", "-layout", "-enc", "UTF-8", str(pdf), "-"], capture_output=True, text=True, errors="replace")
    return got.stdout if got.returncode == 0 else ""


def html_text(page: str) -> str:
    """An HTML edition's text: its headings on lines of their own as "N.N Title", paragraphs and list items as lines."""
    page = re.sub(r"(?is)<(script|style|head)[^>]*>.*?</\1>", " ", page)
    page = re.sub(r"(?is)<h([1-6])[^>]*>(.*?)</h\1>", lambda m: "\n\n" + text(m.group(2)) + "\n\n", page)
    page = re.sub(r"(?i)<(?:br|/p|/div|/li|/tr|/table|/dt|/dd|/h[1-6])\b[^>]*>", "\n", page)
    page = re.sub(r"(?i)<li\b[^>]*>", "\n- ", page)
    page = re.sub(r"(?i)</t[dh]>", " | ", page)
    out = html.unescape(re.sub(r"<[^>]+>", " ", page)).replace("\xa0", " ")
    return "\n".join(re.sub(r" ([.,;:])(?=\s|$)", r"\1", " ".join(x.split())) for x in out.split("\n"))


def clean_pages(raw: str) -> list[str]:
    """The text's lines without the running headers and footers: a line (its digits aside) on more than a third of the
    pages, or a bare page number."""
    pages = raw.split("\f")
    seen: dict[str, int] = {}
    for p in pages:
        for key in {re.sub(r"\d+", "#", " ".join(x.split())) for x in p.split("\n") if x.strip()}:
            seen[key] = seen.get(key, 0) + 1
    limit = max(3, len(pages) // 3)
    lines = []
    for p in pages:
        for x in p.split("\n"):
            key = re.sub(r"\d+", "#", " ".join(x.split()))
            if seen.get(key, 0) > limit and len(pages) > 6:
                continue
            if re.fullmatch(r"\s*(?:Page\s+)?[0-9ivxlc]+(?:\s*(?:of|/)\s*\d+)?\s*", x, re.I):
                continue
            lines.append(x.rstrip())
    return lines


def sections(raw: str) -> list[tuple[str, str, int, str]]:
    """A document's sections as (number, title, depth, text), found by its table of contents: each entry's heading
    looked for in the body, in order. A document with no contents is cut at the numbered headings it has; one with
    neither is one section."""
    lines = clean_pages(raw)
    toc = []
    toc_end = 0
    toc_lines = set()
    for i, x in enumerate(lines[:2500]):
        # An entry whose title runs onto the next line, the leaders and page there.
        if i + 1 < len(lines) and not re.search(r"\.{4,}", x) and re.search(r"\.{4,}\s*[0-9ivxlc]+\s*$", lines[i + 1]) \
                and not TOC_LINE.match(lines[i + 1]) and re.match(r"^\s*(?:\d|[A-Z]\.\d|Annex|Appendix)", x):
            x = x.rstrip() + " " + lines[i + 1].strip()
            toc_lines.add(i + 1)
        m = TOC_LINE.match(x)
        if m:
            title = re.sub(r"^\((?:informative|normative)\)\s*", "", " ".join(m.group(2).split()))
            toc.append((m.group(1), title))
            toc_end = i
            toc_lines.add(i)
    heads = []  # (line index, number, title)
    if len(toc) >= 3:
        at = toc_end + 1
        for num, title in toc:
            ws = [re.escape(x) for x in title.split()[:4]]
            words = r"\s+".join(ws)
            # The title's first words, or fewer where the heading runs onto the next line.
            prefix = ws[0] + "".join(r"(?:\s+" + x for x in ws[1:]) + ")?" * (len(ws) - 1)
            pat = re.compile(r"^\s*" + re.escape(num) + r"(?:[.:]|\s[-–—])?\s+(?:" + words + "|" + prefix + r"\s*$)", re.I)
            pat2 = re.compile(r"^\s*" + words + r"\s*$", re.I)
            alone = re.compile(r"^\s*" + re.escape(num) + r"\.?\s*$")
            for j in range(at, min(len(lines), at + 8000)):
                if not lines[j].lstrip().startswith(num) or PAGE_AT_END.search(lines[j]):
                    continue
                if pat.match(lines[j]) or (alone.match(lines[j]) and (num.startswith(("Annex", "Appendix")) or pat2.match(
                        next((lines[k] for k in range(j + 1, min(j + 4, len(lines))) if lines[k].strip()), "")))):
                    heads.append((j, num, title))
                    at = j + 1
                    break
        # An annex's own sections (A.1, A.2.1), which contents seldom list.
        more = []
        for k, (j, num, title) in enumerate(heads):
            if not num.startswith(("Annex", "Appendix")):
                continue
            letter = num.split()[1]
            end = heads[k + 1][0] if k + 1 < len(heads) else len(lines)
            last = None
            for i in range(j + 1, end):
                m = re.match(r"^\s{0,4}(" + letter + r"(?:\.\d+){1,3})\.?\s{1,12}([A-Z][^\n]{1,100}?)\s*$", lines[i])
                if m and not re.search(r"\.{4,}", lines[i]) and (last is None or numbered_after(last, m.group(1))) \
                        and (last is not None or m.group(1) == letter + ".1"):
                    more.append((i, m.group(1), " ".join(m.group(2).split())))
                    last = m.group(1)
        heads = sorted(heads + more)
        # An annex the contents list but whose heading the body does not show as a line of its own: placed before its
        # first section, so its sections keep their annex ("Annex B: Selection-Based Security Requirements").
        found = {h[1] for h in heads}
        for num, title in toc:
            letter = num.split()[-1]
            if num in found or not re.fullmatch(r"(?:(?:Annex|Appendix) )?[A-Z]", num):
                continue
            child = next((h for h in heads if h[1].startswith(letter + ".")), None)
            if child:
                heads.append((child[0] - 0.5, num, title))
        # And one the contents leave out, from its heading in the body ("Appendix B: Selection-Based Requirements").
        found = {h[1] for h in heads}
        for j, x in enumerate(lines):
            m = re.match(r"^\s{0,4}((?:Annex|Appendix) ([A-Z]))\s*[:.\-–—]\s+([A-Z]\S.{2,90}?)\s*$", x)
            if not m or m.group(1) in found or PAGE_AT_END.search(x):
                continue
            # Only where it stands: after the section before its first one, and before that one.
            child = next((h for h in heads if h[1].startswith(m.group(2) + ".")), None)
            before = max((h[0] for h in heads if child and h[0] < child[0]), default=-1)
            if child and before < j < child[0]:
                heads.append((j, m.group(1), " ".join(m.group(3).split())))
                found.add(m.group(1))
        heads.sort()
    if len(heads) < 3:
        heads, last = [], None
        for j, x in enumerate(lines):
            m = HEADING_LINE.match(x)
            if m and not PAGE_AT_END.search(x) and not re.search(r"\s{3,}\S+\s{3,}", x) and len(m.group(2).split()) <= 14:
                heads.append((j, m.group(1), " ".join(m.group(2).split())))
        # Numbered lines that are not headings (lists, tables) seldom run in order: keep the ones that do.
        kept = []
        for h in heads:
            if not kept or numbered_after(kept[-1][1], h[1]):
                kept.append(h)
        heads = kept if len(kept) >= 3 else []
    out = []
    first = int(heads[0][0] + 0.5) if heads else len(lines)
    front = "\n".join(x for i, x in enumerate(lines[:first]) if i not in toc_lines and not re.search(r"\.{6,}\s*[0-9ivxlc]*\s*$", x)).strip()
    if front:
        out.append(("", "Front matter", 1, front))
    for k, (j, num, title) in enumerate(heads):
        end = int(heads[k + 1][0] + 0.5) if k + 1 < len(heads) else len(lines)
        # A placed annex heading (a half line) has no body of its own.
        body = "" if j != int(j) else "\n".join(lines[int(j) + 1:end]).strip("\n")
        depth = 1 + num.count(".") if not num.startswith(("Annex", "Appendix")) else 1 + num.count(".")
        out.append((num, title, depth, body))
    return out


def numbered_after(a: str, b: str) -> bool:
    """Whether heading number b can follow a: 3 -> 3.1, 3.1 -> 3.2, 3.2.4 -> 4, A.1 -> A.2."""
    pa, pb = a.split("."), b.split(".")
    if pa[0] != pb[0]:
        if pa[0].isdigit() and pb[0].isdigit():
            return int(pb[0]) == int(pa[0]) + 1 and len(pb) == 1
        return len(pb) <= 2 and (not pa[0].isdigit() or not pb[0].isdigit())
    for i in range(min(len(pa), len(pb))):
        if pa[i] != pb[i]:
            return i == len(pb) - 1 and pa[i].isdigit() and pb[i].isdigit() and int(pb[i]) == int(pa[i]) + 1
    return len(pb) == len(pa) + 1 and pb[-1] in ("1", "0")


def layout_text(body: str) -> str:
    """A section's lines as prose: indentation and column runs of spaces folded, paragraphs kept."""
    out = []
    for x in body.split("\n"):
        out.append(re.sub(r"\s{2,}", "  ", x.strip()))
    return re.sub(r"\n{3,}", "\n\n", "\n".join(out)).strip()


# ------------------------------------------------------------------ what a PP requires

# An element as a PP states it, at the start of its line (after a margin's line number, where the PP prints them):
# FIA_UAU.2.1, FCS_COP.1.1/Hash, FCS_CKM.1.1(1), ADV_FSP.4.2C.
ELEMENT = re.compile(r"^[\s|]{0,14}(?:\d{1,3}\s{1,8})?((?:F|A)[A-Z]{2}_[A-Z0-9]{2,7}(?:_EXT)?\.\d+\.\d+[CDE]?)((?:/[\w.\-]+|\([a-z0-9]{1,6}\))*)(?=[\s:.|]|$)")
# What follows a stated element: "shall" or an operation, in the languages the schemes write PPs in.
STATED = re.compile(r"shall|\[(?:selection|assignment|refinement)|\b(?:muss|müssen|soll|sollen)\b|\[(?:zuweisung|auswahl)|"
                    r"\bdoi(?:t|vent)\b|\[(?:affectation|s[ée]lection)|\bdeber[áa]n?\b|\bdebe\b|\[(?:asignaci[óo]n|selecci[óo]n)", re.I)
HOW = (("definition", re.compile(r"extended (?:security )?(?:functional |assurance )?component|component definition|definition of (?:the )?extended|"
                                 r"extended requirements? definition", re.I)),
       ("rationale", re.compile(r"rationale|dependenc|mapping|traceab", re.I)),
       ("conditional", re.compile(r"conditional(?:ly)? mandatory", re.I)),
       ("optional", re.compile(r"\boptional\b|strictly optional", re.I)),
       ("selection-based", re.compile(r"selection[- ]based|selection[- ]dependent", re.I)),
       ("objective", re.compile(r"objective (?:security )?(?:functional )?requirement|objective sfr", re.I)),
       ("implementation-dependent", re.compile(r"implementation[- ](?:dependent|based)", re.I)))


def requirements(secs: list[tuple[str, str, int, str]]) -> dict[str, dict]:
    """The SFRs and SARs a PP states, by component: {FIA_UAU.2: {"how": {"mandatory"}, "as": {"FIA_UAU.2.1"},
    "sections": {"5.1.3"}}}. An element counts where the PP states it (its id leading a line, "shall" or an operation
    after it), and how it is required comes from the headings it sits under: optional, selection-based, objective or
    implementation-dependent requirements, else mandatory. Extended component definitions, rationales and dependency
    tables name components without requiring them, and are left out."""
    found: dict[str, dict] = {}
    trail: list[tuple[int, str]] = []
    for num, title, depth, body in secs:
        while trail and trail[-1][0] >= depth:
            trail.pop()
        trail.append((depth, title))
        # The deepest heading that says how decides ("A.2 Objective Requirements" under "Appendix A Optional Requirements").
        how = next((h for _, t in reversed(trail) for h, rx in HOW if rx.search(t)), "mandatory")
        if how in ("definition", "rationale"):
            continue
        lines = body.split("\n")
        for i, line in enumerate(lines):
            m = ELEMENT.match(line)
            if not m:
                continue
            after = " ".join(lines[i:i + 4])[:600]
            if not STATED.search(after):
                continue
            comp = base_component(m.group(1))
            if not COMPONENT_ID.match(comp):
                continue
            r = found.setdefault(comp, {"how": set(), "as": set(), "sections": set()})
            r["how"].add(how)
            r["as"].add(m.group(1) + m.group(2))
            if num:
                r["sections"].add(num)
    return found


def html_sections(page: str) -> list[tuple[str, str, int, str]]:
    """An HTML edition's sections, by its <h1>..<h6>: (number, title, depth, text)."""
    page = re.sub(r"(?is)<(script|style|head)[^>]*>.*?</\1>", " ", page)
    parts = re.split(r"(?is)(<h[1-6][^>]*>.*?</h[1-6]>)", page)
    out = [("", "Front matter", 1, html_text(parts[0]))] if text(parts[0]) else []
    for i in range(1, len(parts), 2):
        level = int(parts[i][2])
        title = text(parts[i])
        m = re.match(r"^((?:\d+|[A-Z]|Appendix [A-Z]|Annex [A-Z])(?:\.\d+)*)(?:[.:]|\s[-–—])?\s+(.*)$", title)
        num, title = (m.group(1), m.group(2)) if m else ("", title)
        # The number says the depth (NIAP's editions do not keep their heading levels to it); an unnumbered heading
        # sits below the numbered one before it.
        if num:
            depth = 1 + (num.count(".") if not num.startswith(("Annex", "Appendix")) else num.count("."))
        else:
            depth = (out[-1][2] + 1) if out and out[-1][0] else max(1, level - 1)
        out.append((num, title, depth, html_text(parts[i + 1]) if i + 1 < len(parts) else ""))
    return out


# ------------------------------------------------------------------ each document's terms

CCRA_NOTICE = ("The CCRA's: the governmental organizations that hold the copyright with ISO/IEC grant every CC user the "
               "re-use of the CC's and CEM's text, and the publication of documents that use it (the Legal Notice in "
               "each part, and https://www.commoncriteriaportal.org/cc/)")
FORBID = re.compile(r"no part (?:of this (?:document|publication|specification) )?may be (?:reproduced|copied)|should not be reproduced|"
                    r"reproduction (?:is )?(?:strictly )?(?:prohibited|forbidden|not permitted)|prohibida su reproducci|reproduction et utilisation interdites|"
                    r"utilisation interdite|distribution of this document is restricted|recipients of this document|all rights reserved|"
                    r"alle rechte vorbehalten|tous droits r[ée]serv[ée]s", re.I)
GRANT = re.compile(r"reproduction of this document without any change or cut is authori[sz]ed|reproduction de ce document sans alt[ée]ration ni "
                   r"coupure est autoris|(?:this|the) (?:document|publication|protection profile|pp|specification) may be (?:freely )?"
                   r"(?:reproduced|copied|distributed)|permission is (?:hereby )?granted to (?:copy|reproduce|use|distribute)|creative commons|"
                   r"reproduction is authori[sz]ed|reuse is authori[sz]ed|(?:donate|put|putting|placed?) (?:this document |it )?in(?:to)? the public domain", re.I)
COPYRIGHT = re.compile(r"copyright|©|\(c\)\s*(?:19|20)\d\d", re.I)
GOVERNMENT = re.compile(r"bundesamt|federal office for information security|federal offce|\bBSI\b|ANSSI|SGDSN|DCSSI|premier ministre|"
                        r"centro cript|\bCCN\b|ministerio|ministry|ministère|\bKISA\b|national intelligence service|\bNIS\b|\bIPA\b|"
                        r"information-technology promotion agency|\bNIAP\b|national security agency|\bNSA\b|government|department of|"
                        r"\bNLNCSA\b|\bFMV\b|försvarets|communications security establishment|\bCSE\b|australian signals|\bASD\b|"
                        r"\bINTECO\b|\bENISA\b|european union|SOG-IS|\bCCRA\b|cyber security agency|\bSTQC\b|"
                        r"\bTSE\b|türk standardlar|national cyber security|nederlands|\bNCSC\b", re.I)


def terms(head: str, scheme: str, source: str, cpp: bool = False) -> dict:
    """A document's terms, from its first pages: {"code", "label", "text": the notice's words, "shared": whether its
    text is in the pack}. Shared: the CC and CEM (the CCRA's notice), what its notice lets anyone reproduce, the US
    government's works (NIAP's), and what a government or the CCRA publishes with no terms or a bare copyright line
    (a scheme's PP, a CCRA supporting document). Not shared: what a notice forbids to reproduce, and what a company or
    consortium holds the copyright of."""
    flat_ = " ".join(head.split())

    def around(m) -> str:
        """The notice's own sentences: the one the match is in, and the copyright line before it (never the text after)."""
        start = max(flat_.rfind(". ", 0, m.start()) + 2, m.start() - 200, 0)
        c = COPYRIGHT.search(flat_, max(0, m.start() - 200), m.start())
        if c and c.start() < start:
            start = c.start()
        end = flat_.find(".", m.end())
        end = len(flat_) if end < 0 else end + 1
        return flat_[start:min(end, m.end() + 160)].strip()
    if source == "cc":
        return {"code": "ccra-cc", "label": "CCRA notice: re-use and publication allowed", "text": CCRA_NOTICE, "shared": True}
    m = FORBID.search(flat_)
    if m:
        return {"code": "restricted", "label": "its notice restricts reproduction", "text": around(m), "shared": False}
    m = GRANT.search(flat_)
    if m:
        return {"code": "permitted", "label": "its notice allows reproduction", "text": around(m), "shared": True}
    m = COPYRIGHT.search(flat_)
    if m:
        holder = flat_[m.start():m.end() + 120]
        if GOVERNMENT.search(holder):
            return {"code": "government-copyright", "label": "a government publication (copyright line, no terms stated)", "text": around(m),
                    "shared": True}
        return {"code": "company-copyright", "label": "copyright of a company or consortium, no reuse terms", "text": around(m), "shared": False}
    if cpp:
        return {"code": "ccra-cpp", "label": "a collaborative PP of the CCRA's international technical communities, no terms stated",
                "text": "No notice: published free by the CC portal and NIAP for CC evaluations.", "shared": True}
    if scheme == "US" or source == "niap":
        return {"code": "us-government", "label": "a work of the United States government (17 U.S.C. 105), as NIAP publishes it",
                "text": "No copyright notice: published by NIAP (the US scheme).", "shared": True}
    return {"code": "government-no-notice", "label": "a government or CCRA publication with no terms stated",
            "text": f"No notice: published by the {scheme or 'CCRA'} scheme on its site or the CC portal.", "shared": True}


# ------------------------------------------------------------------ the pages

def slug(s: str) -> str:
    return re.sub(r"-{2,}", "-", re.sub(r"[^A-Za-z0-9._-]+", "-", s)).strip("-.") or "doc"


class Writer:
    """The work directory: each page written, and listed with its symbols."""

    def __init__(self, out: Path):
        self.out, self.docs, self.paths = out, [], set()

    def page(self, path: str, title: str, url: str, body: str, symbols: list[dict] | None = None, footer: str = "") -> str:
        if path in self.paths:
            raise RuntimeError(f"two pages at {path}")
        self.paths.add(path)
        target = self.out / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(body.rstrip() + ("\n\n---\n\n" + footer.strip() if footer else "") + "\n", encoding="utf-8")
        self.docs.append({"path": path, "title": title, "url": url, "symbols": symbols or []})
        return path


def symbol(name: str, kind: str, namespace: str, signature: str) -> dict:
    return {"name": name, "kind": kind, "namespace": namespace, "signature": signature}


def section_pages(w: Writer, base: str, doc_title: str, url: str, secs, footer: str) -> list[str]:
    """A document's sections as pages, one per heading of the first two levels, the deeper ones inside it."""
    groups: list[list] = []
    for s in secs:
        if not groups or s[2] <= 2:
            groups.append([s])
        else:
            groups[-1].append(s)
    paths = []
    for i, g in enumerate(groups):
        num, title, depth, body = g[0]
        head = " ".join(f"{doc_title} {num} {title}".split())
        lines = [f"# {head}", "", layout_text(body)]
        for n, t, d, b in g[1:]:
            lines += ["", "#" * min(6, d) + " " + " ".join(f"{n} {t}".split()), "", layout_text(b)]
        page = "\n".join(lines).strip()
        if len(g) == 1 and len(layout_text(body)) < 40:
            continue
        paths.append(w.page(f"{base}/{i:03d}-{slug(num or 'front')}.md", head, url, page, footer=footer))
    return paths


def footer_of(source: str, url: str, t: dict) -> str:
    return f"Source: {source}, {url}\n\nTerms: {t['label']}. {t['text']}"


REPORT = re.compile(r"certification report|validation report|rapport de certification|zertifizierungsreport|certificado de|"
                    r"certificat de|certificate of|maintenance report|assurance continuity", re.I)
DOCS = [(re.compile(r"CC2022PART(\d)R1\.pdf$"), "cc2022/part{0}", "CC:2022 Part {0}"),
        (re.compile(r"CEM2022R1\.pdf$"), "cem2022", "CEM:2022"),
        (re.compile(r"CCPART(\d)V3\.1R5\.pdf$"), "cc31/part{0}", "CC 3.1 R5 Part {0}"),
        (re.compile(r"CEMV3\.1R5\.pdf$"), "cem31", "CEM 3.1 R5")]
EDITIONS = {"cc2022": "CC:2022", "cc31": "CC 3.1 Revision 5"}
HOW_WORDS = {"mandatory": "mandatory", "conditional": "conditionally mandatory", "optional": "optional", "selection-based": "selection-based",
             "objective": "objective", "implementation-dependent": "implementation-dependent", "package": "in its assurance package"}
PP_ID = re.compile(r"\b(?:BSI-(?:CC-)?PP-\d{4}(?:-V\d+)?(?:-\d{4})?|ANSSI-CC-PP-\d{4}[/_-]\d{2}(?:-[A-Z0-9]+)?|DCSSI-PP-\d{4}[/_-]\d{2}|"
                   r"KECS-PP-\d{4}[a-z]?-\d{4}|JISEC-CC-PP-C\d{4}-\d{4}|CCN-CC-PP-\d+-\d{4}|NSCIB-CC-\d+-\w+|EUCC-\d+-\d{4}-PP-\d+)\b")


class Source:
    """What was fetched: the listings, the manifest of files, and each file's text (pdftotext's, kept by its SHA-256)."""

    def __init__(self, src: Path):
        self.src = src
        self.portal = json.loads((src / "portal.json").read_text(encoding="utf-8"))
        self.niap = json.loads((src / "niap.json").read_text(encoding="utf-8"))
        self.manifest = json.loads((src / "manifest.json").read_text())
        (src / "text").mkdir(exist_ok=True)

    def file(self, url: str) -> Path | None:
        m = self.manifest.get(url)
        # NIAP serves some of its HTML editions empty: no file, so the PDF is read instead.
        return self.src / "files" / m["file"] if m and m["size"] and (self.src / "files" / m["file"]).is_file() else None

    def sha(self, url: str) -> str:
        return self.manifest[url]["sha256"]

    def html_of(self, url: str) -> tuple[str, str]:
        """An HTML edition's page and its address: the page index.html sends the browser to, where it only does that."""
        for target, m in self.manifest.items():
            if m.get("edition_of") == url and self.file(target):
                return self.file(target).read_text(encoding="utf-8", errors="replace"), target
        return self.file(url).read_text(encoding="utf-8", errors="replace"), url

    def warm(self) -> None:
        """Every PDF's text extracted once, several at a time, into the cache the pages are written from."""
        todo = [u for u, m in self.manifest.items() if m["file"].lower().endswith(".pdf") or "get_public_file" in u]
        todo = [u for u in todo if self.file(u) and not (self.src / "text" / (self.sha(u) + ".txt")).is_file()]
        if todo:
            say(f"cc-texts: reading {len(todo)} PDFs ...")
            with ThreadPoolExecutor(8) as pool:
                list(pool.map(self.pdf, todo))

    def pdf(self, url: str) -> str:
        cached = self.src / "text" / (self.sha(url) + ".txt")
        if cached.is_file():
            return cached.read_text(encoding="utf-8")
        t = pdf_text(self.file(url))
        cached.write_text(t, encoding="utf-8")
        return t


def pp_records(src: Source) -> list[dict]:
    """The PPs, PP-Modules, PP-Configurations and packages, one record each however many lists name it: the portal's
    and NIAP's records of one PP (the same short name, or the same file) are merged."""
    recs = []
    for p in src.portal["pps"]:
        files = [dict(f, source="portal") for f in p["files"] if f["kind"] != "report"]
        reports = [f["url"] for f in p["files"] if f["kind"] == "report"]
        recs.append({"names": [p["name"]], "abbr": text(p["abbr"]), "version": p["version"], "scheme": p["scheme"], "category": p["category"],
                     "augmented": p.get("augmented", []),
                     "eal": p["eal"], "issued": p["issued"], "archived": p["archived"], "cpp": p["cpp"], "files": files, "reports": reports,
                     "portal": [p["id"]], "niap": [], "lists": [p["list"]]})
    for p in src.niap["pps"]:
        files = [{"kind": f["kind"], "url": f["url"], "source": "niap", "html": f["html"], "name": f["name"]} for f in p["files"]]
        recs.append({"names": [p["pp_name"]], "abbr": p.get("pp_short_name") or "", "version": "", "scheme": "US",
                     "category": p.get("tech_type") or "", "eal": "", "issued": (p.get("pp_date") or "")[:10],
                     "archived": (p.get("sunset_date") or "")[:10] if (p.get("sunset_date") or "") < time.strftime("%Y-%m-%d") else "",
                     "cpp": (p.get("pp_short_name") or "").upper().startswith(("CPP", "MOD_CPP")), "files": files, "reports": [],
                     "portal": [], "niap": [p["pp_id"]], "lists": ["niap"], "cc_version": p.get("cc_version") or "",
                     "sponsor": p.get("pp_sponsor_id") or ""})
    # Union by short name and by each file's checksum.
    parent = list(range(len(recs)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i
    seen: dict[str, int] = {}
    def norm(name: str) -> str:
        return re.sub(r"[\s_.-]+", "", name.upper())

    def stem(url: str) -> str:
        return Path(urllib.parse.unquote(urllib.parse.urlsplit(url).path)).stem

    for i, r in enumerate(recs):
        keys = [("abbr:" + norm(r["abbr"])) if r["abbr"] else None]
        keys += ["sha:" + src.sha(f["url"]) for f in r["files"] if src.file(f["url"]) and f["kind"] in ("pp", "Protection Profile", "Protection Profiles")]
        # A Supporting Document the collaborative PPs' page links on its own belongs to the PP of its name.
        if all(f["kind"] == "sd" for f in r["files"]) and r["files"] and str(r["portal"][0]).startswith("cfg:"):
            base = re.sub(r"(?i)[_ -]*(supporting[_ -]*doc(ument)?|sd)$|^sd[_ -]+", "", stem(r["files"][0]["url"]))
            keys.append("abbr:" + norm(base))
        else:
            keys += ["abbr:" + norm(stem(f["url"])) for f in r["files"] if f["kind"] == "pp" and f["source"] == "portal"]
        for k in filter(None, keys):
            if k in seen:
                parent[find(i)] = find(seen[k])
            else:
                seen[k] = i
    merged: dict[int, dict] = {}
    for i, r in enumerate(recs):
        root = find(i)
        if root not in merged:
            merged[root] = r
            continue
        m = merged[root]
        for k in ("names", "files", "reports", "portal", "niap", "lists"):
            m[k] += [x for x in r[k] if x not in m[k]]
        for k in ("abbr", "version", "category", "eal", "issued", "cc_version", "sponsor", "augmented"):
            if not m.get(k) and r.get(k):
                m[k] = r[k]
        m["archived"] = m["archived"] if m["archived"] and r["archived"] else ""
        m["cpp"] = m["cpp"] or r["cpp"]
    out = list(merged.values())
    used = set()
    for r in out:
        first = next((f for f in r["files"] if src.file(f["url"])), None)
        base = r["abbr"] or (Path(urllib.parse.unquote(urllib.parse.urlsplit(first["url"]).path)).stem if first else r["names"][0])
        s = slug(base)[:80]
        n = 2
        while s.lower() in used:
            s, n = f"{slug(base)[:76]}-{n}", n + 1
        used.add(s.lower())
        r["slug"] = s
        ids = sorted(set(PP_ID.findall(" ".join(r["names"] + [urllib.parse.unquote(f["url"]) for f in r["files"]]))))
        r["ids"] = ids
    return out


def doc_kind(f: dict) -> str:
    k = (f.get("kind") or "").lower()
    if k in ("sd",) or "supporting doc" in k:
        return "sd"
    if k in ("pp", "es", "toolbox") or "protection profile" in k or "pp configuration" in k or "pp-configuration" in k or \
            "pp-module" in k or "package" in k:
        return "pp"
    return "other"


def texts(src_dir: Path, out: Path) -> int:
    src = Source(src_dir)
    src.warm()
    if out.exists():
        import shutil
        shutil.rmtree(out)
    out.mkdir(parents=True)
    w = Writer(out)
    metadata_only = []

    # The CC's catalogue, both editions.
    cats = {}
    for key, (path, title) in CC_XML.items():
        url = f"{PORTAL}/files/{path}"
        if not src.file(url):
            sys.exit(f"cc-texts: {url} was not fetched")
        cats[key] = catalogue(read_xml(src.file(url)), title)

    # The CC, CEM and CCRA documents, each a page per section.
    for d in src.portal["docs"]:
        url = d["url"]
        f = src.file(url)
        if not f or f.suffix.lower() != ".pdf":
            continue
        name = f.name
        hit = next(((rx.search(name), base, title) for rx, base, title in DOCS if rx.search(name)), None)
        if hit:
            base, title = hit[1].format(*hit[0].groups()), hit[2].format(*hit[0].groups())
            t = terms("", "", "cc")
            if base == "cem31":
                # The portal's grant for 3.1 names CC Parts 1 to 3; the CEM 3.1 is the CCRA's publication under its own Legal Notice.
                t = {"code": "ccra-cem31", "label": "the CCRA's publication, under its Legal Notice (the CC portal's re-use grant for 3.1 R5 "
                     "names CC Parts 1 to 3, and the CCRA's policy supports the texts' re-use)", "text": CCRA_NOTICE, "shared": True}
        else:
            base, title = "ccra/" + slug(f.stem)[:80], (d["label"] if len(d["label"]) > 12 else f.stem)
            t = terms(src.pdf(url)[:30000], "", "ccra")
        raw = src.pdf(url)
        if not raw.strip():
            continue
        if t["shared"]:
            section_pages(w, base, title, url, sections(raw), footer_of("Common Criteria portal", url, t))
        else:
            metadata_only.append({"document": title, "url": url, "terms": t["label"], "notice": t["text"]})

    # The PPs.
    required: dict[str, list[dict]] = {}
    evaluated: dict[str, list[dict]] = {}
    written: dict[str, str] = {}  # a file's checksum -> the page its text is on
    bases: set[str] = set()
    records = pp_records(src)
    for r in records:
        title = r["names"][0]
        reqs: dict[str, dict] = {}
        files_lines = []
        notices = []
        # Prefer an HTML edition for the text and the requirements: it keeps the headings.
        files = sorted((f for f in r["files"] if src.file(f["url"])), key=lambda f: (doc_kind(f) != "pp", not f.get("html"), f["source"] != "niap"))
        has_html_pp = any(f.get("html") and doc_kind(f) == "pp" for f in files)
        for f in files:
            url = f["url"]
            kind = doc_kind(f)
            if f.get("html"):
                page, url_shown = src.html_of(url)
                secs = html_sections(page)
                head = html_text(page[:200000])[:30000]
            else:
                raw = src.pdf(url)
                secs = sections(raw) if raw.strip() else []
                head = raw[:30000]
                url_shown = url
                if not raw.strip():
                    files_lines.append(f"- {f.get('name') or Path(urllib.parse.unquote(url)).name} (no text layer): {url}")
                    continue
            label = f.get("name") or Path(urllib.parse.unquote(urllib.parse.urlsplit(url).path)).name
            # A certification report or certificate the portal files as the PP: linked, as its reports are, not its text.
            if kind == "pp" and REPORT.search(" ".join(head[:1500].split())) and not any(c.startswith("F") for c in requirements(secs)):
                files_lines.append(f"- certification report or certificate: {label}, {url_shown}")
                continue
            t = terms(head, r["scheme"], f["source"], r["cpp"])
            files_lines.append(f"- {f['kind']}: {label}, {url_shown} ({t['label']})")
            notices.append(t)
            if kind == "pp" and not (has_html_pp and not f.get("html")):
                for comp, x in requirements(secs).items():
                    e = reqs.setdefault(comp, {"how": set(), "as": set(), "sections": set()})
                    for k in e:
                        e[k] |= x[k]
            elif kind == "sd":
                for comp in requirements(secs):
                    evaluated.setdefault(comp, []).append({"slug": r["slug"], "title": title, "url": url_shown})
            if has_html_pp and kind == "pp" and not f.get("html") and f["kind"] in ("pp", "Protection Profile", "Protection Profiles"):
                files_lines[-1] += "; its text: the HTML edition's"
                continue
            sha = src.sha(url_shown) if url_shown in src.manifest else src.sha(url)
            if sha in written:
                files_lines[-1] += f"; its text: {written[sha]}"
                continue
            if not t["shared"]:
                metadata_only.append({"document": f"{title}: {label}", "url": url_shown, "terms": t["label"], "notice": t["text"]})
                continue
            base = ("sd/" if kind == "sd" else "pp/") + r["slug"] + "/" + slug(Path(label).stem)[:60]
            # Two copies of a document that differ (the portal's and NIAP's): the second beside the first.
            n, first = 2, base
            while base in bases:
                base, n = f"{first}-{n}", n + 1
            bases.add(base)
            paths = section_pages(w, base, f"{r['abbr'] or title}", url_shown, secs, footer_of(
                "NIAP" if f["source"] == "niap" else "Common Criteria portal", url_shown, t))
            if paths:
                written[sha] = paths[0]
        # The assurance package the portal records (EAL4+, its augmentations) for a PP that does not state its SARs.
        eal = re.match(r"EAL(\d)", r["eal"] or "")
        if eal and not any(c.startswith("A") for c in reqs):
            package = cats["cc31"]["eals"].get(f"EAL{eal.group(1)}", [])
            for comp in r.get("augmented", []):
                # An augmentation replaces the package's component of its family (ALC_DVS.2 for EAL4's ALC_DVS.1).
                package = [c for c in package if c.rsplit(".", 1)[0] != comp.rsplit(".", 1)[0]]
                reqs.setdefault(comp, {"how": {"package"}, "as": {f"EAL{eal.group(1)}+ augmented"}, "sections": set()})
            for comp in package:
                reqs.setdefault(comp, {"how": {"package"}, "as": {f"EAL{eal.group(1)}"}, "sections": set()})
        r["requirements"] = {c: {"how": sorted(v["how"]), "as": sorted(v["as"]), "sections": sorted(v["sections"])} for c, v in sorted(reqs.items())}
        for comp, v in r["requirements"].items():
            required.setdefault(comp, []).append({"slug": r["slug"], "title": title, "abbr": r["abbr"], "scheme": r["scheme"],
                                                  "how": v["how"], "as": v["as"], "archived": r["archived"]})
        r["notices"] = notices
        r["files_lines"] = files_lines

    # The Technical Decisions.
    tds_by_comp: dict[str, list[tuple[str, str, str]]] = {}
    tds_by_pp: dict[int, list[tuple[str, str, str]]] = {}
    niap_title = {p["pp_id"]: p["pp_name"] for p in src.niap["pps"]}
    for t in sorted(src.niap["tds"], key=lambda t: t["identifier"]):
        ident = t["identifier"]
        body_file = src_dir / "tds" / f"{ident}.json"
        full = json.loads(body_file.read_text()) if body_file.is_file() else {}
        pps = full.get("protection_profile") or t.get("protection_profile") or []
        sunset = full.get("sunset_protection_profile") or t.get("sunset_protection_profile") or []
        lines = [f"# {ident}: {t['title']}", "",
                 f"NIAP Technical Decision, {t.get('status') or ''}, published {(t.get('publication_date') or '')[:10]}."
                 + (f" Archived {t['removed_on'][:10]}." if t.get("removed_on") not in (None, "", "None") else ""), ""]
        if t.get("related_to"):
            lines += [f"Applies to: {t['related_to']}", ""]
        if pps:
            lines += ["## Protection Profiles", ""] + [f"- {p.get('pp_name') or niap_title.get(p.get('pp_id'), '')}" for p in pps] + [""]
        if sunset:
            lines += ["## Protection Profiles since sunset", ""] + [f"- {p.get('pp_name', '')}" for p in sunset] + [""]
        for key, head in (("description", "Issue"), ("resolution", "Resolution"), ("justification", "Justification")):
            if full.get(key):
                lines += [f"## {head}", "", html_text(full[key]).strip(), ""]
        url = f"{NIAP}/technical-decisions/{ident}"
        w.page(f"td/{ident}.md", f"{ident} {t['title']}", url, "\n".join(lines),
               [symbol(ident, "technical-decision", "NIAP", f"NIAP {ident}: {t['title']} ({t.get('status') or ''})")],
               footer_of("NIAP", url, terms("", "US", "niap")))
        for token in re.split(r"[,;]\s*", t.get("related_to") or ""):
            comp = base_component(token)
            if COMPONENT_ID.match(comp):
                tds_by_comp.setdefault(comp, []).append((ident, t["title"], t.get("status") or ""))
        for p in pps:
            tds_by_pp.setdefault(p.get("pp_id"), []).append((ident, t["title"], t.get("status") or ""))

    # Each PP's own page: what it is, where its files are, under what terms, and what it requires.
    for r in records:
        title = r["names"][0]
        lines = [f"# {title}", ""]
        facts = [("Short name", r["abbr"]), ("Also listed as", "; ".join(r["names"][1:])), ("Identifiers", ", ".join(r["ids"])),
                 ("Version", r["version"]), ("Scheme", r["scheme"]), ("Category", r["category"]),
                 ("Assurance", " ".join([r["eal"]] + (["augmented with " + ", ".join(r.get("augmented", []))] if r.get("augmented") else []))), ("CC version", r.get("cc_version", "")), ("Sponsor", r.get("sponsor", "")),
                 ("Certified or published", r["issued"]), ("Archived or sunset", r["archived"]),
                 ("Collaborative PP", "yes" if r["cpp"] else ""),
                 ("Listed by", ", ".join(sorted(set({"pps": "the CC portal (certified PPs)", "archived": "the CC portal (archived PPs)",
                                                     "cpp": "the CC portal (collaborative PPs)", "niap": "NIAP"}[x] for x in r["lists"]))))]
        lines += [f"- {k}: {v}" for k, v in facts if v] + [""]
        links = [f"- CC portal: {PORTAL}/pps/index.cfm#{i}" for i in r["portal"] if not str(i).startswith("cfg:")]
        links += [f"- NIAP: {NIAP}/protectionprofiles/{i}" for i in r["niap"]]
        links += [f"- Certification report: {u}" for u in r["reports"]]
        lines += ["## Where it is", ""] + links + r["files_lines"] + [""]
        if r["requirements"]:
            lines += ["## Requirements", "", "The SFRs and SARs it states (or its assurance package names), by component:", "",
                      "| Component | How | As stated |", "|---|---|---|"]
            for comp, v in r["requirements"].items():
                lines.append(f"| {comp} | {', '.join(HOW_WORDS.get(h, h) for h in v['how'])} | {', '.join(v['as'][:8])} |")
            lines.append("")
        tds = [x for i in r["niap"] for x in tds_by_pp.get(i, [])]
        if tds:
            lines += ["## Technical Decisions", ""] + [f"- {i} ({s}): {t}" for i, t, s in sorted(set(tds))] + [""]
        if not any(n["shared"] for n in r["notices"]) and r["notices"]:
            lines += ["## Terms", "", "Its text is not in this pack: " + r["notices"][0]["label"] + ". " + r["notices"][0]["text"], ""]
        names = [r["abbr"]] if r["abbr"] else []
        names += [i for i in r["ids"] if i not in names]
        syms = [symbol(n, "protection-profile", "Common Criteria PP", f"{n}: {title}" + (f" ({r['scheme']})" if r["scheme"] else ""))
                for n in names]
        url = (f"{NIAP}/protectionprofiles/{r['niap'][0]}" if r["niap"] else
               next((f["url"] for f in r["files"] if doc_kind(f) == "pp"), r["files"][0]["url"] if r["files"] else f"{PORTAL}/pps/"))
        w.page(f"pp/{r['slug']}/index.md", title, url, "\n".join(lines), syms,
               "Source: " + ", ".join(sorted({"the Common Criteria portal" if f["source"] == "portal" else "NIAP" for f in r["files"]}) or {"NIAP"}))

    # Each component: its definition in each edition, the PPs that require it, the SDs that evaluate it, its TDs.
    comps: dict[str, dict[str, dict]] = {}
    for key, c in cats.items():
        for cid, x in c["components"].items():
            comps.setdefault(cid, {})[key] = x
    for cid in sorted(set(comps) | set(required)):
        m = re.match(r"^([A-Z]{3})_([A-Z0-9]+(?:_EXT)?)\.\d+$", cid)
        if not m:
            continue
        cls, fam = m.group(1), f"{m.group(1)}_{m.group(2)}"
        defs = comps.get(cid, {})
        name = next((x["name"] for x in defs.values()), "")
        kind = "SFR" if cls.startswith("F") else "SAR"
        lines = [f"# {cid} {name}".strip(), ""]
        if defs:
            any_def = next(iter(defs.values()))
            fam_name = cats.get("cc2022", {}).get("families", {}).get(fam, {}).get("name") or cats["cc31"]["families"].get(fam, {}).get("name", "")
            cls_name = (cats["cc2022"]["classes"].get(cls) or cats["cc31"]["classes"].get(cls) or {}).get("name", "")
            lines += [f"Common Criteria {'Part 2 security functional' if kind == 'SFR' else 'Part 3 security assurance'} component, "
                      f"class {cls} {cls_name}, family {fam} {fam_name}. In: {', '.join(EDITIONS[k] for k in defs)}.", ""]
        else:
            lines += [f"An extended component (not in the CC): defined by the PPs that use it, family {fam}.", ""]
        for key, x in defs.items():
            lines += [f"## {EDITIONS[key]}", ""]
            lines.append("Hierarchical to: " + (", ".join(x["hierarchical"]) or "no other components."))
            lines.append("Dependencies: " + (", ".join(x["dependencies"]) or "no dependencies."))
            lines += ["", "### Elements", ""] + [f"{i} {t}" for i, t in x["elements"]]
            if x["notes"]:
                lines += ["", "### Operations", ""] + [f"- {n}" for n in x["notes"]]
            for head, body in x["sections"]:
                lines += ["", f"### {head}", "", body]
            if x["management"]:
                lines += ["", "### Management", ""] + [f"- {n}" for n in x["management"]]
            if x["audit"]:
                lines += ["", "### Audit", ""] + [f"- {lvl}: {n}" if lvl else f"- {n}" for lvl, n in x["audit"]]
            if x["workunits"]:
                lines += ["", "### CEM work units", ""] + [f"{i}: {t}" for i, t in x["workunits"]]
            lines.append("")
        users = sorted(required.get(cid, []), key=lambda u: (bool(u["archived"]), u["scheme"], u["title"]))
        lines += [f"## Protection Profiles that require {cid}", ""]
        if users:
            lines += [f"{len(users)} PPs, PP-Modules and packages state it ({sum(1 for u in users if not u['archived'])} current).", "",
                      "| PP | Scheme | How | As stated | Archived |", "|---|---|---|---|---|"]
            lines += [f"| {u['title']}{' (' + u['abbr'] + ')' if u['abbr'] else ''} | {u['scheme']} | "
                      f"{', '.join(HOW_WORDS.get(h, h) for h in u['how'])} | {', '.join(u['as'][:6])} | {u['archived']} |" for u in users]
        else:
            lines.append("None of the PPs in this pack states it.")
        sds = evaluated.get(cid, [])
        if sds:
            lines += ["", "## Supporting Documents with its evaluation activities", ""] + sorted({f"- {s['title']}: {s['url']}" for s in sds})
        tds = tds_by_comp.get(cid, [])
        if tds:
            lines += ["", "## NIAP Technical Decisions", ""] + [f"- {i} ({s}): {t}" for i, t, s in sorted(set(tds))]
        url = f"{PORTAL}/cc/" if defs else (f"{NIAP}/protectionprofiles" if users else f"{PORTAL}/pps/")
        sig = f"{cid} {name}: {kind}" + (f", required by {len(users)} PPs" if users else "")
        w.page(f"components/{cls}/{cid}.md", f"{cid} {name}".strip(), url, "\n".join(lines),
               [symbol(cid, "component", "Common Criteria", sig)], footer_of("the Common Criteria's XML (cc_2022.xml, cc3R5.xml)", f"{PORTAL}/cc/",
                                                                              terms("", "", "cc")) if defs else "")

    # The families and classes, from both editions, and the EALs.
    fams = {}
    for key, c in cats.items():
        for fid, x in c["families"].items():
            fams.setdefault(fid, {})[key] = x
    for fid, by in sorted(fams.items()):
        cls = fid[:3]
        x = by.get("cc2022") or by["cc31"]
        lines = [f"# {fid} {x['name']}", "", f"Common Criteria family {fid}, class {cls}. In: {', '.join(EDITIONS[k] for k in by)}.", ""]
        for key, y in by.items():
            lines += [f"## {EDITIONS[key]}", ""]
            for head, body in y["sections"]:
                lines += [f"### {head}", "", body, ""]
            lines += ["### Components", ""] + [f"- {c} {cats[key]['components'][c]['name']}" for c in y["components"]] + [""]
        w.page(f"components/{cls}/{fid}.md", f"{fid} {x['name']}", f"{PORTAL}/cc/", "\n".join(lines),
               [symbol(fid, "family", "Common Criteria", f"{fid} {x['name']}: family of class {cls}")],
               footer_of("the Common Criteria's XML", f"{PORTAL}/cc/", terms("", "", "cc")))
    for cid in sorted({c for cat in cats.values() for c in cat["classes"]}):
        x = cats["cc2022"]["classes"].get(cid) or cats["cc31"]["classes"][cid]
        fam_ids = sorted({f for cat in cats.values() for f in cat["classes"].get(cid, {}).get("families", [])})
        lines = [f"# Class {cid}: {x['name']}", "", x["text"], "", "## Families", ""] + [f"- {f} {fams[f].get('cc2022', fams[f].get('cc31'))['name']}" for f in fam_ids]
        w.page(f"components/{cid}/index.md", f"Class {cid}: {x['name']}", f"{PORTAL}/cc/", "\n".join(lines),
               [symbol(cid, "class", "Common Criteria", f"{cid} {x['name']}: {x['kind']} class")],
               footer_of("the Common Criteria's XML", f"{PORTAL}/cc/", terms("", "", "cc")))
    for eal, members in sorted(cats["cc31"]["eals"].items()):
        users = [r for r in records if (r["eal"] or "").startswith(eal)]
        lines = [f"# {eal}", "", f"Evaluation assurance level {eal[3:]}: its assurance components (CC 3.1 R5 Part 3; CC:2022 Part 5 keeps them).", "",
                 "## Components", ""] + [f"- {c} {cats['cc31']['components'].get(c, {}).get('name', '')}" for c in members]
        lines += ["", f"## Protection Profiles at {eal}", ""] + [f"- {r['names'][0]} ({r['eal']}, {r['scheme']})" for r in users]
        w.page(f"components/EAL/{eal}.md", eal, f"{PORTAL}/cc/", "\n".join(lines), [symbol(eal, "package", "Common Criteria", f"{eal}: assurance package")],
               footer_of("the Common Criteria's XML", f"{PORTAL}/cc/", terms("", "", "cc")))

    # The requirements as data, and the index.
    data = {"components": {c: sorted(u, key=lambda u: u["slug"]) for c, u in sorted(required.items())},
            "pps": {r["slug"]: {"title": r["names"][0], "abbr": r["abbr"], "scheme": r["scheme"], "archived": r["archived"],
                                "requirements": r["requirements"]} for r in records}}
    (out / "requirements.json").write_text(json.dumps(data, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    h = hashlib.sha256()
    for url in sorted(src.manifest):
        h.update(f"{url} {src.manifest[url]['sha256']}\n".encode())
    for t in sorted(src.niap["tds"], key=lambda t: t["identifier"]):
        h.update(f"{t['identifier']} {t.get('status')} {t.get('removed_on')}\n".encode())
    provenance = f"files={len(src.manifest)},tds={len(src.niap['tds'])},sha256={h.hexdigest()[:16]}"
    (out / "index.json").write_text(json.dumps({"provenance": provenance, "docs": w.docs, "metadata_only": metadata_only},
                                               ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    say(f"cc-texts: {len(w.docs)} pages: {len(records)} PPs, {sum(1 for c in required)} components required, "
        f"{len(src.niap['tds'])} technical decisions; {len(metadata_only)} documents as metadata and link only")
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--fetch", type=Path, help="fetch the sources into this folder")
    p.add_argument("--from", dest="src", type=Path, help="the folder --fetch wrote")
    p.add_argument("--out", type=Path, help="write the pack's work directory here")
    a = p.parse_args(argv)
    if a.fetch:
        return fetch(a.fetch)
    if a.src and a.out:
        return texts(a.src, a.out)
    p.error("--fetch DIR, or --from DIR --out OUT")
    return 2


if __name__ == "__main__":
    sys.exit(main())
