# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
# The API: sign-in, OIDC, the chat, the admin and every /api route.
# The web (src/web) is its own image; Traefik sends /api, /connect and
# /.well-known here and everything else there.
#
# Built from the repository root: docker build -f src/Llm.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
# Empty: VERSION at the repository's root (Directory.Build.props). Passed on only when
# set: an empty -p:AppVersion= is a global property and would win over VERSION.
ARG APP_VERSION=
WORKDIR /src
COPY global.json Directory.Build.props VERSION branding.env ./
COPY src/Shared/ src/Shared/
# The names people see (branding.env's unless given): PRODUCT_NAME and CODE_INTEL_NAME, read by Directory.Build.props.
ARG PRODUCT_NAME=
ARG CODE_INTEL_NAME=
ENV PRODUCT_NAME=$PRODUCT_NAME CODE_INTEL_NAME=$CODE_INTEL_NAME
COPY src/Llm.Core/Llm.Core.csproj src/Llm.Core/
COPY src/Llm.Api/Llm.Api.csproj src/Llm.Api/
RUN dotnet restore src/Llm.Api/Llm.Api.csproj
COPY src/Llm.Core/ src/Llm.Core/
COPY src/Llm.Api/ src/Llm.Api/
# The skills that come with Arena, built into the app.
COPY skills/ skills/
# The compliance controls, which Admin → Compliance shows.
COPY docs/compliance/controls.yaml docs/compliance/frameworks.yaml docs/compliance/
COPY docs/compliance/catalogs/ docs/compliance/catalogs/
COPY docs/compliance/policies/ docs/compliance/policies/
COPY docs/compliance/isms.md docs/compliance/
COPY docs/compliance/templates/sign-in-notice.md docs/compliance/templates/
COPY deploy/features deploy/settings deploy/
COPY deploy/services/blender/tool/ deploy/services/blender/tool/
RUN dotnet publish src/Llm.Api/Llm.Api.csproj -c Release --no-restore -o /out ${APP_VERSION:+-p:AppVersion=$APP_VERSION} \
 && rm -f /out/*.pdb \
 && mkdir -p /shared/engine-config /shared/directory /shared/sandbox

# Chiseled: no shell, no package manager, runs as a non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
# Whose image this is, and under which licences (LICENSING.md; tools/licence-check.py keeps it).
LABEL org.opencontainers.image.licenses="AGPL-3.0-only OR LicenseRef-Farzan-Commercial" \
      org.opencontainers.image.vendor="Binchitects" \
      org.opencontainers.image.source="https://github.com/Binchitects/farzan"
WORKDIR /app
COPY --from=api /out ./
# Time zones for the chat's date and time tool: the chiseled image has none,
# and the -extra image carries ICU as well, which nothing here needs.
COPY --from=api /usr/share/zoneinfo /usr/share/zoneinfo
# The dashboards, in Grafana's JSON: the app runs every panel's query itself.
COPY src/Llm.Api/Dashboards/json /dashboards
# The plugins that come with the app (Admin → Plugins).
COPY plugins /plugins
# What it shares with other services, as named volumes: a new volume starts as these, the app's.
COPY --from=api --chown=1000:1000 /shared/ /
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Database__Host=postgres \
    Database__Username=arena \
    Auth__DirectoryFile=/directory/users.yml
# The owner of MODELS_DIR on most hosts: downloads land there as theirs.
USER 1000:1000
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 CMD ["dotnet", "Llm.Api.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "Llm.Api.dll"]
