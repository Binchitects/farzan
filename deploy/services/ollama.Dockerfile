# arena-ollama: Ollama (github.com/ollama/ollama, MIT) pinned, with the stack's supervisor (services/vllm/supervisor.sh with
# ENGINE_SERVER=ollama), its launcher and its front (services/ollama/serve.py), which need Python: Ollama's own program
# and libraries (its CUDA backends) from its image, on the python image the stack already has.
#
#   docker compose --profile ollama build ollama
#
# Build arguments:
#   OLLAMA_IMAGE   Ollama's image, pinned (ollama/ollama:0.40.18)
#   BASE_IMAGE     the base: python:3.13-slim
#
# Ollama runs nothing from ollama.com (OLLAMA_NO_CLOUD, set by the launcher) and pulls no model: the launcher makes a GGUF
# file of the library its model.
ARG OLLAMA_IMAGE=ollama/ollama:0.40.18
ARG BASE_IMAGE=python:3.13-slim
FROM ${OLLAMA_IMAGE} AS ollama
FROM ${BASE_IMAGE}

COPY --from=ollama /usr/bin/ollama /usr/bin/ollama
COPY --from=ollama /usr/lib/ollama /usr/lib/ollama

ENV OLLAMA_MODELS=/data/models \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility

RUN ollama --version 2>&1 | grep -q "0\." && mkdir -p /data/models

ENTRYPOINT []
