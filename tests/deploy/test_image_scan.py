# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
"""
tools/image-scan.py: what fails a release. Trivy's report stands in (scan() is replaced): a critical the accepted list
names passes, one it does not fails, an image that could not be scanned fails, and the kernel's headers are not the
image's.
"""
from __future__ import annotations

import importlib.util
import io
import unittest
from contextlib import redirect_stdout
from pathlib import Path

from support import REPO

spec = importlib.util.spec_from_file_location("image_scan", REPO / "tools" / "image-scan.py")
image_scan = importlib.util.module_from_spec(spec)
spec.loader.exec_module(image_scan)  # type: ignore[union-attr]


def report(*vulns: tuple[str, str, str, str]) -> dict:
    return {"Results": [{"Target": "image.tar (debian 12)", "Vulnerabilities": [
        {"VulnerabilityID": i, "Severity": s, "PkgName": p, "InstalledVersion": "1", "FixedVersion": f} for i, s, p, f in vulns]}]}


class ImageScanTests(unittest.TestCase):
    def run_with(self, reports: dict[str, dict], accepted: set[str]) -> tuple[int, str]:
        saved = image_scan.scan, image_scan.accepted_ids
        image_scan.scan = lambda image, engine, work: reports[image]
        image_scan.accepted_ids = lambda today: {i: {} for i in accepted}
        out = io.StringIO()
        try:
            with redirect_stdout(out):
                code = image_scan.main(list(reports))
        finally:
            image_scan.scan, image_scan.accepted_ids = saved
        return code, out.getvalue()

    def test_A_critical_on_the_accepted_list_passes_and_one_off_it_fails(self):
        code, out = self.run_with({"a:1": report(("CVE-1", "CRITICAL", "zlib1g", ""), ("CVE-2", "HIGH", "openssl", "3.1"))}, {"CVE-1"})
        self.assertEqual(code, 0, out)
        self.assertIn("critical 1 (1 accepted)", out)
        self.assertIn("high openssl 1 -> 3.1", out)
        code, out = self.run_with({"a:1": report(("CVE-1", "CRITICAL", "zlib1g", ""))}, set())
        self.assertEqual(code, 1, out)
        self.assertIn("CRITICAL CVE-1 zlib1g 1 -> no fix", out)

    def test_An_image_that_could_not_be_scanned_fails(self):
        code, out = self.run_with({"a:1": {"error": "not on this host"}}, set())
        self.assertEqual(code, 1)
        self.assertIn("a:1: NOT SCANNED: not on this host", out)

    def test_The_kernels_headers_are_not_the_images(self):
        found = image_scan.findings(report(("CVE-9", "CRITICAL", "linux-libc-dev", ""), ("CVE-8", "LOW", "bash", "")))
        self.assertEqual([f["id"] for f in found], ["CVE-8"])

    def test_With_no_env_the_required_settings_get_placeholders_and_the_rest_their_defaults(self):
        values = image_scan.placeholders(REPO / "deploy")
        self.assertEqual(values["COMPOSE_PROJECT_NAME"], "arena")
        self.assertIn("APP_KEY", values)  # ${APP_KEY:?...}: required
        compose = (REPO / "deploy" / "docker-compose.yml").read_text()
        self.assertIn("${SANDBOX_CPUS:-", compose)
        self.assertNotIn("SANDBOX_CPUS", values)  # a default of its own: left to compose


if __name__ == "__main__":
    unittest.main()
