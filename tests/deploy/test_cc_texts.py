# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
"""
tools/cc-texts.py: the Common Criteria knowledge pack's pages. A component's page holds its definition in each edition,
from the CC's own XML, and the PPs that require it and how; a PP states an element only where its id leads a line with
"shall" after it, under headings that say how it is required; a notice that restricts reproduction keeps a PP's text
out (its metadata and requirements stay), and the excerpt quoted is the notice's own sentences. The fixture
(tests/fixtures/code-intel/cc-sources) is an excerpt of the CC's XML (the CCRA's notice allows its re-use) and PPs
written for these tests.
"""
from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "tools" / "cc-texts.py"
SOURCES = HERE.parents[1] / "tests" / "fixtures" / "code-intel" / "cc-sources"
PACK = HERE.parents[1] / "tests" / "fixtures" / "code-intel" / "packs" / "commoncriteria"
spec = importlib.util.spec_from_file_location("cc_texts", SCRIPT)
cc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cc)


class CcTexts(unittest.TestCase):
    def test_An_sfr_names_its_component_whatever_its_iteration_or_element(self):
        self.assertEqual("FCS_COP.1", cc.base_component("FCS_COP.1/Hash"))
        self.assertEqual("FCS_COP.1", cc.base_component("FCS_COP.1(1)"))
        self.assertEqual("FCS_COP.1", cc.base_component("FCS_COP.1.1/SigGen"))
        self.assertEqual("FCS_CKM_EXT.4", cc.base_component("FCS_CKM_EXT.4.1"))
        self.assertEqual("ALC_FLR.2", cc.base_component("ALC_FLR.2.1D"))
        self.assertEqual("FCS_DTLSC_EXT.1", cc.base_component("FCS_DTLSC_EXT.1.3"))

    def test_A_pp_requires_what_it_states_under_headings_that_say_how(self):
        secs = [("5", "Security Functional Requirements", 1, ""),
                ("5.1", "Identification and authentication", 2,
                 "FIA_UAU.2.1     The TSF shall require each user to be authenticated.\nFIA_UID.2 is named here without being stated."),
                ("5.2", "Security requirements rationale", 2, "FAU_GEN.1.1   The TSF shall be able to generate an audit record."),
                ("A", "Optional Requirements", 1, "FCS_COP.1.1/Hash The TSF shall perform [selection: SHA-256, SHA-384]."),
                ("B", "Selection-Based Requirements", 1, "FCS_TLSC_EXT.1.1 The TSF shall implement TLS."),
                ("C", "Extended Component Definitions", 1, "FIA_EXA_EXT.1.1 The TSF shall be defined here."),
                ("D", "Objective Requirements", 1, "FPT_TUD_EXT.2.1\nThe TSF shall verify updates.")]
        got = cc.requirements(secs)
        self.assertEqual({"FIA_UAU.2": ["mandatory"], "FCS_COP.1": ["optional"], "FCS_TLSC_EXT.1": ["selection-based"],
                          "FPT_TUD_EXT.2": ["objective"]}, {k: sorted(v["how"]) for k, v in got.items()})
        self.assertEqual({"FCS_COP.1.1/Hash"}, got["FCS_COP.1"]["as"])

    def test_Contents_find_the_headings_in_the_body_and_running_headers_go(self):
        page = ("Example PP\n\n1 Introduction ........................ 3\n2 Requirements ........................ 4\n2.1 Functional "
                "....................... 4\nAnnex A (informative) Notes .......... 5\n")
        body = ["Example PP  v1.0   Page {}\n\n1    Introduction\nThe TOE is an example.\n",
                "Example PP  v1.0   Page {}\n\n2    Requirements\n2.1  Functional\nFIA_UAU.2.1  The TSF shall authenticate.\n",
                "Example PP  v1.0   Page {}\n\n                   Annex A\n                (informative)\n          Notes\nA.1 General\nA note.\n"]
        raw = "\f".join([page] + [b.format(i + 2) for i, b in enumerate(body)] * 3)
        secs = cc.sections(raw)
        self.assertEqual(["", "1", "2", "2.1", "Annex A", "A.1"], [s[0] for s in secs][:6])
        self.assertNotIn("Example PP  v1.0", "\n".join(s[3] for s in secs))
        self.assertIn("FIA_UAU.2.1", dict((s[0], s[3]) for s in secs)["2.1"])

    def test_A_notice_that_restricts_keeps_the_text_out_and_only_its_own_words_are_quoted(self):
        t = cc.terms("Example PP Copyright (c) 2026 Example Corp. All rights reserved. 1 Introduction The secret design is here.", "DE", "portal")
        self.assertEqual(("restricted", False), (t["code"], t["shared"]))
        self.assertEqual("Copyright (c) 2026 Example Corp. All rights reserved.", t["text"])
        self.assertTrue(cc.terms("© Federal Office for Information Security 2020", "DE", "portal")["shared"])
        self.assertFalse(cc.terms("Copyright 2019 GlobalPlatform, Inc.", "FR", "portal")["shared"])
        self.assertEqual("permitted", cc.terms("Reproduction of this document without any change or cut is authorised.", "FR", "portal")["code"])
        self.assertEqual("us-government", cc.terms("Protection Profile for Applications", "US", "niap")["code"])
        self.assertEqual("ccra-cc", cc.terms("", "", "cc")["code"])

    def test_The_pages_from_the_fixture_are_the_committed_pack_fixture(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "commoncriteria"
            self.assertEqual(0, cc.texts(SOURCES, out))
            index = json.loads((out / "index.json").read_text())
            docs = {x["path"]: x for x in index["docs"]}
            comp = (out / "components/FIA/FIA_UAU.2.md").read_text()
            self.assertIn("FIA_UAU.2.1 The TSF shall require each user to be successfully authenticated", comp)
            self.assertIn("## CC:2022", comp)
            self.assertIn("## CC 3.1 Revision 5", comp)
            self.assertIn("| Protection Profile for Example Devices, Version 1.0 (PP_EX_V1.0) | US | mandatory | FIA_UAU.2.1 |", comp)
            self.assertIn("TD9001 (Published)", comp)
            self.assertEqual(["FIA_UAU.2"], [s["name"] for s in docs["components/FIA/FIA_UAU.2.md"]["symbols"]])
            # The portal's and NIAP's records of one PP are one page, its text from the HTML edition.
            self.assertIn("Listed by: NIAP, the CC portal (certified PPs)", (out / "pp/PP_EX_V1.0/index.md").read_text())
            self.assertNotIn("FIA_EXA_EXT.1", json.loads((out / "requirements.json").read_text())["components"])
            # The restricted PP: its requirements and metadata, not its text.
            self.assertEqual(["pp/pp9999b/index.md"], [p for p in docs if p.startswith("pp/pp9999b/")])
            self.assertIn("FDP_ACC.1", json.loads((out / "requirements.json").read_text())["components"])
            self.assertEqual(1, len(index["metadata_only"]))
            for p in docs:
                self.assertEqual((PACK / p).read_text(), (out / p).read_text(), p)
            self.assertEqual(json.loads((PACK / "index.json").read_text()), index)


if __name__ == "__main__":
    unittest.main()
