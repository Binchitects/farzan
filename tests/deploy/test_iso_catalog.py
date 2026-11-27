# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
"""
tools/iso-catalog.py: ISO Open Data's catalogue as the iso knowledge pack's pages. A draft's reference belongs to its
undated number, a supplement to the edition it amends, a stage to its status; the abstracts' Word HTML reads as text; and
the undated number answers on the current edition. The fixture (tests/fixtures/code-intel/iso-sources) is a few records
of the open data as ISO publishes them (ODC-By 1.0, © ISO).
"""
from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "tools" / "iso-catalog.py"
SOURCES = HERE.parents[1] / "tests" / "fixtures" / "code-intel" / "iso-sources"
spec = importlib.util.spec_from_file_location("iso_catalog", SCRIPT)
ic = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ic)


class IsoCatalog(unittest.TestCase):
    def test_A_references_undated_number_drops_its_year_its_supplement_and_its_drafts_stage(self):
        self.assertEqual("ISO/IEC 27001", ic.base("ISO/IEC 27001:2022/Amd 1:2024"))
        self.assertEqual("ISO 9001", ic.base("ISO/DIS 9001"))
        self.assertEqual("ISO/IEC 27001", ic.base("ISO/IEC FDIS 27001"))
        self.assertEqual("ISO/TS 19115-3", ic.base("ISO/NP TS 19115-3"))
        self.assertEqual("ISO/TR 10013", ic.base("ISO/DTR 10013"))
        self.assertEqual("ISO/IEC TR 27103", ic.base("ISO/IEC DTR 27103"))
        self.assertEqual("ISO/IEC/IEEE 8802-3", ic.base("ISO/IEC/IEEE 8802-3:2017"))
        self.assertEqual("ISO/IEC TS 17961", ic.base("ISO/IEC NP TS 17961"))
        self.assertEqual("ISO Guide 73", ic.base("ISO Guide 73:2009"))
        self.assertEqual("ISO/HL7 27931", ic.base("ISO/HL7 27931:2009"))

    def test_A_supplement_belongs_to_the_edition_it_amends(self):
        self.assertEqual("ISO/IEC 27001:2022", ic.parent("ISO/IEC 27001:2022/Amd 1:2024"))
        self.assertEqual("ISO/IEC 27001:2013", ic.parent("ISO/IEC 27001:2013/DAmd 1"))
        self.assertEqual("ISO 9001:1994", ic.parent("ISO 9001:1994/Cor 1:1995"))
        self.assertEqual("ISO/IEC 9594-8:2020", ic.parent("ISO/IEC 9594-8:2020/PDAM 1"))
        self.assertIsNone(ic.parent("ISO 9001:2015"))

    def test_Stages_are_statuses(self):
        self.assertEqual(["development", "cancelled", "development", "current", "current", "current", "withdrawn"],
                         [ic.status(s) for s in (2020, 3098, 6000, 6060, 9093, 9520, 9599)])
        self.assertEqual("90.93 International Standard confirmed", ic.stage_text(9093))

    def test_Word_html_reads_as_paragraphs_and_items(self):
        markup = ('<p class="MsoBodyText"><span lang="EN-GB">It gives:</span></p>\n<p class="ListContinue1"><!-- [if !supportLists]-->'
                  '<span><span style="mso-list: Ignore;">—<span style="font: 7.0pt">&nbsp;&nbsp;</span></span></span><!--[endif]-->'
                  '<span>design &amp; installation;</span></p><ul><li>one</li><li>two</li></ul>')
        self.assertEqual("It gives:\n\n- design & installation;\n- one\n- two", ic.text(markup))

    def test_The_pages_carry_status_successors_and_the_undated_number_on_the_current_edition(self):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "iso"
            index = ic.write(SOURCES, out)
            docs = {x["path"]: x for x in index["docs"]}
            names = {s["name"]: x["path"] for x in index["docs"] for s in x["symbols"]}
            self.assertEqual("current/82875-iso-iec-27001-2022.md", names["ISO/IEC 27001"])
            self.assertEqual("current/82875-iso-iec-27001-2022.md", names["ISO 27001"])
            self.assertEqual("current/88464-iso-9001-2026.md", names["ISO 9001"])
            self.assertEqual("withdrawn/62085-iso-9001-2015.md", names["ISO 9001:2015"])
            # No current ISO 9002: the draft answers.
            self.assertEqual("development/91841-iso-dis-9002.md", names["ISO 9002"])
            self.assertEqual("committees/iso-iec-jtc-1-sc-27.md", names["ISO/IEC JTC 1/SC 27"])
            self.assertEqual("ics/35.030.md", names["35.030"])
            self.assertEqual("ics/35.md", names["ICS 35"])
            self.assertNotIn("35", names)
            self.assertEqual("https://www.iso.org/standard/62085.html", docs["withdrawn/62085-iso-9001-2015.md"]["url"])

            old = (out / "withdrawn/62085-iso-9001-2015.md").read_text(encoding="utf-8")
            self.assertIn("Status: Withdrawn, stage 95.99 Withdrawal of International Standard.", old)
            self.assertIn("Replaced by: ISO 9001:2026 (current).", old)
            self.assertIn("- ISO 9001:2015/Amd 1:2024 (withdrawn)", old)
            new = (out / "current/82875-iso-iec-27001-2022.md").read_text(encoding="utf-8")
            self.assertIn("Committee: ISO/IEC JTC 1/SC 27, Information security, cybersecurity and privacy protection.", new)
            self.assertIn("ICS: 35.030 IT Security; 03.100.70 Management systems.", new)
            self.assertIn("docs/compliance/frameworks/iso27001.md", new)
            self.assertIn("ODC-By 1.0", new)
            self.assertIn("## Abstract", new)
            committee = (out / "committees/iso-tc-176-sc-2.md").read_text(encoding="utf-8")
            self.assertIn("- ISO 9001:2026: Quality management systems — Requirements", committee)
            self.assertIn("## Withdrawn (2)\n\nISO 9001:2015, ISO 9001:2015/Amd 1:2024", committee)
            self.assertEqual(index, json.loads((out / "index.json").read_text(encoding="utf-8")))
            self.assertRegex(index["provenance"], r"^deliverables=[0-9a-f]{12},committees=[0-9a-f]{12},ics=[0-9a-f]{12}$")


if __name__ == "__main__":
    unittest.main()
