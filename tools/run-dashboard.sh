#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API_DLL="$ROOT_DIR/src/dotnet/DistributedAI.Platform/bin/Debug/net10.0/DistributedAI.Platform.dll"
SIMULATOR_DLL="$ROOT_DIR/tools/DistributedAI.NodeSimulator/bin/Debug/net10.0/DistributedAI.NodeSimulator.dll"
WEB_URL="http://127.0.0.1:4200"
API_URL="http://127.0.0.1:5100"
NODE_SOURCE="${1:-simulator}"
API_PID=""
NODE_SOURCE_PID=""
WEB_PID=""

if [[ -f "$ROOT_DIR/infra/.env" ]]; then
  set -a
  # shellcheck disable=SC1091
  source "$ROOT_DIR/infra/.env"
  set +a
fi

cleanup() {
  trap - EXIT INT TERM
  for pid in "$WEB_PID" "$NODE_SOURCE_PID" "$API_PID"; do
    if [[ -n "$pid" ]]; then
      kill -TERM "$pid" 2>/dev/null || true
    fi
  done
  for pid in "$WEB_PID" "$NODE_SOURCE_PID" "$API_PID"; do
    if [[ -n "$pid" ]]; then
      wait "$pid" 2>/dev/null || true
    fi
  done
}
trap cleanup EXIT INT TERM

[[ -f "$API_DLL" ]] || {
  printf 'Build the .NET solution first: dotnet build DistributedAI.slnx\n' >&2
  exit 1
}
[[ -x "$ROOT_DIR/apps/web/node_modules/.bin/ng" ]] || {
  printf 'Install frontend dependencies first: cd apps/web && npm ci\n' >&2
  exit 1
}
case "$NODE_SOURCE" in
  simulator)
    [[ -f "$SIMULATOR_DLL" ]] || {
      printf 'Build the node simulator first: dotnet build DistributedAI.slnx\n' >&2
      exit 1
    }
    ;;
  adb)
    [[ -f "$ROOT_DIR/tools/adb_node_bridge.py" ]] || {
      printf 'ADB bridge not found: tools/adb_node_bridge.py\n' >&2
      exit 1
    }
    ;;
  *)
    printf 'Usage: ./tools/run-dashboard.sh [simulator|adb]\n' >&2
    exit 2
    ;;
esac

PLATFORM_URLS="$API_URL" dotnet "$API_DLL" &
API_PID=$!

api_ready=0
for attempt in $(seq 1 30); do
  if curl -fsS "$API_URL/healthz" >/dev/null 2>&1; then
    api_ready=1
    break
  fi
  sleep 1
done
[[ "$api_ready" -eq 1 ]] || { printf 'Platform API did not start at %s\n' "$API_URL" >&2; exit 1; }

if [[ "$NODE_SOURCE" == "simulator" ]]; then
  dotnet "$SIMULATOR_DLL" "$API_URL" &
else
  python3 "$ROOT_DIR/tools/adb_node_bridge.py" "$API_URL" &
fi
NODE_SOURCE_PID=$!

cd "$ROOT_DIR/apps/web"
npm start -- --host 127.0.0.1 --port 4200 &
WEB_PID=$!

web_ready=0
for attempt in $(seq 1 60); do
  if curl -fsS "$WEB_URL/" >/dev/null 2>&1; then
    web_ready=1
    break
  fi
  sleep 1
done
[[ "$web_ready" -eq 1 ]] || { printf 'Angular dev server did not start at %s\n' "$WEB_URL" >&2; exit 1; }

nodes_ready=0
for attempt in $(seq 1 20); do
  if curl -fsS "$WEB_URL/api/nodes" | python3 -c 'import json,sys; nodes=json.load(sys.stdin); source=sys.argv[1]; online=[n for n in nodes if n["health"]=="Online"]; valid=(len(nodes)==3 and all(n["nodeId"].startswith("sim-") for n in nodes) and len(online)==3) if source=="simulator" else (len(nodes)>0 and all(n["nodeId"].startswith("adb-") for n in nodes) and len(online)>0); raise SystemExit(0 if valid else 1)' "$NODE_SOURCE" >/dev/null 2>&1; then
    nodes_ready=1
    break
  fi
  sleep 1
done
[[ "$nodes_ready" -eq 1 ]] || { printf 'No valid online nodes were received through the Angular API proxy.\n' >&2; exit 1; }

printf '\nDashboard ready: %s\n' "$WEB_URL"
if [[ "$NODE_SOURCE" == "simulator" ]]; then
  printf 'API: %s | 3 simulated nodes online | API proxy verified\n' "$API_URL"
else
  printf 'API: %s | authorized ADB devices online | API proxy verified\n' "$API_URL"
fi
printf 'Press Ctrl+C to stop the dashboard, API, and simulator.\n\n'
wait "$WEB_PID"
