# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
# Laya, the decision model (server.py), on the CPU: the GPU is the chat model's.
# The weights are not in the image: the app fetches them into the model library
# (MODELS_DIR/laya/english and multilingual), and this reads them from there.
FROM python:3.13-slim
# Whose image this is, and under which licences (LICENSING.md; tools/licence-check.py keeps it).
LABEL org.opencontainers.image.licenses="AGPL-3.0-only OR LicenseRef-Farzan-Commercial" \
      org.opencontainers.image.vendor="Binchitects" \
      org.opencontainers.image.source="https://github.com/Binchitects/farzan"
# The base's own packages brought up to date first: a fix Debian has shipped is in the image even when the base
# image on the registry is older than it.
RUN apt-get update && apt-get upgrade -y && rm -rf /var/lib/apt/lists/*
# PyPI over a slow or lossy link: a minute per read and ten tries before a package counts as missing (a read
# that timed out read as "no matching distribution"). Only pip reads it.
ENV PIP_DEFAULT_TIMEOUT=60 PIP_RETRIES=10

# PyTorch's CPU build (no CUDA libraries), then Laya and what it needs.
RUN pip install --no-cache-dir --extra-index-url https://download.pytorch.org/whl/cpu \
      torch==2.14.1+cpu laya==0.3.27

COPY server.py /app/server.py

ENV LAYA_DIR=/models/laya \
    LAYA_PORT=8000 \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    HF_HUB_DISABLE_TELEMETRY=1 \
    USE_TF=0 \
    HOME=/tmp \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

# pip out of the image: nothing here installs at run time, and pip carries its own copies of urllib3, msgpack and
# setuptools (with known vulnerabilities in the ones it bundles); Python's ensurepip wheel with it.
RUN python3 -m pip uninstall -y pip \
 && rm -rf /usr/local/lib/python3.13/ensurepip/_bundled /usr/local/bin/pip /usr/local/bin/pip3 /usr/local/bin/pip3.13

USER 65534:65534
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s \
  CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4)"]
CMD ["python", "/app/server.py"]
