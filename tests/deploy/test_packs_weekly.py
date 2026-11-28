# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
"""
tools/packs-weekly.py: which pack of a name is the newest (a week's date after 1.0, 11.12 after 11.5), what keeps a new
pack out (another embedding model, far fewer pages), the index of the newest of each name with where each is served (a
pack unchanged this week where it was released before), and pruning to the two newest of each name.
"""
from __future__ import annotations

import importlib.util
import json
import os
import sqlite3
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "tools" / "packs-weekly.py"
spec = importlib.util.spec_from_file_location("packs_weekly", SCRIPT)
pw = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pw)


def pack(path: Path, name: str, version: str, docs: int = 100, model: str = "nomic-embed-text", commit: str = "abc") -> Path:
    con = sqlite3.connect(path)
    con.execute("create table pack_meta (key text primary key, value text)")
    con.executemany("insert into pack_meta values (?, ?)", [("source_name", name), ("pack_version", version), ("doc_count", str(docs)),
                    ("embedding_model", model), ("embedding_dim", "768"), ("source_commit", commit), ("license", "MIT"), ("attribution", "them")])
    con.commit()
    con.close()
    return path


class PacksWeekly(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.packs = Path(self.tmp.name)
        pw.PACKS = self.packs

    def tearDown(self):
        self.tmp.cleanup()

    def test_Another_checkouts_library_is_renewed_with_this_ones_code(self):
        saved = pw.WORK, pw.DOCSRC, pw.PACKS
        try:
            (self.packs / "live" / "packs").mkdir(parents=True)
            pack(self.packs / "live" / "packs" / "cpp-2026.11.12.kpack", "cpp", "2026.11.12")
            pw.use_library(self.packs / "live")
            self.assertEqual((self.packs / "live" / "packs").resolve(), pw.PACKS)
            self.assertEqual((self.packs / "live" / ".packwork" / "docsrc").resolve(), pw.DOCSRC)
            self.assertEqual((self.packs / "live" / ".packwork").resolve(), pw.WORK)
            self.assertEqual(["cpp"], list(pw.newest()))
        finally:
            pw.WORK, pw.DOCSRC, pw.PACKS = saved

    def test_The_newest_of_each_name_is_by_the_version_it_records(self):
        pack(self.packs / "cpp.kpack", "cpp", "1.0")
        pack(self.packs / "cpp-2026.11.5.kpack", "cpp", "2026.11.5")
        pack(self.packs / "cpp-2026.11.12.kpack", "cpp", "2026.11.12")
        pack(self.packs / "sqlite.kpack", "sqlite", "1.0")
        (self.packs / "broken.kpack").write_text("not a pack")
        newest = pw.newest()
        self.assertEqual({"cpp": "cpp-2026.11.12.kpack", "sqlite": "sqlite.kpack"}, {n: p.name for n, (p, _) in newest.items()})

    def test_A_pack_with_another_model_or_far_fewer_pages_is_kept_out_unless_forced(self):
        old = (self.packs / "a.kpack", {"embedding_model": "nomic-embed-text", "embedding_dim": "768", "doc_count": "1000"})
        self.assertIsNone(pw.check(pack(self.packs / "b.kpack", "x", "2026.11.12", docs=900), old, False))
        self.assertIn("pages", pw.check(pack(self.packs / "c.kpack", "x", "2026.11.12", docs=500), old, False))
        self.assertIn("embedding model", pw.check(pack(self.packs / "d.kpack", "x", "2026.11.12", model="other"), old, False))
        self.assertIsNone(pw.check(self.packs / "c.kpack", old, True))
        # The first of its name, or one before of a handful of pages (a fixture), is no measure.
        self.assertIsNone(pw.check(self.packs / "c.kpack", None, False))
        self.assertIsNone(pw.check(self.packs / "c.kpack", (old[0], {**old[1], "doc_count": "5"}), False))

    def test_The_index_lists_the_newest_of_each_with_where_it_is_served(self):
        unchanged = pack(self.packs / "sqlite.kpack", "sqlite", "1.0")
        (self.packs / "published.json").write_text(json.dumps({"sqlite.kpack": {
            "url": "https://example.test/packs-2026.11.05/sqlite.kpack", "sha256": pw.sha256(unchanged), "release": "packs-2026.11.05"}}))
        pack(self.packs / "cpp.kpack", "cpp", "1.0")
        new = pack(self.packs / "cpp-2026.11.12.kpack", "cpp", "2026.11.12")
        index, sums = pw.write_index("2026.11.12", [new], publish=True)
        # Remembered only once the release holds them.
        self.assertNotIn("cpp-2026.11.12.kpack", json.loads((self.packs / "published.json").read_text()))
        pw.remember_published()
        entries = {e["name"]: e for e in json.loads(index.read_text())["packs"]}
        self.assertEqual("2026.11.12", entries["cpp"]["version"])
        self.assertTrue(entries["cpp"]["url"].endswith("/releases/download/packs-2026.11.12/cpp-2026.11.12.kpack"))
        self.assertEqual("https://example.test/packs-2026.11.05/sqlite.kpack", entries["sqlite"]["url"])
        self.assertIn(f"{pw.sha256(new)}  cpp-2026.11.12.kpack", sums.read_text())
        published = json.loads((self.packs / "published.json").read_text())
        self.assertEqual("packs-2026.11.12", published["cpp-2026.11.12.kpack"]["release"])
        self.assertEqual("packs-2026.11.05", published["sqlite.kpack"]["release"])

    class FakeGitHub:
        """gh and curl as the release step calls them: a release (or none), its files, uploads that fail as told."""

        def __init__(self, exists=False, held=None, fail=lambda cmd, n: False):
            self.exists, self.held, self.fail, self.calls, self.notes, self.inputs = exists, dict(held or {}), fail, [], [], []

        def __call__(self, cmd, **kw):
            import subprocess
            self.calls.append(cmd)
            ok = lambda out="": subprocess.CompletedProcess(cmd, 0, out, "")
            if cmd[:3] == ["gh", "auth", "token"]:
                return ok("TOKEN")
            if cmd[:3] == ["gh", "release", "view"]:
                if not self.exists:
                    return subprocess.CompletedProcess(cmd, 1, "", "release not found")
                assets = [{"name": n, "size": z, "state": st, "apiUrl": f"https://api.github.com/repos/x/y/releases/assets/{n}"}
                          for n, (z, st) in self.held.items()]
                return ok(json.dumps({"uploadUrl": "https://uploads.github.com/repos/x/y/releases/1/assets{?name,label}", "assets": assets}))
            if cmd[:3] == ["gh", "release", "create"]:
                self.exists = True
            if "--notes-file" in cmd:
                self.notes.append(Path(cmd[cmd.index("--notes-file") + 1]).read_text())
            if cmd[:3] == ["gh", "api", "-X"]:
                self.held.pop(cmd[-1].rsplit("/", 1)[-1], None)
            if cmd[0] == "curl":
                self.inputs.append(kw.get("input", ""))
                name = cmd[-1].split("?name=", 1)[1]
                tries = sum(1 for c in self.calls if c[0] == "curl" and c[-1] == cmd[-1])
                if self.fail(cmd, tries):
                    self.held[name] = (1, "starter")
                    return subprocess.CompletedProcess(cmd, 28, "000", "curl: (28) Operation too slow")
                self.held[name] = (Path(cmd[cmd.index("--data-binary") + 1][1:]).stat().st_size, "uploaded")
                return ok("201")
            return ok()

        def uploads(self):
            return [c for c in self.calls if c[0] == "curl"]

    def released(self, fake, *args):
        saved = pw.subprocess.run, pw.time.sleep, os.environ.get("HTTPS_PROXY")
        pw.subprocess.run, pw.time.sleep = fake, lambda _: None
        os.environ["HTTPS_PROXY"] = "http://proxy.test:3128"
        try:
            return pw.release(*args)
        finally:
            pw.subprocess.run, pw.time.sleep = saved[0], saved[1]
            if saved[2] is None:
                os.environ.pop("HTTPS_PROXY", None)
            else:
                os.environ["HTTPS_PROXY"] = saved[2]

    def test_The_release_is_a_draft_until_every_file_is_up_and_a_stalled_upload_is_tried_again_the_other_way(self):
        new = pack(self.packs / "cpp-2026.11.12.kpack", "cpp", "2026.11.12")
        index, sums = pw.write_index("2026.11.12", [new], True)
        fake = self.FakeGitHub(fail=lambda cmd, n: cmd[-1].endswith(".kpack") and n == 1)
        self.assertTrue(self.released(fake, "2026.11.12", [], index, sums))
        create = next(c for c in fake.calls if c[:3] == ["gh", "release", "create"])
        self.assertEqual("packs-2026.11.12", create[3])
        self.assertIn("--draft", create)
        ups = fake.uploads()
        self.assertEqual(4, len(ups))  # the pack twice, index.json, SHA256SUMS
        # Straight first, then through the proxy; the partial file of the stalled try removed before the next.
        self.assertIn("--noproxy", ups[0])
        self.assertEqual("http://proxy.test:3128", ups[1][ups[1].index("-x") + 1])
        self.assertIn(["gh", "api", "-X", "DELETE", "repos/x/y/releases/assets/cpp-2026.11.12.kpack"], fake.calls)
        # Stalls are cut, and the token is never on a command line.
        self.assertEqual("60", ups[0][ups[0].index("--speed-time") + 1])
        self.assertFalse(any("TOKEN" in " ".join(c) for c in ups))
        self.assertEqual("Authorization: Bearer TOKEN\n", fake.inputs[0])
        # Shown only after the last upload.
        self.assertEqual(["gh", "release", "edit", "packs-2026.11.12", "--repo", pw.REPO, "--draft=false", "--latest=false"], fake.calls[-1])

    def test_A_file_that_never_uploads_stops_the_release_a_draft(self):
        new = pack(self.packs / "cpp-2026.11.12.kpack", "cpp", "2026.11.12")
        index, sums = pw.write_index("2026.11.12", [new], True)
        fake = self.FakeGitHub(exists=True, fail=lambda cmd, n: True)
        with self.assertRaises(RuntimeError):
            self.released(fake, "2026.11.12", [], index, sums)
        # The day's release already there is taken up (no second create), and it is never shown.
        self.assertNotIn(["gh", "release", "create"], [c[:3] for c in fake.calls])
        self.assertEqual(pw.UPLOAD_TRIES, len(fake.uploads()))
        self.assertNotIn("--draft=false", [x for c in fake.calls for x in c])
        self.assertFalse((self.packs / "published.json").exists())

    def test_A_second_run_that_day_sends_only_what_the_release_lacks_and_keeps_every_packs_line(self):
        first = pack(self.packs / "cpp-2026.11.12.kpack", "cpp", "2026.11.12")
        pw.write_index("2026.11.12", [first], True)
        pw.remember_published()
        second = pack(self.packs / "qt-2026.11.12.kpack", "qt", "2026.11.12")
        index, sums = pw.write_index("2026.11.12", [second], True)
        fake = self.FakeGitHub(exists=True, held={first.name: (first.stat().st_size, "uploaded"), "index.json": (1, "uploaded")})
        self.assertTrue(self.released(fake, "2026.11.12", ["- **qt** 2026.11.12: 12,000 pages"], index, sums))
        sent = [c[-1].split("?name=", 1)[1] for c in fake.uploads()]
        self.assertEqual(["qt-2026.11.12.kpack", "index.json", "SHA256SUMS"], sent)
        # The index is always sent again: the one there is removed first.
        self.assertIn(["gh", "api", "-X", "DELETE", "repos/x/y/releases/assets/index.json"], fake.calls)
        self.assertNotIn(["gh", "release", "create"], [c[:3] for c in fake.calls])
        self.assertIn("- **qt** 2026.11.12: 12,000 pages", fake.notes[0])
        self.assertIn("- **cpp** 2026.11.12:", fake.notes[0])

    def test_Pruning_keeps_the_two_newest_of_each_name(self):
        for v in ("1.0", "2026.11.5", "2026.11.12"):
            pack(self.packs / f"cpp-{v}.kpack", "cpp", v)
        pw.prune()
        self.assertEqual(["cpp-2026.11.12.kpack", "cpp-2026.11.5.kpack"], sorted(p.name for p in self.packs.glob("*.kpack")))
        self.assertEqual(["cpp-1.0.kpack"], [p.name for p in (self.packs / "superseded").iterdir()])


if __name__ == "__main__":
    unittest.main()
