# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
# The plugin runtime: plugins' server code, each plugin a worker of its own (supervisor.mjs says how it is kept apart).
# Node only, no packages: nothing to download but the base image, which the offline bundle carries.
FROM node:22-alpine
# Whose image this is, and under which licences (LICENSING.md; tools/licence-check.py keeps it).
LABEL org.opencontainers.image.licenses="AGPL-3.0-only OR LicenseRef-Farzan-Commercial" \
      org.opencontainers.image.vendor="Binchitects" \
      org.opencontainers.image.source="https://github.com/Binchitects/farzan"
COPY supervisor.mjs /opt/plugins/supervisor.mjs
# Root, to give each worker its own uid; compose takes every capability but those away, and the network.
USER root
ENTRYPOINT ["node", "/opt/plugins/supervisor.mjs"]
