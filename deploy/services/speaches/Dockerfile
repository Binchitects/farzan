# arena-speaches: speaches (speech to text and text to speech, on the CPU) with what its image carries brought up to
# date. Its newest release (0.9.0-rc.3) ships packages with known vulnerabilities (Pillow, urllib3, cryptography,
# starlette, python-multipart, protobuf; Ubuntu's OpenSSL and apt): each is raised to at least the version that fixes
# them, then checked (pip check, and the app imports). Compose builds it with the speech feature on.
ARG SPEACHES_IMAGE=ghcr.io/speaches-ai/speaches:0.9.0-rc.3-cpu
FROM ${SPEACHES_IMAGE}

USER root
RUN apt-get update \
 && apt-get upgrade -y \
 && rm -rf /var/lib/apt/lists/*

USER ubuntu
# PyPI over a slow or lossy link: a minute per read and ten tries before a package counts as missing.
ENV PIP_DEFAULT_TIMEOUT=60 PIP_RETRIES=10 PIP_DISABLE_PIP_VERSION_CHECK=1
RUN --mount=type=cache,target=/home/ubuntu/.cache/pip,uid=1000 \
    python -m ensurepip --upgrade \
 && python -m pip install --upgrade \
      "pillow>=12.2.0" "urllib3>=2.6.3" "cryptography>=48.0.1" "pyOpenSSL>=26.0.0" "starlette>=0.49.1,<0.50" \
      "python-multipart>=0.0.22" "protobuf>=5.29.6,<6" "dnspython>=2.9.0" "h11>=0.16.0" "anyio>=4.14.2" \
      "certifi>=2026.10.18" "fsspec>=2026.6.0" "lxml>=6.1.0" "orjson>=3.11.6" "Brotli>=1.2.0" \
      "jaraco.context>=6.1.0" "setuptools>=78.1.1" "wheel>=0.46.2" "gradio>=6.6.0" \
 && python -m pip check \
 && python -c "import speaches.main, PIL, urllib3, cryptography, starlette; print('speaches imports;', 'pillow', PIL.__version__, 'starlette', starlette.__version__)" \
 && python -m pip uninstall -y pip

LABEL org.opencontainers.image.title="arena-speaches" \
      org.opencontainers.image.description="speaches on the CPU with its vulnerable packages brought up to date"
