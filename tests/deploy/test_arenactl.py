"""arenactl, the repository's one command: the features it builds for, doctor, the list of scripts, builds and bundles
(nothing here needs .NET, Node, a container engine or the network)."""
from __future__ import annotations

import importlib.machinery
import importlib.util
import io
import os
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import unittest
import unittest.mock
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

def load_arenactl():
    loader = importlib.machinery.SourceFileLoader("arenactl", str(REPO / "arenactl"))
    spec = importlib.util.spec_from_loader("arenactl", loader)
    module = importlib.util.module_from_spec(spec)
    loader.exec_module(module)
    return module


class FeatureTests(unittest.TestCase):
    def setUp(self):
        self.a = load_arenactl()

    def test_The_defaults_with_and_without_and_what_they_require(self):
        rows = {f["id"]: f for f in self.a.features()}
        defaults = self.a.choose_features(None, None)
        self.assertEqual(defaults, sorted(i for i, f in rows.items() if f["default"]))
        # A feature brings those it requires.
        needing = next(f for f in rows.values() if f["requires"] and not f["default"])
        on = self.a.choose_features(needing["id"], None)
        for r in needing["requires"]:
            self.assertIn(r, on)
        self.assertEqual(sorted(rows), self.a.choose_features(None, None, all_=True))

    def test_CPU_only_leaves_out_what_needs_a_GPU_and_what_requires_it(self):
        rows = {f["id"]: f for f in self.a.features()}
        gpu = {i for i, f in rows.items() if "gpu" in f["needs"]}
        self.assertTrue(gpu)
        on = set(self.a.choose_features(None, None, cpu_only=True))
        self.assertFalse(gpu & on)
        self.assertTrue(all(set(rows[i]["requires"]) <= on for i in on))
        # Unless asked for by name.
        self.assertIn("pictures", self.a.choose_features("pictures", None, cpu_only=True))

    def test_An_unknown_feature_is_refused(self):
        with self.assertRaises(self.a.Stop):
            self.a.choose_features("no-such-feature", None)

    def test_An_upgrade_builds_for_the_installations_features_leaving_out_those_this_release_dropped(self):
        # An 8.1 installation ran workspaces; 8.2 has no such feature: it goes, and the rest are built for.
        with tempfile.TemporaryDirectory() as d:
            (Path(d) / "deploy" / "scripts").mkdir(parents=True)
            (Path(d) / "deploy" / "scripts" / "installer.sh").write_text("")
            (Path(d) / "deploy" / ".env").write_text("COMPOSE_PROFILES=chat-models,workspaces,code-studio,sandbox\n")
            built = []

            class Built(Exception):
                pass

            def build(engine, profiles):
                built.extend(profiles)
                raise Built()

            self.a.linux_only = lambda *_: None
            self.a.build_images = build
            self.a.Engine = lambda *_: None
            args = unittest.mock.Mock(command="upgrade", rest=["--dir", d], no_build=False)
            with unittest.mock.patch("sys.stderr", io.StringIO()), unittest.mock.patch("sys.stdout", io.StringIO()):
                with self.assertRaises(Built):
                    self.a.cmd_installer(args)
            self.assertIn("chat-models", built)
            self.assertIn("sandbox", built)
            self.assertNotIn("workspaces", built)
            self.assertIn("code-studio", built)


class CommandLineTests(unittest.TestCase):
    def run_it(self, *args: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, str(REPO / "arenactl"), *args], cwd=REPO, capture_output=True, text=True, timeout=300)

    def test_Doctor_says_each_thing_a_task_needs_with_what_to_do(self):
        r = self.run_it("doctor", "--for", "build")
        self.assertIn(r.returncode, (0, 1))
        self.assertIn("Python 3.8+", r.stdout)
        self.assertIn("container engine", r.stdout)

    def test_Scripts_lists_every_script_with_what_it_is_for(self):
        r = self.run_it("scripts")
        self.assertEqual(r.returncode, 0, r.stderr)
        for name in ("installer.sh", "make-installer.sh", "backup.sh", "make-cert.sh", "release-scan.py"):
            self.assertIn(name, r.stdout)
        listed = [line.split()[0] for line in r.stdout.splitlines() if line.startswith("  ") and line.split()]
        described = [line for line in r.stdout.splitlines() if line.startswith("  ") and len(line.split()) > 1]
        self.assertEqual(len(listed), len(described), "a script without a line saying what it is for")

    def test_Every_check_names_a_script_that_is_there(self):
        a = load_arenactl()
        for name, (script, _) in a.CHECKS.items():
            self.assertTrue((REPO / "deploy" / "scripts" / script).is_file() or (REPO / "tools" / script).is_file(), name)
        for name, (script, _) in a.OPS.items():
            self.assertTrue((REPO / "deploy" / "scripts" / script).is_file(), name)

class Engine:
    name = "docker"


class BuildTests(unittest.TestCase):
    """build_images: the release's images built at once, and, when that fails, each again alone."""

    def build_with(self, fails):
        arenactl = load_arenactl()
        calls = []

        class FakeCompose:
            def __init__(self, engine, profiles, project="arena"):
                pass

            def config(self):
                return {"services": {s: {"build": {"context": ".."}} for s in ("app", "web", "studio")} | {"postgres": {"image": "postgres:17"}}}

            def run(self, *args):
                calls.append(args)
                if fails(args):
                    raise arenactl.Stop("docker failed (exit 1)", 1)

            def close(self):
                pass

        arenactl.Compose = FakeCompose
        arenactl.say = lambda *a, **k: None
        arenactl.step = lambda *a, **k: None
        return arenactl, calls

    def test_images_that_time_out_together_are_built_again_one_at_a_time(self):
        # PyPI times out while three images read it at once; each alone builds.
        arenactl, calls = self.build_with(lambda args: args[0] == "build" and len(args) > 2)
        arenactl.build_images(Engine(), ["core"], pull=False)
        self.assertEqual(calls, [("build", "app", "studio", "web"), ("build", "app"), ("build", "studio"), ("build", "web")])

    def test_an_image_that_fails_alone_fails_the_build(self):
        arenactl, calls = self.build_with(lambda args: args[0] == "build" and "studio" in args)
        with self.assertRaises(arenactl.Stop):
            arenactl.build_images(Engine(), ["core"], pull=False)
        self.assertEqual(calls[-1], ("build", "studio"))
        # One image asked for: it is not built twice.
        arenactl, calls = self.build_with(lambda args: True)
        with self.assertRaises(arenactl.Stop):
            arenactl.build_images(Engine(), ["core"], pull=False, services=["studio"])
        self.assertEqual(calls, [("build", "studio")])

    def test_keep_going_says_what_did_not_build_or_pull_and_goes_on(self):
        arenactl, calls = self.build_with(lambda args: "studio" in args or args == ("pull", "postgres"))
        arenactl.warn = lambda *a, **k: None
        engine = Engine()
        engine.has_image = lambda ref: False
        arenactl.pinned_images = lambda: {}
        failed = arenactl.build_images(engine, ["core"], keep_going=True)
        self.assertEqual(failed, {"studio", "postgres"})
        self.assertIn(("build", "web"), calls)


class BundleTests(unittest.TestCase):
    """arenactl bundle: an optional feature whose image cannot be built or pulled is left out, the bundle still made."""

    def bundle(self, failed, without=None):
        arenactl = load_arenactl()
        made = []
        arenactl.doctor_checks = lambda *a: []
        arenactl.linux_only = lambda *a: None
        arenactl.build_images = lambda *a, **k: set(failed)
        arenactl.release_tag_missing = lambda: False
        arenactl.ensure_embedding = lambda *a: None
        arenactl.say = arenactl.step = arenactl.warn = lambda *a, **k: None
        arenactl.run = lambda cmd, **k: made.append(cmd) or 0
        args = arenactl.argparse.Namespace(podman=False, engine="docker", dry_run=False, no_build=False, without=without,
                                           with_deps=False, out=None, models_dir=None, image_cache=None,
                                           models=False, no_chat_models=False, packs=False, no_image_cache=False)
        return arenactl, made, args

    def test_a_feature_whose_image_failed_is_left_out(self):
        arenactl, made, args = self.bundle({"lmdeploy"})
        self.assertEqual(arenactl.cmd_bundle(args), 0)
        cmd = made[-1]
        self.assertIn("lmdeploy", cmd[cmd.index("--without") + 1].split(","))

    def test_features_that_need_a_failed_one_go_too(self):
        arenactl, made, args = self.bundle({"code-intel"}, without="lean")
        arenactl.cmd_bundle(args)
        cmd = made[-1]
        left = cmd[cmd.index("--without") + 1].split(",")
        self.assertTrue({"code-intel", "scanners", "lean"} <= set(left), left)

    def test_packs_asked_for_where_there_are_none_stop_the_bundle(self):
        arenactl, made, args = self.bundle(set())
        with tempfile.TemporaryDirectory() as d:
            arenactl.ROOT = Path(d)
            (Path(d) / "packs").mkdir()
            args.packs = True
            with self.assertRaises(arenactl.Stop):
                arenactl.cmd_bundle(args)
            (Path(d) / "packs" / "cpp.kpack").write_bytes(b"x")
            arenactl.DIST = Path(d) / "dist"
            self.assertEqual(arenactl.cmd_bundle(args), 0)

    def test_a_core_image_that_failed_stops_the_bundle(self):
        arenactl, made, args = self.bundle({"app"})
        with self.assertRaises(arenactl.Stop):
            arenactl.cmd_bundle(args)
        self.assertEqual(made, [])

    def test_the_embedding_model_is_fetched_when_the_library_lacks_it_and_checked(self):
        arenactl = load_arenactl()
        arenactl.step = arenactl.warn = lambda *a, **k: None
        with tempfile.TemporaryDirectory() as d:
            body = {"data": b"model"}
            arenactl.fetch = lambda url, dest: (dest.parent.mkdir(parents=True, exist_ok=True), dest.write_bytes(body["data"]))
            import hashlib
            arenactl.EMBED_SHA256 = hashlib.sha256(b"model").hexdigest()
            arenactl.ensure_embedding(d)
            target = Path(d) / arenactl.EMBED_FILE
            self.assertEqual(target.read_bytes(), b"model")
            # Another file than the one pinned is not kept.
            target.unlink()
            body["data"] = b"tampered"
            arenactl.ensure_embedding(d)
            self.assertFalse(target.exists())
            self.assertEqual(list(target.parent.iterdir()), [])


class ProxyTests(unittest.TestCase):
    """--proxy: taken anywhere on the line, given to the builds and the SDK's containers, its password never printed."""

    def setUp(self):
        self.saved = {k: os.environ.get(k) for k in ("ARENA_PROXY", "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy", "NO_PROXY", "no_proxy")}

    def tearDown(self):
        for k, v in self.saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v

    def test_taken_before_or_after_the_command(self):
        arenactl = load_arenactl()
        self.assertEqual(arenactl.take_proxy(["--proxy", "http://p:1", "bundle", "--with-deps"]), (["bundle", "--with-deps"], "http://p:1"))
        self.assertEqual(arenactl.take_proxy(["bundle", "--proxy=http://p:1"]), (["bundle"], "http://p:1"))
        self.assertEqual(arenactl.take_proxy(["bundle"]), (["bundle"], None))
        with self.assertRaises(arenactl.Stop):
            arenactl.take_proxy(["bundle", "--proxy"])

    def test_builds_and_containers_get_it(self):
        arenactl = load_arenactl()
        arenactl.say = arenactl.warn = lambda *a, **k: None
        for k in self.saved:
            os.environ.pop(k, None)
        self.assertEqual(arenactl.proxy_build_args(), [])
        arenactl.use_proxy("http://172.10.0.138:10808")
        args = arenactl.proxy_build_args()
        self.assertIn("HTTPS_PROXY=http://172.10.0.138:10808", args)
        self.assertIn("http_proxy=http://172.10.0.138:10808", args)
        self.assertIn("NO_PROXY=localhost,127.0.0.1,::1", args)
        self.assertIn("https_proxy=http://172.10.0.138:10808", arenactl.proxy_env_args())
        with self.assertRaises(arenactl.Stop):
            arenactl.use_proxy("172.10.0.138:10808")

    def test_docker_is_restarted_only_after_a_yes(self):
        arenactl = load_arenactl()
        arenactl.warn = lambda *a, **k: None
        arenactl.output = lambda cmd, **k: "app\tarena\nup782\t\n" if cmd[1] == "ps" else "/app\tunless-stopped\n/up782\tno\n"
        err = io.StringIO()
        with unittest.mock.patch("sys.stderr", err), unittest.mock.patch("sys.stdin", io.StringIO("")):
            with self.assertRaises(arenactl.Stop):
                arenactl.docker_restart_notice(["sudo"], yes=False)
        self.assertIn("STAY STOPPED until started by hand: up782", err.getvalue())
        self.assertIn("every running container stops: 2 now", err.getvalue())
        tty = io.StringIO("no\n")
        tty.isatty = lambda: True
        with unittest.mock.patch("sys.stderr", io.StringIO()), unittest.mock.patch("sys.stdin", tty), \
                unittest.mock.patch("builtins.input", lambda *_: "no"):
            with self.assertRaises(arenactl.Stop):
                arenactl.docker_restart_notice(["sudo"], yes=False)
        with unittest.mock.patch("sys.stderr", io.StringIO()):
            arenactl.docker_restart_notice(["sudo"], yes=True)

    def test_a_proxy_that_does_not_answer_changes_nothing(self):
        arenactl = load_arenactl()
        self.assertIsNotNone(arenactl.proxy_answers("http://127.0.0.1:9"))

    def test_its_password_is_never_printed(self):
        arenactl = load_arenactl()
        self.assertEqual(arenactl.masked("--build-arg HTTP_PROXY=http://me:secret@p:1 x"), "--build-arg HTTP_PROXY=http://***@p:1 x")


if __name__ == "__main__":
    unittest.main()
