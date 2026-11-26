"""
tools/images-for.py: the stack's images a change is in, read from deploy/docker-compose.yml and the Dockerfiles it
builds, so a redeploy builds and starts again every one that holds the change.
"""
from __future__ import annotations

import importlib.util
import io
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest import mock

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parents[1] / "tools" / "images-for.py"
spec = importlib.util.spec_from_file_location("images_for", SCRIPT)
images_for = importlib.util.module_from_spec(spec)
spec.loader.exec_module(images_for)


class ImagesFor(unittest.TestCase):
    built = images_for.images()

    def of(self, *paths: str) -> list[str]:
        return images_for.services_for(list(paths), self.built)

    def test_the_apps_own_files_and_the_skills_it_carries_are_in_the_app(self):
        self.assertEqual(self.of("skills/code-review/SKILL.md"), ["app"])
        self.assertEqual(self.of("src/Llm.Core/Chat/HistoryWords.cs"), ["app"])
        self.assertEqual(self.of("docs/chat.md"), ["web"])

    def test_a_service_s_own_files_and_what_others_copy_of_them(self):
        # The sandbox's runner is in the 3D sandbox and Lean too.
        self.assertEqual(self.of("deploy/services/sandbox/runner.py"), ["blender", "lean", "sandbox"])
        self.assertEqual(self.of("deploy/services/sandbox/arena/__init__.py"), ["sandbox"])
        self.assertEqual(self.of("tests/deploy/test_images_for.py", "CHANGELOG.md"), [])

    def test_copy_and_add_from_the_context_only(self):
        dockerfile = (
            "FROM node:22 AS page\n"
            "COPY src/web/package.json src/web/package-lock.json ./\n"
            "COPY --chmod=755 run.sh /usr/local/bin/\n"
            "COPY --from=page /out /srv\n"
            'ADD ["a b.txt", "c.txt", "/data/"]\n'
            "copy one.txt \\\n  two.txt /x/\n"
        )
        self.assertEqual(images_for.copied(dockerfile), ["src/web/package.json", "src/web/package-lock.json", "run.sh", "a b.txt", "c.txt", "one.txt", "two.txt"])

    def test_prints_the_services_of_the_paths_given(self):
        out = io.StringIO()
        with mock.patch("sys.stdin", io.StringIO("src/Llm.Api/Program.cs\nsrc/web/src/app/nav.ts\n")), redirect_stdout(out):
            self.assertEqual(images_for.main(["-"]), 0)
        self.assertEqual(out.getvalue(), "app\nweb\n")


if __name__ == "__main__":
    unittest.main()
