# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
"""arenactl, the repository's one command: the features it builds for, doctor, the list of scripts, builds and bundles
(nothing here needs .NET, Node, a container engine or the network)."""
from __future__ import annotations

import importlib.machinery
import importlib.util
import io
import json
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


class FakeRegistry:
    """Enough of an OCI registry's push API for arenactl publish: a token from its challenge, uploads in chunks of at
    most 4 MiB (as ghcr.io), no upload status (303, as ghcr.io), manifests by tag or digest; faults on request."""

    def __init__(self):
        import http.server, threading
        self.blobs, self.manifests, self.uploads, self.faults, self.log = {}, {}, {}, [], []
        reg = self

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a): pass

            def reply(self, code, headers=None, body=b""):
                self.send_response(code)
                for k, v in (headers or {}).items():
                    self.send_header(k, v)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def handle_one(self):
                body = self.rfile.read(int(self.headers.get("Content-Length") or 0))
                path, _, query = self.path.partition("?")
                reg.log.append((self.command, path))
                if path == "/token":
                    return self.reply(200, body=json.dumps({"token": "t0k"}).encode())
                if self.headers.get("Authorization") != "Bearer t0k":
                    return self.reply(401, {"WWW-Authenticate": f'Bearer realm="{reg.url}/token",service="fake"'})
                if path == "/v2/":
                    return self.reply(200)
                for f in list(reg.faults):
                    if f[0] == self.command:
                        reg.faults.remove(f)
                        return self.reply(f[1], body=b'{"errors":[{"code":"FAULT"}]}')
                parts = path.split("/")
                if "uploads" in parts and self.command == "POST":
                    uid = str(len(reg.uploads))
                    reg.uploads[uid] = b""
                    return self.reply(202, {"Location": f"/v2/x/blobs/upload/{uid}"})
                if "upload" in parts:
                    uid = parts[-1]
                    if self.command == "GET":
                        return self.reply(303, {"Location": "/elsewhere"})
                    if self.command == "PATCH":
                        if len(body) > 4 << 20:
                            return self.reply(416)
                        lo = int(self.headers["Content-Range"].split("-")[0])
                        if lo != len(reg.uploads[uid]):
                            return self.reply(416)
                        reg.uploads[uid] += body
                        return self.reply(202, {"Location": f"/v2/x/blobs/upload/{uid}"})
                    if self.command == "PUT":
                        digest = dict(q.split("=") for q in query.split("&"))["digest"]
                        data = reg.uploads.pop(uid) + body
                        assert "sha256:" + __import__("hashlib").sha256(data).hexdigest() == digest
                        reg.blobs[digest] = data
                        return self.reply(201)
                if "blobs" in parts and self.command == "HEAD":
                    return self.reply(200 if parts[-1] in reg.blobs else 404)
                if "manifests" in parts and self.command == "PUT":
                    reg.manifests[(parts[2] + "/" + parts[3], parts[-1])] = (self.headers["Content-Type"], body)
                    return self.reply(201)
                return self.reply(404)

            do_GET = do_POST = do_PUT = do_PATCH = do_HEAD = handle_one

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()


def saved_image(path: Path, layer: bytes) -> str:
    """An engine's save of one image: index.json -> an index -> a manifest, its config and one layer."""
    import hashlib
    blobs = {}

    def put(data: bytes) -> dict:
        d = "sha256:" + hashlib.sha256(data).hexdigest()
        blobs[d] = data
        return {"digest": d, "size": len(data)}
    cfg = put(b'{"architecture":"amd64","os":"linux"}')
    lay = put(layer)
    man = json.dumps({"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json",
                      "config": {**cfg, "mediaType": "application/vnd.oci.image.config.v1+json"},
                      "layers": [{**lay, "mediaType": "application/vnd.oci.image.layer.v1.tar+gzip"}]}).encode()
    m = put(man)
    idx = json.dumps({"schemaVersion": 2, "manifests": [{**m, "mediaType": "application/vnd.oci.image.manifest.v1+json"}]}).encode()
    i = put(idx)
    att = put(b'{"schemaVersion":2}')
    # As Docker saves it: the image, and an attestation beside it naming another manifest as its subject.
    top = json.dumps({"schemaVersion": 2, "manifests": [{**i, "mediaType": "application/vnd.oci.image.index.v1+json"},
                                                        {**att, "mediaType": "application/vnd.oci.image.manifest.v1+json",
                                                         "artifactType": "application/vnd.docker.attestation.manifest.v1+json",
                                                         "annotations": {"io.containerd.manifest.subject": m["digest"]}}]}).encode()
    with tarfile.open(path, "w") as t:
        for name, data in [("index.json", top)] + [("blobs/" + d.replace(":", "/"), b) for d, b in blobs.items()]:
            info = tarfile.TarInfo(name)
            info.size = len(data)
            t.addfile(info, io.BytesIO(data))
    return i["digest"]


class PublishTests(unittest.TestCase):
    """arenactl publish: the release's own images to a registry, in chunks it can take, and the release's notes."""

    def setUp(self):
        self.a = load_arenactl()
        self.reg = FakeRegistry()
        self.addCleanup(self.reg.close)
        self.a.own_images = lambda engine: [("app", "arena-app"), ("lmdeploy", "arena-lmdeploy:0.18.0"), ("vllm", "arena-vllm")]
        self.here = {"arena-app:8.3.0", "arena-lmdeploy:0.18.0", "arena-app:8.3.0-rc1"}
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.a.DIST = self.tmp / "dist"
        self.layer = os.urandom(9 << 20)          # three chunks: two of 4 MiB and the rest
        self.digests = {}
        test = self

        class Eng:
            name = "docker"
            def __init__(self, *_): pass
            def need(self): return "docker"
            def has_image(self, ref): return ref in test.here

        def run(cmd, **k):
            if cmd[1] == "save":
                test.digests[cmd[-1]] = saved_image(Path(cmd[cmd.index("-o") + 1]), test.layer + cmd[-1].encode())
            return 0
        self.a.Engine = Eng
        self.a.run = run
        self.tags = "v8.2.3\nv8.3.0-rc1\n"
        self.a.output = lambda cmd, **k: "1000" if cmd[1:3] == ["image", "inspect"] else self.tags if "tag" in cmd else None
        # Patched for the test only: the modules are the process's own, every other test's too.
        sleep = unittest.mock.patch.object(self.a.time, "sleep", lambda *_: None)
        sleep.start()
        self.addCleanup(sleep.stop)
        os.environ["ARENA_REGISTRY_TOKEN"] = "secret-token"
        self.addCleanup(os.environ.pop, "ARENA_REGISTRY_TOKEN", None)

    def publish(self, **kw):
        args = dict(engine=None, version="8.3.0", registry=self.reg.url.replace("http://", "") + "/farzan", services=None, from_build=False,
                    no_latest=False, release=False, dry_run=False, retries=2, work_dir=str(self.tmp))
        args.update(kw)
        orig = self.a.Registry.__init__

        def init(reg, host, token, proxy_url=None, retries=5, sleep=None):
            orig(reg, host, token, None, retries, lambda *_: None)
            reg.base = self.reg.url
        with unittest.mock.patch.object(self.a.Registry, "__init__", init), unittest.mock.patch("sys.stdout", io.StringIO()) as out:
            code = self.a.cmd_publish(unittest.mock.Mock(**args))
        return code, out.getvalue()

    def test_each_image_goes_by_service_with_its_tags_and_the_digest_it_has_here(self):
        code, out = self.publish()
        self.assertEqual(code, 0)
        self.assertEqual(sorted(k for k in self.reg.manifests if not k[1].startswith("sha256:")),
                         [("farzan/app", "8.3.0"), ("farzan/app", "latest"),
                          ("farzan/lmdeploy", "0.18.0"), ("farzan/lmdeploy", "8.3.0"), ("farzan/lmdeploy", "latest")])
        self.assertIn("not here, left out: vllm", out)
        listing = (self.a.DIST / "arena-8.3.0-images.txt").read_text().splitlines()
        self.assertEqual(listing[0], f"app\t{self.reg.url[7:]}/farzan/app@{self.digests['arena-app:8.3.0']}\t8.3.0 latest")
        # No request larger than ghcr.io takes, and the token is never said.
        self.assertNotIn("secret-token", out)

    def test_a_blob_the_registry_has_is_not_sent_again(self):
        self.publish(services="app")
        sent = sum(1 for m, _ in self.reg.log if m == "PATCH")
        self.publish(services="app")
        self.assertEqual(sum(1 for m, _ in self.reg.log if m == "PATCH"), sent)

    def test_a_broken_chunk_is_sent_again_and_a_refused_one_starts_the_blob_over(self):
        self.reg.faults += [("PATCH", 502), ("PATCH", 416)]
        self.assertEqual(self.publish(services="app")[0], 0)
        self.assertIn(("farzan/app", "8.3.0"), self.reg.manifests)

    def test_a_registry_that_keeps_failing_stops_it_saying_to_run_it_again(self):
        self.reg.faults += [("PATCH", 503)] * 50
        with self.assertRaises(self.a.Stop) as e:
            self.publish(services="app")
        self.assertIn("run it again to go on", str(e.exception))

    def test_an_older_release_published_after_a_newer_one_leaves_latest_with_it(self):
        self.tags = "v8.2.3\nv8.4.0\n"
        code, out = self.publish(services="app")
        self.assertEqual([k for k in self.reg.manifests if not k[1].startswith("sha256:")], [("farzan/app", "8.3.0")])
        self.assertIn(":latest stays with 8.4.0", out)

    def test_a_work_folder_not_there_yet_is_made(self):
        self.assertEqual(self.publish(services="app", work_dir=str(self.tmp / "not" / "yet"))[0], 0)

    def test_a_pre_release_never_moves_latest(self):
        self.publish(version="8.3.0-rc1", services="app")
        self.assertEqual([k for k in self.reg.manifests if not k[1].startswith("sha256:")], [("farzan/app", "8.3.0-rc1")])

    def test_a_dry_run_sends_nothing_and_an_unknown_service_is_refused(self):
        self.assertEqual(self.publish(dry_run=True)[0], 0)
        self.assertEqual(self.reg.blobs, {})
        with self.assertRaises(self.a.Stop):
            self.publish(services="nope")

    def test_with_no_token_it_says_how_to_get_one(self):
        os.environ.pop("ARENA_REGISTRY_TOKEN")
        with unittest.mock.patch.object(self.a.shutil, "which", lambda *_: None), self.assertRaises(self.a.Stop) as e:
            self.publish(services="app")
        self.assertIn("ARENA_REGISTRY_TOKEN", str(e.exception))

    def test_the_images_section_of_the_notes_is_put_in_once_and_replaced_in_place(self):
        body = self.a.release_notes_with_images("Notes.\n", "## Images\n\nold")
        self.assertEqual(body, "Notes.\n\n<!-- images -->\n## Images\n\nold\n<!-- /images -->\n")
        again = self.a.release_notes_with_images(body + "\nAfter.\n", "## Images\n\nnew")
        self.assertEqual(again.count("<!-- images -->"), 1)
        self.assertIn("new", again)
        self.assertNotIn("old", again)
        self.assertTrue(again.rstrip().endswith("After."))

if __name__ == "__main__":
    unittest.main()
