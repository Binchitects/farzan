#!/usr/bin/env bash
# A throwaway Arena for making plugins and for the plugins' browser tests, from this checkout, beside anything else on
# the host: nothing of the live stack is touched (its own container names, ports and database), nothing is downloaded
# (Postgres and node:22-alpine must be on the host already), and no image of Arena's is built.
#
#   tools/plugin-dev-stack.sh up      Postgres, the plugin runtime, the app (tools/dn run) and the web (vite) on
#                                     http://localhost:5173; the admin's password is written to .plugin-dev/admin-password
#                                     (0600, never printed); plugins with code may install unsigned here, and dev installs are on
#   tools/plugin-dev-stack.sh down    everything stopped and removed, its data with it
#   tools/plugin-dev-stack.sh status
#
# Then: arena-plugin dev --arena http://localhost:5173 (with ARENA_API_KEY: a person's key, Admin → People), or
#       E2E_BASE_URL=http://localhost:5173 E2E_CHANNEL=chrome E2E_PASSWORD_FILE=.plugin-dev/admin-password npx playwright test e2e/plugins.spec.ts
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
state="$here/.plugin-dev"
name="arena-plugin-dev"
pg_image="${PG_IMAGE:-pgvector/pgvector:0.8.7-pg16-bookworm}"
app_port="${APP_PORT:-5080}"
web_port="${WEB_PORT:-5173}"

up() {
  mkdir -p "$state"
  chmod 700 "$state"
  docker image inspect "$pg_image" >/dev/null 2>&1 || { echo "$pg_image is not on this host (PG_IMAGE names another)" >&2; exit 1; }
  docker image inspect node:22-alpine >/dev/null 2>&1 || { echo "node:22-alpine is not on this host" >&2; exit 1; }
  [ -f "$state/admin-password" ] || (umask 077; head -c 24 /dev/urandom | base64 | tr -d '/+=' > "$state/admin-password")
  [ -f "$state/data-key" ] || (umask 077; head -c 32 /dev/urandom | base64 > "$state/data-key")
  [ -f "$state/pg-password" ] || (umask 077; head -c 18 /dev/urandom | base64 | tr -d '/+=' > "$state/pg-password")
  if ! docker ps --format '{{.Names}}' | grep -qx "$name-pg"; then
    docker rm -f "$name-pg" >/dev/null 2>&1 || true
    docker run -d --name "$name-pg" -p 127.0.0.1::5432 -e POSTGRES_USER=arena -e POSTGRES_DB=arena \
      -e POSTGRES_PASSWORD_FILE=/run/pg-password -v "$state/pg-password:/run/pg-password:ro" "$pg_image" >/dev/null
  fi
  pg_port="$(docker port "$name-pg" 5432 | head -1 | sed 's/.*://')"
  for _ in $(seq 1 60); do docker exec "$name-pg" pg_isready -U arena >/dev/null 2>&1 && break; sleep 1; done
  if ! docker ps --format '{{.Names}}' | grep -qx "$name-runtime"; then
    docker rm -f "$name-runtime" >/dev/null 2>&1 || true
    docker build -q --pull=false -t "$name-runtime:local" "$here/deploy/services/plugins" >/dev/null
    # As compose runs it: no network at all, the app by its socket in .plugin-dev/runtime (its group is yours).
    mkdir -p "$state/runtime"
    docker run -d --name "$name-runtime" --network none --init --cap-drop ALL --cap-add SETUID --cap-add SETGID \
      --cap-add CHOWN --cap-add DAC_OVERRIDE --cap-add FOWNER --cap-add KILL --security-opt no-new-privileges:true --read-only \
      --tmpfs /work:size=512m,mode=0711 --tmpfs /tmp:size=16m --pids-limit 512 --memory 2g \
      -e PLUGINS_SOCKET_GID="$(id -g)" -v "$state/runtime:/plugin-runtime" "$name-runtime:local" >/dev/null
  fi
  if ! curl -s -o /dev/null "http://127.0.0.1:$app_port/api/info"; then
    docker rm -f "$name-app" >/dev/null 2>&1 || true
    echo "Starting the app on :$app_port (tools/dn run; its log: .plugin-dev/app.log)"
    (umask 077; cat > "$state/app.env" <<ENV
ConnectionStrings__App=Host=127.0.0.1;Port=$pg_port;Database=arena;Username=arena;Password=$(cat "$state/pg-password")
ASPNETCORE_URLS=http://127.0.0.1:$app_port
Auth__Domain=localhost
Auth__AdminEmail=admin@plugins.test
Auth__AdminPassword=$(cat "$state/admin-password")
Auth__DataKey=$(cat "$state/data-key")
Plugins__Directory=/repo/plugins
Plugins__AllowUnsigned=true
Plugins__DevInstalls=true
Dashboards__Path=/repo/deploy/config/dashboards
PluginRuntime__Root=/repo/.plugin-dev/runtime
Engine__Enabled=false
ENV
    )
    (cd "$here" && DN_ENV_FILE="$state/app.env" DN_NAME="$name-app" setsid tools/dn run --project src/Llm.Api -c Release --no-launch-profile < /dev/null > "$state/app.log" 2>&1 & echo $! > "$state/app.pid")
    for _ in $(seq 1 240); do curl -s -o /dev/null "http://127.0.0.1:$app_port/api/info" && break; sleep 1; done
  fi
  if ! curl -s -o /dev/null "http://127.0.0.1:$web_port/"; then
    echo "Starting the web on :$web_port (vite; its log: .plugin-dev/web.log)"
    (cd "$here/src/web" && setsid env API_URL="http://127.0.0.1:$app_port" npx vite --port "$web_port" --strictPort < /dev/null > "$state/web.log" 2>&1 & echo $! > "$state/web.pid")
    for _ in $(seq 1 120); do curl -s -o /dev/null "http://127.0.0.1:$web_port/" && break; sleep 1; done
  fi
  status
}

down() {
  for p in web app; do
    if [ -f "$state/$p.pid" ]; then
      # The process group setsid made: vite and its children, tools/dn and its container.
      kill -- "-$(cat "$state/$p.pid")" 2>/dev/null || true
      rm -f "$state/$p.pid"
    fi
  done
  docker rm -f "$name-app" "$name-pg" "$name-runtime" >/dev/null 2>&1 || true
  # The runtime's folder holds root's and the plugins' users' files: emptied from inside a container.
  [ -d "$state/runtime" ] && docker run --rm --network none -v "$state/runtime:/x" node:22-alpine sh -c 'rm -rf /x/* /x/.[!.]*' >/dev/null 2>&1 || true
  echo "Stopped. Its secrets stay in .plugin-dev/ until you remove the folder."
}

status() {
  for c in "$name-pg" "$name-runtime"; do printf '%-26s %s\n' "$c" "$(docker inspect -f '{{.State.Status}}' "$c" 2>/dev/null || echo absent)"; done
  printf '%-26s %s\n' "app :$app_port" "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$app_port/api/info" || echo down)"
  printf '%-26s %s\n' "web :$web_port" "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$web_port/" || echo down)"
}

case "${1:-}" in
  up) up ;;
  down) down ;;
  status) status ;;
  *) sed -n '2,15p' "$0"; exit 64 ;;
esac
