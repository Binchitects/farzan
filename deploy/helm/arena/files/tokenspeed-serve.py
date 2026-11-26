#!/usr/bin/env python3
"""
TokenSpeed's server as the supervisor starts it (services/vllm/supervisor.sh, ENGINE_SERVER=tokenspeed): `ts serve` with
the app's arguments, the supervisor's name, host and port, its metrics, and the key.

    serve.py /library/hf/... --tp 2 --max-model-len N --max-num-seqs N ... --served-model-name NAME --host HOST --port PORT

- The key (VLLM_API_KEY, from the supervisor) is handed to TokenSpeed as --api-key inside this process: TokenSpeed's own
  `ts` entry point is called here, with its arguments set in this process, so the key is never on a command line that ps
  shows to anyone in the container.
- Its metrics are on (--enable-metrics), served on the same host as the model, on the port next to it (PORT + 1000).
"""
from __future__ import annotations

import os
import sys

TS = os.environ.get("TOKENSPEED_CLI", "ts")


def value(argv: list[str], flag: str) -> str | None:
    return argv[argv.index(flag) + 1] if flag in argv[:-1] else None


def serve_args(argv: list[str]) -> list[str]:
    """`ts serve`'s arguments for the supervisor's (the key not among them); a ValueError says why not."""
    if not argv or argv[0].startswith("--"):
        raise ValueError("the first argument is the model's snapshot folder")
    port = value(argv, "--port")
    if not value(argv, "--served-model-name") or not (port or "").isdigit():
        raise ValueError("--served-model-name and --port are the supervisor's, and both are needed")
    if "--api-key" in argv:
        raise ValueError("the key is the launcher's to give, not an argument")
    return ["serve", *argv, "--enable-metrics", "--metrics-host", value(argv, "--host") or "0.0.0.0", "--metrics-port", str(int(port) + 1000)]


def entry():
    """TokenSpeed's own `ts` command, as a function of this process (its console script's entry point)."""
    from importlib.metadata import entry_points
    for e in entry_points(group="console_scripts"):
        if e.name == "ts":
            return e.load()
    return None


def main(argv: list[str]) -> int:
    try:
        args = serve_args(argv)
    except ValueError as e:
        print(f"serve.py: {e}", file=sys.stderr, flush=True)
        return 1
    key = os.environ.pop("VLLM_API_KEY", "")
    os.environ.setdefault("HF_HUB_OFFLINE", "1")
    print(f"serve.py: ts {' '.join(args)}", flush=True)
    run = None if os.path.sep in TS else entry()
    if run is None:
        # A ts given by its path (the fake's): run as a program, the key by the environment.
        os.environ["TOKENSPEED_API_KEY"] = key
        os.execv(sys.executable, [sys.executable, TS, *args])
    sys.argv = ["ts", *args, *(["--api-key", key] if key else [])]
    return run() or 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
