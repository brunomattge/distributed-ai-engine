#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API_DLL="$ROOT_DIR/src/dotnet/DistributedAI.Platform/bin/Debug/net10.0/DistributedAI.Platform.dll"
SIMULATOR_DLL="$ROOT_DIR/tools/DistributedAI.NodeSimulator/bin/Debug/net10.0/DistributedAI.NodeSimulator.dll"
BASE_URL="${PLATFORM_URL:-http://127.0.0.1:5100}"
API_PID=""
SIMULATOR_PID=""

cleanup() {
  if [[ -n "$SIMULATOR_PID" ]]; then
    kill -TERM "$SIMULATOR_PID" 2>/dev/null || true
    wait "$SIMULATOR_PID" 2>/dev/null || true
  fi
  if [[ -n "$API_PID" ]]; then
    kill -TERM "$API_PID" 2>/dev/null || true
    wait "$API_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT

[[ -f "$API_DLL" ]] || { printf 'Build the solution first: dotnet build DistributedAI.slnx\n' >&2; exit 1; }
[[ -f "$SIMULATOR_DLL" ]] || { printf 'Build the node simulator first: dotnet build DistributedAI.slnx\n' >&2; exit 1; }

PLATFORM_URLS="$BASE_URL" dotnet "$API_DLL" &
API_PID=$!

ready=0
for attempt in $(seq 1 30); do
  if curl -fsS "$BASE_URL/healthz" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
[[ "$ready" -eq 1 ]] || { printf 'Platform API did not become ready at %s\n' "$BASE_URL" >&2; exit 1; }
printf 'API health endpoint: online\n'

dotnet "$SIMULATOR_DLL" "$BASE_URL" &
SIMULATOR_PID=$!

online_count=0
for attempt in $(seq 1 20); do
  nodes_json="$(curl -fsS "$BASE_URL/api/nodes")"
  online_count="$(printf '%s' "$nodes_json" | python3 -c 'import json,sys; print(sum(n["health"] == "Online" for n in json.load(sys.stdin)))')"
  [[ "$online_count" -eq 3 ]] && break
  sleep 1
done
[[ "$online_count" -eq 3 ]] || { printf 'Expected three simulated nodes online; found %s\n' "$online_count" >&2; exit 1; }
printf 'Simulated workers: 3/3 online\n'

kill -TERM "$SIMULATOR_PID"
wait "$SIMULATOR_PID" 2>/dev/null || true
SIMULATOR_PID=""
printf 'Simulator stopped; waiting for the configured 6-second heartbeat timeout.\n'
sleep 7

offline_count="$(curl -fsS "$BASE_URL/api/nodes" | python3 -c 'import json,sys; print(sum(n["health"] == "Offline" for n in json.load(sys.stdin)))')"
[[ "$offline_count" -eq 3 ]] || { printf 'Expected three offline nodes after timeout; found %s\n' "$offline_count" >&2; exit 1; }
printf 'Failure detection: 3/3 nodes marked Offline after heartbeat timeout\n'

test_node_id="disconnect-smoke"
curl -fsS -X POST "$BASE_URL/api/nodes" \
  -H 'Content-Type: application/json' \
  -d '{"nodeId":"disconnect-smoke","name":"Disconnect smoke node","hardwareModel":"test"}' >/dev/null
curl -fsS -X POST "$BASE_URL/api/nodes/$test_node_id/heartbeat" \
  -H 'Content-Type: application/json' \
  -d '{"cpuPercent":10,"availableRamMiB":512,"temperatureCelsius":35,"latencyMs":1}' >/dev/null
curl -fsS -X POST "$BASE_URL/api/nodes/$test_node_id/disconnect" -d '{}' >/dev/null
health_after_disconnect="$(curl -fsS "$BASE_URL/api/nodes" | python3 -c 'import json,sys; nodes=json.load(sys.stdin); print(next(n["health"] for n in nodes if n["nodeId"] == sys.argv[1]))' "$test_node_id")"
[[ "$health_after_disconnect" == "Offline" ]] || { printf 'Explicit disconnect should be immediate; found %s\n' "$health_after_disconnect" >&2; exit 1; }
printf 'Explicit disconnect endpoint: immediate Offline transition verified\n'

curl -fsS -X POST "$BASE_URL/api/nodes/$test_node_id/heartbeat" \
  -H 'Content-Type: application/json' \
  -d '{"cpuPercent":10,"availableRamMiB":512,"temperatureCelsius":35,"latencyMs":1}' >/dev/null
health_after_heartbeat="$(curl -fsS "$BASE_URL/api/nodes" | python3 -c 'import json,sys; nodes=json.load(sys.stdin); print(next(n["health"] for n in nodes if n["nodeId"] == sys.argv[1]))' "$test_node_id")"
[[ "$health_after_heartbeat" == "Online" ]] || { printf 'Heartbeat should restore Online; found %s\n' "$health_after_heartbeat" >&2; exit 1; }
printf 'Reconnect heartbeat: Online transition verified\n'
printf 'Phase 1 API/simulator smoke test: PASS\n'
