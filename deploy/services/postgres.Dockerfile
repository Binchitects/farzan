# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
# arena-postgres: the stack's Postgres (pgvector, the image compose runs) with pgBackRest, for
# point-in-time recovery: WAL archived as it is written, full and differential backups, a
# restore to any second (docs/deployment.md, "Backups and restore", PITR).
#
#   docker compose -f docker-compose.yml -f pitr.yml build postgres   # approve first: Debian's packages
#
# Built from the pgvector image already on the host; pgBackRest comes from the distribution's packages.
ARG POSTGRES_IMAGE=pgvector/pgvector:0.8.7-pg16-bookworm
FROM ${POSTGRES_IMAGE}
# Whose image this is, and under which licences (LICENSING.md; tools/licence-check.py keeps it).
LABEL org.opencontainers.image.licenses="(AGPL-3.0-only OR LicenseRef-Farzan-Commercial) AND PostgreSQL" \
      org.opencontainers.image.vendor="Binchitects" \
      org.opencontainers.image.source="https://github.com/Binchitects/farzan"

RUN apt-get update \
 && apt-get upgrade -y \
 && apt-get install -y --no-install-recommends pgbackrest \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /var/log/pgbackrest /var/spool/pgbackrest \
 && chown postgres:postgres /var/log/pgbackrest /var/spool/pgbackrest

LABEL org.opencontainers.image.title="arena-postgres" \
      org.opencontainers.image.description="pgvector's Postgres 16 with pgBackRest"
