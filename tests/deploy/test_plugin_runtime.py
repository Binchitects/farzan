"""deploy/services/plugins: the plugin runtime, in its real container with compose's limits, broken on purpose.

The framework's chaos plugin (tests/fixtures/plugins/packages/chaos.fap) hangs, spins, leaks its heap and buffers
outside it, forks, writes too much, opens too many files, exits, throws where nothing catches it, writes garbage on
the protocol and tries to reach what is not its own. Each break must fail its own call only: the runtime answers,
the hello plugin beside it answers, and the chaos plugin is started again.

Needs Docker and the node:22-alpine image (the runtime's base, in the offline bundle); skipped without them.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import unittest
import uuid
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SERVICE = REPO / "deploy" / "services" / "plugins"
FIXTURES = REPO / "tests" / "fixtures" / "plugins" / "packages"
IMAGE = "arena-plugins:test-" + uuid.uuid4().hex[:8]


def docker(*args: str, check: bool = True, timeout: int = 300) -> subprocess.CompletedProcess:
    return subprocess.run(["docker", *args], capture_output=True, text=True, check=check, timeout=timeout)


def have_docker() -> str | None:
    if shutil.which("docker") is None:
        return "no docker"
    if docker("image", "inspect", "node:22-alpine", check=False).returncode != 0:
        return "no node:22-alpine image (the runtime's base)"
    return None


class Runtime:
    """The runtime's container and a connection to its socket, as the app holds one."""

    def __init__(self, root: Path):
        self.root = root
        self.name = "arena-plugins-test-" + uuid.uuid4().hex[:8]
        self.sock: socket.socket | None = None
        self.buffer = b""
        self.next = 1
        self.lock = threading.Lock()
        self.messages: list[dict] = []
        self.host_answers = {}
        self.backlog: list[dict] = []

    def start(self):
        # As compose runs it (deploy/docker-compose.yml, service plugins).
        docker("run", "-d", "--name", self.name, "--init", "--network", "none", "--read-only",
               "--cap-drop", "ALL", "--cap-add", "SETUID", "--cap-add", "SETGID", "--cap-add", "CHOWN",
               "--cap-add", "DAC_OVERRIDE", "--cap-add", "FOWNER", "--cap-add", "KILL",
               "--security-opt", "no-new-privileges:true", "--pids-limit", "512", "--memory", "1g", "--cpus", "2",
               "--tmpfs", "/work:size=256m,mode=0711", "--tmpfs", "/tmp:size=16m,mode=1777",
               "-e", f"PLUGINS_SOCKET_GID={os.getgid()}", "-e", "PLUGINS_WATCH_MS=100",
               "-v", f"{self.root}:/plugin-runtime", IMAGE)
        path = self.root / "run" / "host.sock"
        deadline = time.time() + 30
        while time.time() < deadline:
            if path.exists():
                try:
                    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                    s.connect(str(path))
                    self.sock = s
                    return
                except OSError:
                    s.close()
            time.sleep(0.1)
        raise AssertionError("the runtime did not listen: " + docker("logs", self.name, check=False).stderr)

    def stop(self):
        if self.sock:
            self.sock.close()
        docker("rm", "-f", self.name, check=False)

    def send(self, m: dict):
        self.sock.sendall((json.dumps(m) + "\n").encode())

    def read(self, until, timeout: float = 30):
        """Reads messages, answering the workers' host API calls, until `until(message)` is true."""
        deadline = time.time() + timeout
        self.sock.settimeout(0.5)
        for i, m in enumerate(self.backlog):
            if until(m):
                return self.backlog.pop(i)
        while time.time() < deadline:
            while b"\n" in self.buffer:
                line, self.buffer = self.buffer.split(b"\n", 1)
                m = json.loads(line)
                self.messages.append(m)
                if m.get("type") == "host":
                    self.answer_host(m)
                    continue
                if until(m):
                    return m
                self.backlog.append(m)
                del self.backlog[:-200]
            try:
                chunk = self.sock.recv(65536)
            except socket.timeout:
                continue
            if not chunk:
                raise AssertionError("the runtime closed the app's connection")
            self.buffer += chunk
        raise AssertionError("no answer in time; the last messages: " + json.dumps(self.messages[-5:]))

    def answer_host(self, m: dict):
        # A tiny host: storage for the hello plugin, and a refusal for what the chaos plugin may not do.
        method, params = m["method"], m.get("params", {})
        if method == "storage.get":
            self.send({"type": "host.result", "hid": m["hid"], "result": self.host_answers.get(params.get("key"))})
        elif method == "storage.set":
            self.host_answers[params.get("key")] = params.get("value")
            self.send({"type": "host.result", "hid": m["hid"], "result": None})
        else:
            self.send({"type": "host.result", "hid": m["hid"], "error": {"code": -32001, "message": "not granted"}})

    def request(self, m: dict, timeout: float = 30) -> dict:
        with self.lock:
            m["id"] = self.next
            self.next += 1
        self.send(m)
        return self.read(lambda r: r.get("type") in ("result", "status") and r.get("id") == m["id"], timeout)

    def start_plugin(self, name: str, **limits) -> dict:
        return self.request({"type": "start", "plugin": name, "version": "1", "source": f"/plugin-runtime/packages/{name}/1",
                             "entry": "server/index.js", "limits": {"timeoutMs": 2000, "busyMs": 4000, **limits},
                             "init": {"hostApi": 1, "arena": {"version": "8.3.0"}, "plugin": {"name": name, "version": "1"},
                                      "settings": {}, "scopes": ["storage"], "network": []}})

    def tool(self, plugin: str, name: str, args: dict | None = None, timeout_ms: int = 2000, wait: float = 30) -> dict:
        token = uuid.uuid4().hex
        context = {"token": token, "person": {"id": "p1", "name": "Alice", "email": "alice@example.com", "groups": []},
                   "chatId": None, "projectId": None, "deadline": int(time.time() * 1000) + timeout_ms, "dev": False}
        return self.request({"type": "call", "plugin": plugin, "method": "tool.call", "timeoutMs": timeout_ms, "token": token,
                             "params": {"name": name, "arguments": args or {}, "context": context}}, wait)

    def status(self) -> dict:
        r = self.request({"type": "status"})
        return {p["plugin"]: p for p in r["plugins"]}


@unittest.skipIf(have_docker() is not None, have_docker() or "")
class PluginRuntimeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        docker("build", "--pull=false", "-t", IMAGE, str(SERVICE), timeout=600)
        cls.tmp = Path(tempfile.mkdtemp(prefix="arena-plugin-runtime-"))
        for name in ("hello", "chaos"):
            dest = cls.tmp / "packages" / name / "1"
            dest.mkdir(parents=True)
            with zipfile.ZipFile(FIXTURES / f"{name}.fap") as z:
                for info in z.infolist():
                    if not info.filename.startswith("META-INF/"):
                        z.extract(info, dest)
        cls.rt = Runtime(cls.tmp)
        cls.rt.start()
        assert "result" in cls.rt.start_plugin("hello"), "hello starts"
        assert "result" in cls.rt.start_plugin("chaos", memoryMb=64, processes=16, openFiles=64, fileMb=8), "chaos starts"

    @classmethod
    def tearDownClass(cls):
        cls.rt.stop()
        # The runtime made its files as root and as the plugins' users: removed from inside a container.
        subprocess.run(["docker", "run", "--rm", "-v", f"{cls.tmp}:/x", "--network", "none", "node:22-alpine",
                        "sh", "-c", "rm -rf /x/* /x/.[!.]*"], capture_output=True, check=False)
        shutil.rmtree(cls.tmp, ignore_errors=True)
        docker("rmi", "-f", IMAGE, check=False)

    def kind(self, r: dict) -> str:
        return r["error"]["kind"] if "error" in r else "answered"

    def assert_unharmed(self):
        """After a break: the chaos plugin answers again (started anew if it had to be), and hello was never touched."""
        self.assertEqual(self.rt.tool("chaos", "ok")["result"]["content"], "fine")
        r = self.rt.tool("hello", "greet", {"name": "Bob"})
        self.assertIn("Bob", r["result"]["content"])

    def test_a_call_and_its_host_api_calls_go_through(self):
        r = self.rt.tool("hello", "greet", {"name": "Alice"})
        self.assertRegex(r["result"]["content"], r"^Hello, Alice! \(\d+\)$")
        self.assertTrue(any(m.get("type") == "host" and m["method"] == "storage.set" for m in self.rt.messages))

    def test_isolation_another_plugins_files_the_network_the_socket_and_its_own_code(self):
        out = self.rt.tool("chaos", "escape", wait=30)["result"]["content"].splitlines()
        reached = [line for line in out if "REACHED" in line]
        self.assertEqual(reached, [], out)
        uid = next(line for line in out if line.startswith("uid "))
        hello_uid = self.rt.status()["hello"]["uid"]
        self.assertNotEqual(uid, f"uid {hello_uid}")
        self.assertGreaterEqual(int(uid.split()[1]), 40000)

    def test_a_call_that_never_answers_fails_at_its_deadline_and_the_worker_lives_on(self):
        pid = self.rt.status()["chaos"]["pid"]
        started = time.time()
        self.assertEqual(self.kind(self.rt.tool("chaos", "hang", timeout_ms=1000)), "timeout")
        self.assertLess(time.time() - started, 5)
        time.sleep(1.2)  # the ping after the deadline: answered, so the worker is kept
        self.assertEqual(self.rt.status()["chaos"]["pid"], pid)
        self.assert_unharmed()

    def test_a_blocked_event_loop_is_ended_while_the_plugin_beside_it_answers(self):
        token = uuid.uuid4().hex
        # The spin goes out, and hello is asked during it.
        spin_id = 10_000 + int(time.time()) % 1000
        self.rt.send({"type": "call", "id": spin_id, "plugin": "chaos", "method": "tool.call", "timeoutMs": 1500, "token": token,
                      "params": {"name": "spin", "arguments": {}, "context": {"token": token, "person": None, "chatId": None,
                                                                              "projectId": None, "deadline": 0, "dev": False}}})
        t = time.time()
        self.assertIn("Ann", self.rt.tool("hello", "greet", {"name": "Ann"})["result"]["content"])
        self.assertLess(time.time() - t, 1.5)
        r = self.rt.read(lambda m: m.get("type") == "result" and m.get("id") == spin_id)
        self.assertEqual(r["error"]["kind"], "timeout")
        self.rt.read(lambda m: m.get("type") == "exit" and m.get("plugin") == "chaos")
        self.assert_unharmed()

    def test_memory_in_its_heap_and_outside_it(self):
        self.assertIn(self.kind(self.rt.tool("chaos", "leak", timeout_ms=10000, wait=40)), ("crash", "memory"))
        self.assert_unharmed()
        self.assertEqual(self.kind(self.rt.tool("chaos", "buffers", timeout_ms=10000, wait=40)), "memory")
        self.assert_unharmed()

    def test_processes_files_and_file_size_stop_at_its_own_limits(self):
        r = self.rt.tool("chaos", "fork_bomb", timeout_ms=10000, wait=40)
        text = r["result"]["content"] if "result" in r else r["error"]["message"]
        m = re.match(r"started (\d+), then EAGAIN", text)
        self.assertIsNotNone(m, text)
        self.assertLess(int(m.group(1)), 16)
        self.assert_unharmed()
        r = self.rt.tool("chaos", "open_files")
        m = re.match(r"opened (\d+), then EMFILE", r["result"]["content"])
        self.assertIsNotNone(m, r)
        self.assertLess(int(m.group(1)), 64)
        r = self.rt.tool("chaos", "big_file", timeout_ms=5000)
        # Past its file size, the write is refused or the worker is ended (SIGXFSZ): either way, it wrote no 200 MB.
        self.assertNotEqual(r.get("result", {}).get("content"), "wrote 200 MB")
        self.assert_unharmed()

    def test_exits_uncaught_throws_and_garbage_on_the_protocol_fail_their_own_call(self):
        for tool, kind in (("exit", "crash"), ("throw_later", "crash"), ("garbage", "protocol")):
            with self.subTest(tool=tool):
                self.assertEqual(self.kind(self.rt.tool("chaos", tool)), kind)
                self.assert_unharmed()

    def test_an_ordinary_error_fails_the_call_and_the_worker_lives_on(self):
        pid = self.rt.status()["chaos"]["pid"]
        r = self.rt.tool("chaos", "fail")
        self.assertEqual(r["error"]["kind"], "error")
        self.assertIn("an ordinary bug", r["error"]["message"])
        self.assertEqual(self.rt.status()["chaos"]["pid"], pid)

    def test_too_many_calls_at_once_are_refused_not_queued_forever(self):
        tokens = []
        first = 50_000
        for i in range(20):
            t = uuid.uuid4().hex
            tokens.append(t)
            self.rt.send({"type": "call", "id": first + i, "plugin": "chaos", "method": "tool.call", "timeoutMs": 800, "token": t,
                          "params": {"name": "hang", "arguments": {}, "context": {"token": t, "person": None, "chatId": None,
                                                                                  "projectId": None, "deadline": 0, "dev": False}}})
        kinds = []
        for i in range(20):
            r = self.rt.read(lambda m, i=i: m.get("type") == "result" and m.get("id") == first + i)
            kinds.append(r["error"]["kind"])
        self.assertEqual(kinds.count("busy"), 4)
        self.assertEqual(kinds.count("timeout"), 16)
        self.assert_unharmed()

    def test_a_package_from_outside_its_folder_is_never_started(self):
        r = self.rt.request({"type": "start", "plugin": "evil", "version": "1", "source": "/etc", "entry": "passwd", "limits": {}, "init": {}})
        self.assertIn("packages/", r["error"]["message"])
        r = self.rt.request({"type": "start", "plugin": "evil", "version": "1", "source": "/plugin-runtime/packages/hello/1",
                             "entry": "../../../../etc/passwd", "limits": {}, "init": {}})
        self.assertIn("error", r)

    def test_zz_a_worker_that_crashes_again_and_again_waits_longer_each_time(self):
        for _ in range(3):
            self.rt.tool("chaos", "exit")
        r = self.rt.tool("chaos", "ok")
        self.assertEqual(r["error"]["kind"], "crash")
        self.assertIn("starts again in", r["error"]["message"])
        self.assertGreater(self.rt.status()["chaos"]["backoffMs"], 0)
        self.assertIn("Cy", self.rt.tool("hello", "greet", {"name": "Cy"})["result"]["content"])


if __name__ == "__main__":
    unittest.main()
