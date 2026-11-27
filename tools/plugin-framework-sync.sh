#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
# Copyright (C) 2026 Binchitects
# Takes the plugin framework's conformance suite (its manifests and their expected errors, its packages and what
# verifying each must say, and the publisher's public key) into tests/fixtures/plugins, and records which framework
# commit it came from, with each file's SHA-256, in plugins/framework.lock. Arena's tests hold Arena to that suite.
#   tools/plugin-framework-sync.sh /path/to/farzan-arena-plugin-framework
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
fw="${1:?give the path of a checkout of the framework}"
[ -f "$fw/conformance/manifests/expected.json" ] || { echo "$fw has no conformance suite" >&2; exit 1; }
out="$here/tests/fixtures/plugins"
rm -rf "$out"
mkdir -p "$out"
cp -r "$fw/conformance/manifests" "$fw/conformance/packages" "$fw/conformance/keys" "$out/"
commit="$(git -C "$fw" rev-parse HEAD)"
python3 - "$fw" "$out" "$commit" "$here/plugins/framework.lock" <<'PYEOF'
import hashlib, json, os, sys
fw, out, commit, lock = sys.argv[1:]
version = json.load(open(os.path.join(fw, "package.json")))["version"]
lines = ["# The plugin framework Arena is held to (tools/plugin-framework-sync.sh writes this).",
         "framework: Binchitects/farzan-arena-plugin-framework", f"version: {version}", f"commit: {commit}", "files:"]
for root, _, files in sorted(os.walk(out)):
    for f in sorted(files):
        path = os.path.join(root, f)
        lines.append(f"  {os.path.relpath(path, out)}: {hashlib.sha256(open(path, 'rb').read()).hexdigest()}")
open(lock, "w").write("\n".join(lines) + "\n")
print(f"tests/fixtures/plugins from {commit} ({version})")
PYEOF
