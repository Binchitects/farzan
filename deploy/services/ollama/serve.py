#!/usr/bin/env python3
"""
Ollama as the supervisor starts it (services/vllm/supervisor.sh, ENGINE_SERVER=ollama): one `ollama serve` a model, on
the model's GPUs, behind a front that asks the engine key, since Ollama's own server asks none.

    serve.py /library/....gguf --context N --parallel N --kv q8_0 --served-model-name NAME --host HOST --port PORT

- Ollama listens on 127.0.0.1 only, on a port of its own; the front listens on the supervisor's HOST:PORT and passes
  every request on once it carries the key (Authorization: Bearer KEY, the key from VLLM_API_KEY, which the front takes
  out of the environment before Ollama starts). /health answers 200 once the model is loaded, and /metrics counts the
  requests running and waiting, with vLLM's names, which the app reads; neither needs the key, as in vLLM.
- The model is a GGUF file of the library, made into an Ollama model named NAME (`ollama create` from a Modelfile:
  its context, nothing pulled). Ollama keeps its own copy of the file in OLLAMA_MODELS (a volume): a second start of the
  same file copies nothing again.
- Ollama runs nothing from ollama.com (OLLAMA_NO_CLOUD), keeps the model loaded (OLLAMA_KEEP_ALIVE=-1), one model only,
  with --parallel answers at once and its cache at --kv (f16, q8_0 or q4_0, with flash attention).
"""
from __future__ import annotations

import http.client
import http.server
import json
import os
import signal
import socket
import socketserver
import subprocess
import sys
import tempfile
import threading
import time

OLLAMA = os.environ.get("OLLAMA_CLI", "ollama")
MODELS = os.environ.get("OLLAMA_MODELS", "/data/models")
KV = {"f16", "q8_0", "q4_0"}
HOP = {"connection", "keep-alive", "transfer-encoding", "te", "trailer", "upgrade", "proxy-authorization", "proxy-authenticate", "host",
       "content-length", "authorization"}


def value(argv: list[str], flag: str) -> str | None:
    return argv[argv.index(flag) + 1] if flag in argv[:-1] else None


def plan(argv: list[str]) -> dict:
    """What to run for the supervisor's arguments; a ValueError says why not."""
    if not argv or argv[0].startswith("--"):
        raise ValueError("the first argument is the model's GGUF file")
    path = argv[0]
    if not path.lower().endswith(".gguf"):
        raise ValueError(f"Ollama runs GGUF files here, and {path} is not one")
    name, port, host = value(argv, "--served-model-name"), value(argv, "--port"), value(argv, "--host") or "0.0.0.0"
    if not name or not (port or "").isdigit():
        raise ValueError("--served-model-name and --port are the supervisor's, and both are needed")
    context = value(argv, "--context") or "4096"
    parallel = value(argv, "--parallel") or "1"
    kv = value(argv, "--kv") or "f16"
    if not context.isdigit() or not parallel.isdigit():
        raise ValueError("--context and --parallel are numbers")
    if kv not in KV:
        raise ValueError(f"--kv is one of {', '.join(sorted(KV))}, not {kv}")
    modelfile = f"FROM {path}\nPARAMETER num_ctx {context}\n"
    env = {"OLLAMA_MODELS": MODELS, "OLLAMA_NUM_PARALLEL": parallel, "OLLAMA_CONTEXT_LENGTH": context, "OLLAMA_KV_CACHE_TYPE": kv,
           "OLLAMA_FLASH_ATTENTION": "1", "OLLAMA_KEEP_ALIVE": "-1", "OLLAMA_MAX_LOADED_MODELS": "1", "OLLAMA_NO_CLOUD": "1",
           "OLLAMA_NOPRUNE": "1"}
    return {"path": path, "name": name, "host": host, "port": int(port), "modelfile": modelfile, "env": env}


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def ask(port: int, method: str, path: str, body: dict | None = None, timeout: float = 5) -> tuple[int, dict]:
    c = http.client.HTTPConnection("127.0.0.1", port, timeout=timeout)
    try:
        c.request(method, path, json.dumps(body) if body is not None else None, {"Content-Type": "application/json"})
        r = c.getresponse()
        data = r.read()
        try:
            return r.status, json.loads(data or b"{}")
        except json.JSONDecodeError:
            return r.status, {}
    finally:
        c.close()


class Front(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address, key: str, inner: int, name: str):
        self.key, self.inner, self.name = key, inner, name
        self.loaded = False
        self.running = 0
        self.lock = threading.Lock()
        super().__init__(address, Handler)


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server: Front

    def log_message(self, *_):
        pass

    def answer(self, status: int, body: bytes, kind: str = "application/json"):
        self.send_response(status)
        self.send_header("Content-Type", kind)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def handle_one(self):
        path = self.path.split("?", 1)[0]
        if path == "/health":
            return self.answer(200 if self.server.loaded else 503, b"{}")
        if path == "/metrics":
            with self.server.lock:
                running = self.server.running
            text = (f'vllm:num_requests_running{{model_name="{self.server.name}"}} {running}\n'
                    f'vllm:num_requests_waiting{{model_name="{self.server.name}"}} 0\n')
            return self.answer(200, text.encode(), "text/plain; version=0.0.4")
        if not self.server.key or self.headers.get("Authorization", "") != f"Bearer {self.server.key}":
            return self.answer(401, b'{"error": "the engine key is needed"}')
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else None
        headers = {k: v for k, v in self.headers.items() if k.lower() not in HOP}
        with self.server.lock:
            self.server.running += 1
        c = http.client.HTTPConnection("127.0.0.1", self.server.inner, timeout=3600)
        try:
            c.request(self.command, self.path, body, headers)
            r = c.getresponse()
            self.send_response(r.status)
            for k, v in r.getheaders():
                if k.lower() not in HOP:
                    self.send_header(k, v)
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
            while True:
                chunk = r.read1(65536)
                if not chunk:
                    break
                self.wfile.write(b"%x\r\n%s\r\n" % (len(chunk), chunk))
                self.wfile.flush()
            self.wfile.write(b"0\r\n\r\n")
        except (OSError, http.client.HTTPException):
            self.close_connection = True
        finally:
            c.close()
            with self.server.lock:
                self.server.running -= 1

    do_GET = do_POST = do_DELETE = do_PUT = handle_one


def main(argv: list[str]) -> int:
    try:
        p = plan(argv)
    except ValueError as e:
        print(f"serve.py: {e}", file=sys.stderr, flush=True)
        return 1
    key = os.environ.pop("VLLM_API_KEY", "")
    inner = free_port()
    env = dict(os.environ, **p["env"], OLLAMA_HOST=f"127.0.0.1:{inner}")
    os.makedirs(MODELS, exist_ok=True)
    stopping = threading.Event()
    signal.signal(signal.SIGTERM, lambda *_: stopping.set())
    signal.signal(signal.SIGINT, lambda *_: stopping.set())
    print(f"serve.py: ollama serve on 127.0.0.1:{inner} for {p['name']}", flush=True)
    ollama = subprocess.Popen([OLLAMA, "serve"], env=env)
    front = Front((p["host"], p["port"]), key, inner, p["name"])
    threading.Thread(target=front.serve_forever, daemon=True).start()
    try:
        for _ in range(600):
            if stopping.is_set() or ollama.poll() is not None:
                return 0 if stopping.is_set() else 1
            try:
                if ask(inner, "GET", "/api/version")[0] == 200:
                    break
            except OSError:
                pass
            time.sleep(0.5)
        else:
            print("serve.py: ollama did not start in 5 minutes", file=sys.stderr, flush=True)
            return 1
        with tempfile.NamedTemporaryFile("w", suffix=".Modelfile", delete=False) as f:
            f.write(p["modelfile"])
        made = subprocess.run([OLLAMA, "create", p["name"], "-f", f.name], env=env)
        os.unlink(f.name)
        if made.returncode != 0:
            print(f"serve.py: ollama create {p['name']} failed ({made.returncode})", file=sys.stderr, flush=True)
            return 1
        # Loaded now, and kept: a request with no prompt loads a model.
        status, _ = ask(inner, "POST", "/api/generate", {"model": p["name"], "keep_alive": -1}, timeout=3600)
        if status != 200:
            print(f"serve.py: ollama could not load {p['name']} ({status})", file=sys.stderr, flush=True)
            return 1
        front.loaded = True
        print(f"serve.py: {p['name']} is loaded", flush=True)
        while not stopping.is_set() and ollama.poll() is None:
            stopping.wait(1)
        return 0 if stopping.is_set() else 1
    finally:
        front.loaded = False
        # Drained: Ollama ends the answers it has as it stops.
        if ollama.poll() is None:
            ollama.send_signal(signal.SIGTERM)
            try:
                ollama.wait(110)
            except subprocess.TimeoutExpired:
                ollama.kill()
        front.shutdown()


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
