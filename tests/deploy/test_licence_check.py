# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
"""tools/licence-check.py: the licensing guard, on the repository as it is and on what it must catch."""
from __future__ import annotations

import importlib.util
import subprocess
import tempfile
import unittest
from datetime import date
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("licence_check", ROOT / "tools" / "licence-check.py")
lc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lc)  # type: ignore[union-attr]

POLICY = lc.policy()
HEAD = f"# SPDX-License-Identifier: {lc.SPDX}\n# Copyright (C) 2026 {lc.HOLDER}\n"


class TheRepository(unittest.TestCase):
    def test_The_repository_checks_out(self):
        with mock.patch("sys.stdout"), mock.patch("sys.stderr") as err:
            code = lc.main([], today=date(2026, 11, 27))
        self.assertEqual(code, 0, "".join(str(c.args[0]) for c in err.write.call_args_list))

    def test_Every_source_file_of_ours_is_in_the_check(self):
        files = dict(lc.source_files(POLICY))
        for rel in ("tools/licence-check.py", "arenactl", "src/Llm.Api/Program.cs", "src/web/src/main.tsx",
                    "deploy/services/blender/Dockerfile", "deploy/scripts/installer.sh"):
            self.assertIn(rel, files)
        self.assertFalse([f for f in files if "/Migrations/" in f or "node_modules" in f])

    def test_LICENSE_md_is_the_AGPL_unchanged(self):
        text = (ROOT / "LICENSE.md").read_text()
        self.assertIn("GNU AFFERO GENERAL PUBLIC LICENSE", text)
        self.assertIn("Version 3, 19 November 2007", text)
        self.assertEqual(lc.check_files({**POLICY, "agpl_sha256": "0" * 64})[0][:10], "LICENSE.md")


class Headers(unittest.TestCase):
    def test_A_file_without_the_lines_is_named_and_one_with_them_passes(self):
        self.assertEqual(lc.header_problem("print(1)\n", 2026), "no SPDX-License-Identifier line")
        self.assertIsNone(lc.header_problem(HEAD + "print(1)\n", 2026))

    def test_Another_licence_or_holder_or_a_year_to_come_fails(self):
        self.assertIn("not", lc.header_problem("# SPDX-License-Identifier: MIT\n# Copyright (C) 2026 Binchitects\n", 2026))
        self.assertIn("names", lc.header_problem(f"# SPDX-License-Identifier: {lc.SPDX}\n# Copyright (C) 2026 Someone\n", 2026))
        self.assertIn("year", lc.header_problem(f"# SPDX-License-Identifier: {lc.SPDX}\n# Copyright (C) 2031 Binchitects\n", 2026))
        self.assertIsNone(lc.header_problem(f"// SPDX-License-Identifier: {lc.SPDX}\n// Copyright (C) 2026-2027 Binchitects\n", 2027))

    def test_The_lines_go_after_a_shebang_and_an_encoding_line(self):
        out = lc.with_header("#!/usr/bin/env python3\n# -*- coding: utf-8 -*-\nprint(1)\n", "#", 2026)
        self.assertEqual(out.splitlines()[:4], ["#!/usr/bin/env python3", "# -*- coding: utf-8 -*-",
                                                f"# SPDX-License-Identifier: {lc.SPDX}", f"# Copyright (C) 2026 {lc.HOLDER}"])
        self.assertIsNone(lc.header_problem(out, 2026))

    def test_CSS_gets_block_comments_and_CRLF_stays_CRLF(self):
        css = lc.with_header("a { color: red }\n", "/*", 2026)
        self.assertTrue(css.startswith(f"/* SPDX-License-Identifier: {lc.SPDX} */\n"))
        crlf = lc.with_header("Write-Host 1\r\nWrite-Host 2\r\n", "#", 2026)
        self.assertNotIn("\n", crlf.replace("\r\n", ""))
        self.assertIsNone(lc.header_problem(css, 2026))

    def test_Fix_adds_them_and_leaves_a_wrong_one_to_a_person(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            subprocess.run(["git", "init", "-q"], cwd=root, check=True)
            (root / "a.py").write_text("print(1)\n")
            (root / "b.sh").write_text("#!/bin/sh\n# Copyright (C) 2026 Someone Else\necho\n")
            subprocess.run(["git", "add", "."], cwd=root, check=True)
            with mock.patch.object(lc, "ROOT", root):
                problems = lc.check_headers({"exempt_files": []}, fix=True, year=2026)
            self.assertIsNone(lc.header_problem((root / "a.py").read_text(), 2026))
            self.assertEqual(len(problems), 1)
            self.assertTrue(problems[0].startswith("b.sh"))


class Licences(unittest.TestCase):
    def test_Permissive_passes_weak_copyleft_is_reviewed_and_the_rest_fails(self):
        self.assertEqual(lc.verdict("MIT", POLICY), "allowed")
        self.assertEqual(lc.verdict("LGPL-2.1-only", POLICY), "review")
        for bad in ("GPL-3.0-only", "AGPL-3.0-only", "SSPL-1.0", "NOASSERTION", "Some Licence"):
            self.assertEqual(lc.verdict(bad, POLICY), "denied", bad)

    def test_OR_takes_the_best_choice_and_AND_the_worst_part(self):
        self.assertEqual(lc.verdict("GPL-2.0-only OR MIT", POLICY), "allowed")
        self.assertEqual(lc.verdict("MIT AND GPL-2.0-only", POLICY), "denied")
        self.assertEqual(lc.verdict("(MIT AND LGPL-3.0-only)", POLICY), "review")
        self.assertEqual(lc.verdict("Apache-2.0 WITH LLVM-exception", POLICY), "allowed")

    def test_What_packages_write_is_read_as_SPDX(self):
        for raw, spdx in (("MIT License", "MIT"), ("Apache 2.0 License", "Apache-2.0"), ("BSD 3-Clause License", "BSD-3-Clause"),
                          ("", "NOASSERTION"), (None, "NOASSERTION"), ("(MIT OR Apache-2.0)", "MIT OR Apache-2.0")):
            self.assertEqual(lc.normalise(raw, POLICY), spdx, raw)
        self.assertEqual(lc.licence_of_text("Permission is hereby granted, free of charge, to any person obtaining a copy"), "MIT")
        self.assertEqual(lc.licence_of_text("SQLite is Public Domain"), "LicenseRef-Public-Domain")

    def test_A_GPL_package_fails_unless_accepted_and_an_acceptance_lapses_on_its_date(self):
        gpl = {"ecosystem": "npm", "name": "left-pad-gpl", "version": "1.0.0", "licence": "GPL-3.0-only"}
        self.assertEqual(len(lc.check_components([gpl], POLICY, date(2026, 11, 27))), 1)
        ok = {**POLICY, "accepted": [{"ecosystem": "npm", "name": "left-pad-*", "licence": "GPL-3.0-only", "why": "t", "until": "2026-12-31"}]}
        self.assertEqual(lc.check_components([gpl], ok, date(2026, 11, 27)), [])
        self.assertEqual(len(lc.check_components([gpl], ok, date(2027, 1, 1))), 1)

    def test_A_Python_package_the_notices_do_not_list_fails_until_written(self):
        c = {"ecosystem": "pypi", "name": "newpkg", "version": "1.0", "licence": "NOT LISTED"}
        self.assertIn("--write", lc.check_components([c], POLICY, date(2026, 11, 27))[0])

    def test_The_notices_read_back_as_written(self):
        comps = [{"ecosystem": "npm", "name": "react", "version": "19.0.0", "licence": "MIT"},
                 {"ecosystem": "nuget", "name": "Npgsql", "version": "10.0.3", "licence": "PostgreSQL"}]
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "N.md"
            path.write_text(lc.render_notices(comps))
            with mock.patch.object(lc, "NOTICES", path):
                self.assertEqual(lc.read_notices(), {("npm", "react", "19.0.0"): "MIT", ("nuget", "Npgsql", "10.0.3"): "PostgreSQL"})

    def test_Each_image_label_names_ours_and_the_main_program_in_it(self):
        self.assertEqual(lc.label_expected("deploy/services/blender/Dockerfile", POLICY), f"({lc.SPDX}) AND GPL-2.0-or-later")
        self.assertEqual(lc.label_expected("src/web/Dockerfile", POLICY), lc.SPDX)


class Authors(unittest.TestCase):
    def repo(self, d: str) -> Path:
        root = Path(d)
        env = ["-c", "user.name=Ali Ghadyani", "-c", "user.email=owner@example.com"]
        subprocess.run(["git", "init", "-q"], cwd=root, check=True)
        subprocess.run(["git", *env, "commit", "-q", "--allow-empty", "-m", "the owner's"], cwd=root, check=True)
        return root

    def test_A_commit_by_someone_else_must_be_signed_off(self):
        with tempfile.TemporaryDirectory() as d:
            root = self.repo(d)
            other = ["-c", "user.name=Someone Else", "-c", "user.email=else@example.com"]
            subprocess.run(["git", *other, "commit", "-q", "--allow-empty", "-m", "theirs"], cwd=root, check=True)
            with mock.patch.object(lc, "ROOT", root):
                problems = lc.check_authors(POLICY)
            self.assertEqual(len(problems), 1)
            self.assertIn("Someone Else", problems[0])
            subprocess.run(["git", *other, "commit", "-q", "--allow-empty", "-s", "-m", "theirs, signed off"], cwd=root, check=True)
            with mock.patch.object(lc, "ROOT", root):
                self.assertEqual(len(lc.check_authors(POLICY)), 1)  # the one unsigned commit still, not the signed one


if __name__ == "__main__":
    unittest.main()
