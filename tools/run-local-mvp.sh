#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INFERENCE_LAUNCHER="${INFERENCE_LAUNCHER:-$ROOT_DIR/experiments/phase-3/start-mvp.sh}"
DASHBOARD_LAUNCHER="${DASHBOARD_LAUNCHER:-$ROOT_DIR/tools/run-dashboard.sh}"
ADB="${ANDROID_ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
MODEL_PROFILE="${1:-fast}"
WORKER_COUNT="${INFERENCE_WORKER_COUNT:-3}"
INFERENCE_PID=""
DASHBOARD_PID=""
MODEL_HEALTH_URL="${MODEL_HEALTH_URL:-http://127.0.0.1:8000/api/health}"
INFERENCE_STATUS_URL="${INFERENCE_STATUS_URL:-http://127.0.0.1:5100/api/inference-status}"
MODEL_READY_TIMEOUT_SECONDS="${MODEL_READY_TIMEOUT_SECONDS:-1800}"
CLUSTER_READY_TIMEOUT_SECONDS="${CLUSTER_READY_TIMEOUT_SECONDS:-90}"

usage() {
  cat <<'TEXT'
Usage: ./tools/run-local-mvp.sh [fast|large|path/to/model.gguf]

  fast       Qwen2.5 0.5B Q4_K_M (default)
  large      Qwen2.5 7B Q3_K_M
  model path Use a GGUF already supported by the Phase 3 launcher

Starts the USB Android cluster, distributed model, Python runtime, .NET platform,
ADB telemetry bridge, and Angular dashboard in one Terminal session.
Press Ctrl+C to stop the local services and Android RPC workers.
TEXT
}

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

cleanup() {
  local exit_code=$?
  trap - EXIT INT TERM
  for pid in "$DASHBOARD_PID" "$INFERENCE_PID"; do
    if [[ -n "$pid" ]]; then
      kill -TERM "$pid" 2>/dev/null || true
    fi
  done
  for pid in "$DASHBOARD_PID" "$INFERENCE_PID"; do
    if [[ -n "$pid" ]]; then
      wait "$pid" 2>/dev/null || true
    fi
  done
  exit "$exit_code"
}

trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

[[ -x "$INFERENCE_LAUNCHER" ]] || fail "Inference launcher is not executable: $INFERENCE_LAUNCHER"
[[ -x "$DASHBOARD_LAUNCHER" ]] || fail "Dashboard launcher is not executable: $DASHBOARD_LAUNCHER"
[[ -x "$ADB" ]] || fail "ADB not found at $ADB. Set ANDROID_ADB to its path."
command -v curl >/dev/null 2>&1 || fail "curl is required."
command -v lsof >/dev/null 2>&1 || fail "lsof is required to check for conflicting local services."
command -v python3 >/dev/null 2>&1 || fail "python3 is required for local readiness checks."
[[ "$WORKER_COUNT" == "3" ]] || fail "The local MVP launcher requires exactly three RPC workers; INFERENCE_WORKER_COUNT is $WORKER_COUNT."
[[ "$MODEL_READY_TIMEOUT_SECONDS" =~ ^[0-9]+$ && "$MODEL_READY_TIMEOUT_SECONDS" -gt 0 ]] || fail "MODEL_READY_TIMEOUT_SECONDS must be a positive integer."
[[ "$CLUSTER_READY_TIMEOUT_SECONDS" =~ ^[0-9]+$ && "$CLUSTER_READY_TIMEOUT_SECONDS" -gt 0 ]] || fail "CLUSTER_READY_TIMEOUT_SECONDS must be a positive integer."

if ! adb_output="$("$ADB" devices 2>&1)"; then
  printf '%s\n' "$adb_output" >&2
  fail "Could not query ADB devices. Start the ADB server and authorize the phones, then retry."
fi
connected_devices="$(printf '%s\n' "$adb_output" | awk 'NR > 1 && $2 == "device" { count++ } END { print count + 0 }')"
[[ "$connected_devices" -eq 3 ]] || fail "Expected exactly three authorized USB Android devices; found $connected_devices. No services were started."
export INFERENCE_WORKER_COUNT=3

for port in 4200 5100 8000 8080; do
  if lsof -nP -iTCP:"$port" -sTCP:LISTEN 2>/dev/null | tail -n +2 | grep -q .; then
    fail "Local port $port is already in use. Stop its service before starting the MVP."
  fi
done

printf 'Starting the local MVP with model profile: %s\n' "$MODEL_PROFILE"
printf 'Starting the USB Android workers and loading the distributed model.\n'
"$INFERENCE_LAUNCHER" "$MODEL_PROFILE" &
INFERENCE_PID=$!

model_ready=0
for ((attempt = 1; attempt <= MODEL_READY_TIMEOUT_SECONDS; attempt++)); do
  if curl -fsS "$MODEL_HEALTH_URL" 2>/dev/null | python3 -c 'import json,sys; p=json.load(sys.stdin); raise SystemExit(0 if p.get("state")=="ready" and p.get("supervisor_state")=="ready" else 1)' >/dev/null 2>&1; then
    model_ready=1
    break
  fi
  if ! kill -0 "$INFERENCE_PID" 2>/dev/null; then
    if wait "$INFERENCE_PID"; then inference_exit=0; else inference_exit=$?; fi
    INFERENCE_PID=""
    fail "The distributed inference launcher exited before the model became ready (exit $inference_exit)."
  fi
  sleep 1
done
[[ "$model_ready" -eq 1 ]] || fail "The distributed model did not become ready within ${MODEL_READY_TIMEOUT_SECONDS}s."

printf 'Distributed model ready. Starting the .NET platform, ADB bridge, and Angular dashboard.\n'
"$DASHBOARD_LAUNCHER" adb &
DASHBOARD_PID=$!

cluster_ready=0
for ((attempt = 1; attempt <= CLUSTER_READY_TIMEOUT_SECONDS; attempt++)); do
  if curl -fsS "$INFERENCE_STATUS_URL" 2>/dev/null | python3 -c 'import json,sys; raise SystemExit(0 if json.load(sys.stdin).get("available") is True else 1)' >/dev/null 2>&1; then
    cluster_ready=1
    break
  fi
  if ! kill -0 "$DASHBOARD_PID" 2>/dev/null; then
    if wait "$DASHBOARD_PID"; then dashboard_exit=0; else dashboard_exit=$?; fi
    DASHBOARD_PID=""
    fail "The dashboard launcher exited before distributed chat became ready (exit $dashboard_exit)."
  fi
  sleep 1
done
[[ "$cluster_ready" -eq 1 ]] || fail "The distributed cluster did not become ready within ${CLUSTER_READY_TIMEOUT_SECONDS}s."

printf '\nLocal MVP ready:\n'
printf '  Cluster dashboard: http://127.0.0.1:4200/cluster\n'
printf '  Chat:              http://127.0.0.1:4200/chat\n'
printf '  Inference API:     http://127.0.0.1:5100/v1\n'
printf '  Terminal client:   python3 examples/clients/stream_completion.py\n'
printf 'Press Ctrl+C to stop all local services and Android RPC workers.\n\n'

while true; do
  if ! kill -0 "$INFERENCE_PID" 2>/dev/null; then
    if wait "$INFERENCE_PID"; then inference_exit=0; else inference_exit=$?; fi
    INFERENCE_PID=""
    fail "The distributed inference launcher stopped unexpectedly (exit $inference_exit)."
  fi
  if ! kill -0 "$DASHBOARD_PID" 2>/dev/null; then
    if wait "$DASHBOARD_PID"; then dashboard_exit=0; else dashboard_exit=$?; fi
    DASHBOARD_PID=""
    fail "The dashboard launcher stopped unexpectedly (exit $dashboard_exit)."
  fi
  sleep 1
done
