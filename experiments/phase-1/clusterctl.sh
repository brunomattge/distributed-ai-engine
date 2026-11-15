#!/usr/bin/env bash
set -euo pipefail

PHASE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PHASE0_DIR="$PHASE_DIR/../phase-0"
ADB="${ANDROID_ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
WORKER_BIN="$PHASE0_DIR/build-android/bin/ggml-rpc-server"
STATE_DIR="$PHASE_DIR/.runtime"
ACTIVE_FILE="$STATE_DIR/active.tsv"
REMOTE_PORT=50052
LOCAL_PORTS=(50052 50053 50054)
RUN_DIR=""

usage() {
  cat <<'TEXT'
Usage: ./clusterctl.sh {start|status|watch [seconds] [samples]|logs|stop}

  start          Start one RPC worker per phone and create USB ADB tunnels.
  status         Show the current health of all three workers.
  watch [seconds] [samples]
                 Poll periodically; 0 samples means continuous monitoring.
  logs           Show the latest lines from phone RPC logs.
  stop           Stop started workers and remove USB tunnels.
TEXT
}

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

log() {
  local message="$*"
  printf '%s\n' "$message"
  if [[ -n "$RUN_DIR" ]]; then
    printf '%s\n' "$message" >> "$RUN_DIR/cluster.log"
  fi
}

require_tools() {
  [[ -x "$ADB" ]] || fail "ADB not found at $ADB. Set ANDROID_ADB to the correct path."
  command -v nc >/dev/null 2>&1 || fail "The nc command is unavailable on this Mac."
}

load_session() {
  [[ -f "$ACTIVE_FILE" ]] || fail "No active cluster is registered. Run ./clusterctl.sh start first."
  RUN_DIR="$(cat "$STATE_DIR/active-run")"
  [[ -d "$RUN_DIR" ]] || fail "Session directory does not exist: $RUN_DIR"
}

friendly_model() {
  case "$1" in
    SM_A075M|SM-A075M) printf 'Galaxy A07' ;;
    SM_A065M|SM-A065M) printf 'Galaxy A06' ;;
    *) printf '%s' "$1" ;;
  esac
}

start_cluster() {
  require_tools
  [[ -x "$WORKER_BIN" ]] || fail "Worker Android not found: $WORKER_BIN"
  [[ ! -f "$ACTIVE_FILE" ]] || fail "A session is already active. Check ./clusterctl.sh status or stop it with ./clusterctl.sh stop."

  local serials=()
  while IFS= read -r serial; do
    [[ -n "$serial" ]] && serials+=("$serial")
  done < <("$ADB" devices | awk 'NR > 1 && $2 == "device" { print $1 }')
  if [[ ${#serials[@]} -ne 3 ]]; then
    "$ADB" devices -l
    fail "Expected 3 authorized phones over USB; found ${#serials[@]}."
  fi

  local models=()
  local android_versions=()
  local i serial model version running
  for i in 0 1 2; do
    serial="${serials[$i]}"
    model=$("$ADB" -s "$serial" shell getprop ro.product.model | tr -d '\r')
    version=$("$ADB" -s "$serial" shell getprop ro.build.version.release | tr -d '\r')
    running=$("$ADB" -s "$serial" shell 'pidof ggml-rpc-server' | tr -d '\r' || true)
    [[ -z "$running" ]] || fail "$serial already has an active ggml-rpc-server. Stop the existing session before starting another."
    models+=("$model")
    android_versions+=("$version")
  done

  mkdir -p "$STATE_DIR" "$PHASE_DIR/results"
  RUN_DIR="$PHASE_DIR/results/cluster-$(date +%Y%m%d-%H%M%S)"
  mkdir -p "$RUN_DIR"
  : > "$ACTIVE_FILE"
  printf '%s\n' "$RUN_DIR" > "$STATE_DIR/active-run"
  for i in 0 1 2; do
    printf '%s|%s|%s|%s\n' "${serials[$i]}" "${models[$i]}" "${android_versions[$i]}" "${LOCAL_PORTS[$i]}" >> "$ACTIVE_FILE"
  done

  log "Phase 1 — starting the distributed USB cluster"
  log "Session: $RUN_DIR"
  for i in 0 1 2; do
    serial="${serials[$i]}"
    log "Worker $((i + 1)): copying and starting ${models[$i]} (Android ${android_versions[$i]})"
    if ! "$ADB" -s "$serial" push "$WORKER_BIN" /data/local/tmp/ggml-rpc-server >/dev/null; then
      log "Failed to copy the worker to $serial; cleaning up the partial startup."
      stop_cluster
      exit 1
    fi
    "$ADB" -s "$serial" shell chmod 755 /data/local/tmp/ggml-rpc-server
    "$ADB" -s "$serial" shell "GGML_RPC_DEBUG=2 nohup /data/local/tmp/ggml-rpc-server -H 127.0.0.1 -p $REMOTE_PORT > /data/local/tmp/ggml-rpc-server-phase1.log 2>&1 < /dev/null &"
    "$ADB" -s "$serial" forward --remove "tcp:${LOCAL_PORTS[$i]}" >/dev/null 2>&1 || true
    if ! "$ADB" -s "$serial" forward "tcp:${LOCAL_PORTS[$i]}" "tcp:$REMOTE_PORT" >/dev/null; then
      log "Failed to create a USB tunnel for Worker $((i + 1)); cleaning up the partial startup."
      stop_cluster
      exit 1
    fi
  done

  local ready
  for i in 0 1 2; do
    ready=0
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
      if nc -z -w 2 127.0.0.1 "${LOCAL_PORTS[$i]}" >/dev/null 2>&1; then
        ready=1
        break
      fi
      sleep 1
    done
    if [[ "$ready" -ne 1 ]]; then
      log "Worker $((i + 1)) did not respond on port ${LOCAL_PORTS[$i]}; checking its log."
      "$ADB" -s "${serials[$i]}" exec-out cat /data/local/tmp/ggml-rpc-server-phase1.log > "$RUN_DIR/worker-$((i + 1)).log" 2>/dev/null || true
      stop_cluster
      exit 1
    fi
    log "Worker $((i + 1)): ${models[$i]} — RPC TCP port is reachable at 127.0.0.1:${LOCAL_PORTS[$i]}"
  done

  log "Cluster ready: all 3 RPC TCP ports are reachable. Run ./clusterctl.sh watch to monitor them."
  printf '%s\n' "Started: $(date '+%Y-%m-%d %H:%M:%S')" > "$RUN_DIR/status.log"
  printf '%s\n' "Transport: USB via ADB" >> "$RUN_DIR/status.log"
  printf '%s\n' "Devices: ${models[*]}" >> "$RUN_DIR/status.log"
}

status_once() {
  load_session
  require_tools
  local timestamp serial model version port state pid process_state rpc_state all_online=1 worker=0
  timestamp="$(date '+%Y-%m-%d %H:%M:%S')"
  printf '\n%s — Cluster health\n' "$timestamp"
  while IFS='|' read -r serial model version port; do
    [[ -n "$serial" ]] || continue
    worker=$((worker + 1))
    state=$("$ADB" -s "$serial" get-state </dev/null 2>/dev/null || true)
    if [[ "$state" == "device" ]]; then
      state="connected"
      pid=$("$ADB" -s "$serial" shell 'pidof ggml-rpc-server' </dev/null 2>/dev/null | tr -d '\r' || true)
      if [[ -n "$pid" ]]; then process_state="running"; else process_state="stopped"; all_online=0; fi
    else
      state="disconnected"
      process_state="unavailable"
      all_online=0
    fi
    if [[ "$state" == "connected" ]] && nc -z -w 2 127.0.0.1 "$port" </dev/null >/dev/null 2>&1; then
      rpc_state="online"
    else
      rpc_state="offline"
      all_online=0
    fi
    printf 'Worker %s — %s (%s): ADB %s | process %s | TCP RPC %s (port %s)\n' \
      "$worker" "$(friendly_model "$model")" "$model" "$state" "$process_state" "$rpc_state" "$port"
    printf '%s | worker=%s | serial=%s | model=%s | adb=%s | process=%s | rpc_tcp=%s | port=%s\n' \
      "$timestamp" "$worker" "$serial" "$model" "$state" "$process_state" "$rpc_state" "$port" >> "$RUN_DIR/status.log"
  done < "$ACTIVE_FILE"

  if [[ "$all_online" -eq 1 && "$worker" -eq 3 ]]; then
    printf 'Cluster: all 3 RPC TCP ports are reachable\n'
    return 0
  fi
  printf 'Cluster: INCOMPLETE — check USB connections, RPC processes, and TCP ports.\n'
  return 1
}

watch_cluster() {
  local interval="${1:-2}"
  local sample_limit="${2:-0}"
  local sample_count=0
  [[ "$interval" =~ ^[0-9]+$ && "$interval" -ge 1 && "$interval" -le 60 ]] || fail "Interval must be a number from 1 to 60 seconds."
  [[ "$sample_limit" =~ ^[0-9]+$ ]] || fail "Sample count must be an integer."
  load_session
  if [[ "$sample_limit" -eq 0 ]]; then
    printf 'Continuous monitoring is active. Ctrl+C stops monitoring but leaves workers running.\n'
  else
    printf 'Monitoring %s samples every %s second(s).\n' "$sample_limit" "$interval"
  fi
  printf 'Samples are recorded in %s/status.log\n' "$RUN_DIR"
  trap 'printf "\nMonitoring stopped; the cluster is still active. Run ./clusterctl.sh stop when you want to shut it down.\n"; exit 0' INT
  while true; do
    status_once || true
    sample_count=$((sample_count + 1))
    if [[ "$sample_limit" -gt 0 && "$sample_count" -ge "$sample_limit" ]]; then
      printf "Monitoring finished after %s samples; workers remain active.\n" "$sample_count"
      break
    fi
    sleep "$interval"
  done
}

show_logs() {
  load_session
  require_tools
  local worker=0 serial model version port
  while IFS='|' read -r serial model version port; do
    [[ -n "$serial" ]] || continue
    worker=$((worker + 1))
    printf '\n--- Worker %s — %s (%s), last 20 lines ---\n' "$worker" "$(friendly_model "$model")" "$model"
    "$ADB" -s "$serial" exec-out tail -n 20 /data/local/tmp/ggml-rpc-server-phase1.log </dev/null 2>&1 || true
  done < "$ACTIVE_FILE"
}

stop_cluster() {
  [[ -f "$ACTIVE_FILE" ]] || fail "No active cluster is registered."
  load_session
  require_tools
  local worker=0 serial model version port
  while IFS='|' read -r serial model version port; do
    [[ -n "$serial" ]] || continue
    worker=$((worker + 1))
    "$ADB" -s "$serial" exec-out cat /data/local/tmp/ggml-rpc-server-phase1.log </dev/null > "$RUN_DIR/worker-$worker.log" 2>/dev/null || true
    "$ADB" -s "$serial" shell 'pid="$(pidof ggml-rpc-server)"; if [ -n "$pid" ]; then kill $pid; fi' </dev/null >/dev/null 2>&1 || true
    "$ADB" -s "$serial" forward --remove "tcp:$port" </dev/null >/dev/null 2>&1 || true
    log "Worker $worker stopped; tunnel for port $port removed."
  done < "$ACTIVE_FILE"
  printf '%s\n' "Stopped: $(date '+%Y-%m-%d %H:%M:%S')" >> "$RUN_DIR/status.log"
  rm -f "$ACTIVE_FILE" "$STATE_DIR/active-run"
  log "Cluster stopped. Session logs: $RUN_DIR"
}

command="${1:-}"
case "$command" in
  start) start_cluster ;;
  status) status_once ;;
  watch) shift; watch_cluster "${1:-2}" "${2:-0}" ;;
  logs) show_logs ;;
  stop) stop_cluster ;;
  -h|--help|help) usage ;;
  *) usage >&2; exit 2 ;;
esac
