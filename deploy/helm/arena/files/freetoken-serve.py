#!/usr/bin/env python3
"""
FreeToken's server as the supervisor starts it (services/vllm/supervisor.sh, ENGINE_SERVER=freetoken): `ft serve` with
the app's arguments, the model's path as its --model, the supervisor's name, host and port, its GPU, and the key.

    serve.py /library/... --max-seq-len-override N --max-running-requests N ... --served-model-name NAME --host HOST --port PORT

- The model is a GGUF file of the library or a snapshot's folder.
- FreeToken runs a model on one GPU (its experts that do not fit there in RAM): the supervisor's CUDA_VISIBLE_DEVICES,
  one GPU, given to FreeToken as its --gpu (nvidia-smi's number) and taken out of the environment, so FreeToken does not
  number the GPUs again. More than one is refused.
- The key (VLLM_API_KEY, from the supervisor) goes to FreeToken by the environment (FREETOKEN_API_KEY), never on a
  command line. FreeToken asks it on every route but /health, which the supervisor reads to know it is ready.
"""
from __future__ import annotations

import os
import sys

FT = os.environ.get("FREETOKEN_CLI", "ft")


def value(argv: list[str], flag: str) -> str | None:
    return argv[argv.index(flag) + 1] if flag in argv[:-1] else None


def serve_args(argv: list[str], gpus: str | None) -> list[str]:
    """`ft serve`'s arguments for the supervisor's; a ValueError says why not."""
    if not argv or argv[0].startswith("--"):
        raise ValueError("the first argument is the model's file or folder")
    if not value(argv, "--served-model-name") or not (value(argv, "--port") or "").isdigit():
        raise ValueError("--served-model-name and --port are the supervisor's, and both are needed")
    if any(a in ("--api-key", "--gpu") for a in argv):
        raise ValueError("the key and the GPU are the launcher's to give, not arguments")
    out = ["serve", "--model", argv[0], *argv[1:]]
    if gpus:
        if "," in gpus:
            raise ValueError(f"FreeToken runs a model on one GPU, and this one was given {gpus}")
        out += ["--gpu", gpus]
    return out


def main(argv: list[str]) -> int:
    gpus = os.environ.pop("CUDA_VISIBLE_DEVICES", None)
    os.environ.pop("HIP_VISIBLE_DEVICES", None)
    try:
        args = serve_args(argv, gpus)
    except ValueError as e:
        print(f"serve.py: {e}", file=sys.stderr, flush=True)
        return 1
    key = os.environ.pop("VLLM_API_KEY", "")
    if key:
        os.environ["FREETOKEN_API_KEY"] = key
    os.environ.setdefault("HF_HUB_OFFLINE", "1")
    print(f"serve.py: {FT} {' '.join(args)}", flush=True)
    os.execvp(FT, [FT, *args])
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
