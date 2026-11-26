# arena-freetoken: FreeToken (github.com/FlashML-org/FreeToken, Apache-2.0) pinned from PyPI, with the stack's supervisor
# (services/vllm/supervisor.sh with ENGINE_SERVER=freetoken), built on SGLang's image, which already has what FreeToken
# runs on: PyTorch 2.14.1 for CUDA 13.0 (the one sglang-kernel, which FreeToken pins, takes), Triton and CUDA 13's nvcc,
# which compiles FreeToken's kernels as they are first used.
#
#   docker compose --profile freetoken build freetoken      # about 0.4 GB fetched from PyPI, on SGLang's image
#
# Build arguments:
#   BASE_IMAGE         the base: SGLang's own image (lmsysorg/sglang:v0.5.23-cu130-runtime), as the sglang service runs
#   FREETOKEN_VERSION  the FreeToken release, pinned (0.2.3)
#   PIP_INDEX_URL      a PyPI mirror the admin names in place of pypi.org: empty (the default) keeps pypi.org
#
# FreeToken goes into the image's own Python (SGLang's, /opt/sglang), with no resolve of its dependencies (--no-deps):
# PyTorch, torchvision, Triton, FlashInfer and the rest are the image's; only the packages FreeToken pins to other versions
# than the image has (sglang-kernel, transformers, pillow, tvm-ffi) and its own (flashlib) are fetched, so the build can
# never fetch another PyTorch or CUDA. FlashInfer stays the image's 0.7.2 (FreeToken asks 0.6.18.post1, whose prebuilt
# kernels PyPI does not have, so it would fetch them as it runs): its kernels for every GPU are in the image. Its kernels are compiled once a GPU runs them (a build has none), into
# FREETOKEN_CACHE (a volume): a second start compiles nothing. Models come from the library (HF_HUB_OFFLINE: FreeToken
# never fetches one itself).
ARG BASE_IMAGE=lmsysorg/sglang:v0.5.23-cu130-runtime
FROM ${BASE_IMAGE}

ARG FREETOKEN_VERSION=0.2.3
ARG PIP_INDEX_URL=

# Checked by its version and imports: FreeToken's server looks for a GPU, which a build has none of. pip's cache is kept
# between builds (a cache mount, not in the image).
RUN --mount=type=cache,target=/root/.cache/pip \
    python3 -m pip install --no-deps --timeout 60 --retries 5 ${PIP_INDEX_URL:+--index-url "$PIP_INDEX_URL"} \
      "freetoken==${FREETOKEN_VERSION}" "sglang-kernel==0.4.9" "flashlib==0.4.0" \
      "apache-tvm-ffi==0.1.13.post3" "transformers==5.18.0" "pillow==12.2.0" \
 && ft --version \
 && python3 -c "import freetoken.server.args, flashinfer, flashlib, torch; print('torch', torch.__version__)"

ENV HF_HUB_OFFLINE=1 \
    FREETOKEN_CACHE=/cache \
    TORCH_EXTENSIONS_DIR=/cache/torch-extensions \
    TRITON_CACHE_DIR=/cache/triton \
    FLASHINFER_WORKSPACE_BASE=/cache/flashinfer

ENTRYPOINT []
