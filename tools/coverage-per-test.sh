#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
# Coverage per test of Llm.Tests: the run under dotnet-coverage (one session, its counts reset after each test by
# PerTestCoverageAttribute), each test's lines kept in .cov/tests/, then tools/coverage-redundant.py on them.
#   tools/coverage-per-test.sh                       every test (an hour or two)
#   tools/coverage-per-test.sh "FullyQualifiedName~Dashboard"   some
# Collections run one after another, so each test's lines are its own (background loops add theirs to whichever runs).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(dirname "$here")"
filter="${1:-}"
rm -rf "$root/.cov" && mkdir -p "$root/.cov/tests"
"$here/dn" build tests/Llm.Tests -c Debug -v quiet
"$here/dn" tool restore >/dev/null
test="dotnet test tests/Llm.Tests -c Debug --no-build ${filter:+--filter $filter} -e ARENA_COVER_SESSION=arena-cover -e ARENA_COVER_DIR=/repo/.cov/tests ${ARENA_COVER_KEEP:+-e ARENA_COVER_KEEP=1} -- xUnit.ParallelizeTestCollections=false"
"$here/dn" tool run dotnet-coverage collect --session-id arena-cover --settings tests/coverage.settings.xml -f cobertura -o /repo/.cov/all.cobertura.xml "$test" || true
python3 "$here/coverage-redundant.py" "$root/.cov/tests" > "$root/.cov/redundant.md"
echo "report: .cov/redundant.md"
