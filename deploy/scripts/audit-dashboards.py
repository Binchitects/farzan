#!/usr/bin/env python3
"""Run every dashboard panel's queries in the app and judge the data.

Catches what a healthy-looking page hides: queries that error (bad PromQL,
LogQL or SQL), panels that are empty, null or NaN values, and percentages out
of range. Each panel is asked as its page asks it: through the app's
/api/dashboards API, signed in as the admin (the password comes from .env and
is never printed).

    python3 scripts/audit-dashboards.py              # metrics and logs over the last hour, as the pages open
    python3 scripts/audit-dashboards.py 6h           # another window (usage panels: 30 days)
    python3 scripts/audit-dashboards.py --json out.json
    python3 scripts/audit-dashboards.py --truth 7d    # the numbers checked against each other (see TRUTH)

Exit status: the number of queries that ERRORED. Empty panels are reported but
do not fail: many are empty by design until the matching traffic exists.
"""
import http.cookiejar
import json
import math
import ssl
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SQL_UIDS = {"gateway-db"}


def env(key, default=""):
    for line in (ROOT / ".env").read_text(encoding="utf-8").splitlines():
        if line.startswith(key + "="):
            return line.split("=", 1)[1].strip()
    return default


DOM = env("DOMAIN", "llm.localhost")
PORT = env("TRAEFIK_HTTPS_PORT", "443")
BASE = f"https://{DOM}" + ("" if PORT == "443" else f":{PORT}")
# What to trust: the stack's CA, or the certificate itself when an operator brought their own.
# Traefik's own certificate unless ACME_EMAIL gives a real one: then it is checked.
CTX = ssl.create_default_context() if env("ACME_EMAIL") else ssl._create_unverified_context()
OPENER = urllib.request.build_opener(urllib.request.HTTPSHandler(context=CTX),
                                     urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


def call(path, body=None):
    req = urllib.request.Request(BASE + path, data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json", "Accept": "application/json",
                                          "X-Requested-With": "audit"})
    try:
        with OPENER.open(req, timeout=120) as r:
            return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.load(e)
        except ValueError:
            return e.code, {"error": f"HTTP {e.code}"}


def seconds(text):
    return int(text[:-1]) * {"m": 60, "h": 3600, "d": 86400}[text[-1]]


def iso(t):
    return datetime.fromtimestamp(t, timezone.utc).isoformat()


def values(result):
    """Every number a target returned, and how many were null or NaN; log lines are counted apart."""
    nums, bad = [], 0
    for s in result.get("series") or []:
        for p in s["points"]:
            v = p[1] if len(p) > 1 else None
            if v is None or (isinstance(v, float) and (math.isnan(v) or math.isinf(v))):
                bad += 1
            else:
                nums.append(v)
    table = result.get("table")
    if table:
        numeric = [i for i, c in enumerate(table["columns"]) if c["type"] == "number"]
        for row in table["rows"]:
            for i in numeric:
                if row[i] is None:
                    bad += 1
                else:
                    nums.append(row[i])
    return nums, bad, len(result.get("logs") or [])


# Two panels that count the same thing from sources of their own, which must agree within the share given: what the
# engines say against what the gateway booked, the line's figures against each other. (dashboard, panel, how) twice: how is
# "sum" (every point of every series, or every number of a table), "sum:LEGEND" (the points of that series), or
# "col:NAME" (a table's column, summed).
TRUTH = [
    ("Tokens the engines made against tokens the gateway booked",
     ("stack-performance", "Token volume (per window)", "sum:generated"), ("stack-performance", "Tokens generated, by model", "col:Tokens generated"), 0.10),
    ("The stack's tokens a second now, two ways",
     ("stack-performance", "Stack throughput (live)", "last"), ("stack-performance", "Making now (tokens a second)", "last"), 0.02),
    ("Requests in the time window: LLM Overview against LLM and users",
     ("llm-overview", "Requests", "last"), ("llm-users", "Requests", "last"), 0.0),
]


def figure(result, how):
    """One number from a panel's targets, as the check says."""
    total, found = 0.0, False
    for t in result["results"]:
        if how == "last":
            for s in t.get("series") or []:
                pts = [p[1] for p in s["points"] if len(p) > 1 and isinstance(p[1], (int, float)) and not math.isnan(p[1])]
                if pts:
                    total, found = total + pts[-1], True
            tab = t.get("table")
            if tab and tab["rows"]:
                nums = [v for v in tab["rows"][0] if isinstance(v, (int, float))]
                if nums:
                    total, found = total + nums[-1], True
        elif how.startswith("sum"):
            legend = how[4:] if how.startswith("sum:") else None
            for s in t.get("series") or []:
                if legend is None or s.get("name") == legend:
                    for p in s["points"]:
                        if len(p) > 1 and isinstance(p[1], (int, float)) and not math.isnan(p[1]):
                            total, found = total + p[1], True
        elif how.startswith("col:"):
            tab = t.get("table")
            if tab:
                names = [c["name"] for c in tab["columns"]]
                if how[4:] in names:
                    i = names.index(how[4:])
                    for row in tab["rows"]:
                        if isinstance(row[i], (int, float)):
                            total, found = total + row[i], True
    return total if found else None


def truth(window):
    now = time.time()
    span = seconds(window)
    panels = {}

    def ask(uid, title, how):
        if uid not in panels:
            _, panels[uid] = call(f"/api/dashboards/{uid}")
        p = next((x for x in panels[uid].get("panels", []) if x.get("title") == title), None)
        if p is None:
            return None, f"no panel {title!r} on {uid}"
        code, r = call(f"/api/dashboards/{uid}/panels/{p['key']}/query", {"from": iso(now - span), "to": iso(now), "maxDataPoints": 2000})
        if code != 200:
            return None, f"HTTP {code}"
        return figure(r, how), None

    failed = 0
    print(f"Truth over {window}: each pair counts the same thing from its own source")
    for name, a, b, share in TRUTH:
        (x, ex), (y, ey) = ask(*a), ask(*b)
        if ex or ey or x is None or y is None:
            print(f"  ?     {name}: {ex or ey or 'no number'} ({x}, {y})")
            continue
        gap = abs(x - y) / max(abs(x), abs(y), 1e-9)
        ok = gap <= share + 1e-9
        failed += not ok
        print(f"  {'ok' if ok else 'GAP':5} {name}: {x:,.2f} against {y:,.2f}, {100 * gap:.1f}% apart (at most {100 * share:.0f}%)")
    return failed


def main():
    args = sys.argv[1:]
    dump = None
    if "--json" in args:
        i = args.index("--json")
        dump = args[i + 1]
        del args[i:i + 2]
    checking = "--truth" in args
    if checking:
        args.remove("--truth")
    window = args[0].removeprefix("now-") if args else "1h"
    code, body = call("/api/auth/login", {"userName": "admin", "password": env("ADMIN_PASSWORD")})
    if code != 200 or body.get("status") != "ok":
        sys.exit(f"cannot sign in to the app: HTTP {code} {body.get('error', '')}")
    if checking:
        return truth(window)

    now = time.time()
    rows = []
    _, dashboards = call("/api/dashboards/")
    for listed in dashboards:
        _, d = call(f"/api/dashboards/{listed['uid']}")
        for p in d["panels"]:
            if p["type"] in ("row", "text") or not p.get("supported"):
                continue
            # Usage panels read the requests' tables: a month, as their page opens.
            span = 30 * 86400 if SQL_UIDS & set(p.get("datasources", [])) else seconds(window)
            code, r = call(f"/api/dashboards/{d['uid']}/panels/{p['key']}/query",
                           {"from": iso(now - span), "to": iso(now), "maxDataPoints": 400})
            unit = ((p.get("fieldConfig") or {}).get("defaults") or {}).get("unit", "")
            base = {"dash": d["title"], "panel": p.get("title"), "unit": unit}
            if code != 200:
                rows.append({**base, "ref": "-", "verdict": f"ERROR HTTP {code}: {r.get('error', '')[:200]}"})
                continue
            for t in r["results"]:
                rec = {**base, "ref": t["refId"]}
                if t.get("error"):
                    rows.append({**rec, "verdict": "ERROR " + t["error"][:200]})
                    continue
                nums, bad, lines = values(t)
                problems = []
                if not nums and not lines:
                    problems.append("NO DATA")
                if bad:
                    problems.append(f"{bad} null/NaN")
                if nums and unit == "percentunit" and max(nums) > 1.0001:
                    problems.append(f"percentunit max {max(nums):.3g}")
                if nums and unit == "percent" and max(nums) > 100.01:
                    problems.append(f"percent max {max(nums):.3g}")
                rows.append({**rec, "verdict": "; ".join(problems) or "ok",
                             "min": min(nums) if nums else None, "max": max(nums) if nums else None, "lines": lines})

    if dump:
        Path(dump).write_text(json.dumps(rows, indent=1, default=str), encoding="utf-8")
    errors = [x for x in rows if x["verdict"].startswith("ERROR")]
    empty = [x for x in rows if x["verdict"] != "ok" and not x["verdict"].startswith("ERROR")]
    print(f"{len(rows)} panel queries (metrics and logs over {window}, usage over 30d): "
          f"{len(rows) - len(errors) - len(empty)} ok, {len(empty)} empty or partial, {len(errors)} errors")
    for x in errors:
        print(f"  ERROR  [{x['dash']}] {x['panel']} ({x['ref']}): {x['verdict'][6:]}")
    for x in empty:
        print(f"  note   [{x['dash']}] {x['panel']} ({x['ref']}): {x['verdict']}")
    return len(errors)


if __name__ == "__main__":
    sys.exit(main())
