#!/usr/bin/env bash
set -euo pipefail

PHASE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PHASE1_DIR="$PHASE_DIR/../phase-1"
PHASE0_DIR="$PHASE_DIR/../phase-0"
CLUSTER="$PHASE1_DIR/clusterctl.sh"
ACTIVE_FILE="$PHASE1_DIR/.runtime/active.tsv"
SUPERVISOR_STATUS_FILE="$PHASE1_DIR/.runtime/supervisor-status.json"
ADB="${ANDROID_ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
SERVER="$PHASE0_DIR/build-mac/bin/llama-server"
fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
FAST_MODEL="$PHASE0_DIR/models/qwen2.5-0.5b-instruct-q4_k_m.gguf"
LARGE_MODEL="$PHASE0_DIR/models/qwen2.5-7b-instruct-q3_k_m.gguf"
MODEL=""
MODEL_PROFILE=""
if [[ $# -gt 0 ]]; then
  case "$1" in
    fast|1) MODEL="$FAST_MODEL"; MODEL_PROFILE="fast" ;;
    large|7b|2) MODEL="$LARGE_MODEL"; MODEL_PROFILE="large" ;;
    *) MODEL="$1"; MODEL_PROFILE="custom" ;;
  esac
elif [[ -t 0 ]]; then
  printf 'Choose the distributed model for this session:\n'
  printf '  1) Fast — Qwen2.5 0.5B Q4_K_M (measured default)\n'
  printf '  2) Larger — Qwen2.5 7B Q3_K_M\n'
  read -r -p 'Selection [1]: ' selection
  case "$selection" in
    ''|1|fast) MODEL="$FAST_MODEL"; MODEL_PROFILE="fast" ;;
    2|7b|large) MODEL="$LARGE_MODEL"; MODEL_PROFILE="large" ;;
    *) fail "Invalid selection. Use 1 (fast) or 2 (larger)." ;;
  esac
else
  MODEL="$FAST_MODEL"
  MODEL_PROFILE="fast"
fi
[[ "$MODEL" == /* ]] || MODEL="$PWD/$MODEL"
MODEL_ID="$(basename "$MODEL" .gguf)"
case "$MODEL_ID" in
  qwen2.5-0.5b-instruct-q4_k_m) MODEL_NAME="Qwen2.5 0.5B Instruct · Q4_K_M" ;;
  qwen2.5-7b-instruct-q3_k_m) MODEL_NAME="Qwen2.5 7B Instruct · Q3_K_M" ;;
  *) MODEL_NAME="$MODEL_ID" ;;
esac
SERVER_PID=""
API_PID=""
STARTED_CLUSTER=0
RUN_DIR=""
RUN_STARTED="$(date '+%Y-%m-%d %H:%M:%S')"
SERVER_START_EPOCH=0
MODEL_READY_SECONDS="not measured"
RECOVERY_COUNT=0
RECOVERY_GENERATION=0
MAX_RECOVERY_ATTEMPTS="${INFERENCE_MAX_RECOVERIES:-2}"
INFERENCE_WORKER_COUNT="${INFERENCE_WORKER_COUNT:-3}"
[[ "$INFERENCE_WORKER_COUNT" =~ ^[1-3]$ ]] || fail "INFERENCE_WORKER_COUNT must be 1, 2, or 3."
PHONE_WAIT_SECONDS="${INFERENCE_PHONE_WAIT_SECONDS:-600}"
RPC_PORTS=(50052 50053 50054)
RPC_ENDPOINTS=()
RPC_DEVICES=()
for ((worker_index = 0; worker_index < INFERENCE_WORKER_COUNT; worker_index++)); do
  RPC_ENDPOINTS+=("127.0.0.1:${RPC_PORTS[$worker_index]}")
  RPC_DEVICES+=("RPC${worker_index}")
done
RPC_LIST="$(IFS=,; printf '%s' "${RPC_ENDPOINTS[*]}")"
RPC_DEVICE_LIST="$(IFS=,; printf '%s' "${RPC_DEVICES[*]}")"

write_supervisor_status() {
  local state="$1" message="$2" updated_at temporary_file
  updated_at="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
  temporary_file="${SUPERVISOR_STATUS_FILE}.tmp"
  mkdir -p "$(dirname "$SUPERVISOR_STATUS_FILE")"
  printf '{"state":"%s","generation":%s,"message":"%s","updated_at":"%s"}\n' \
    "$state" "$RECOVERY_GENERATION" "$message" "$updated_at" > "$temporary_file"
  mv "$temporary_file" "$SUPERVISOR_STATUS_FILE"
  if [[ -n "$RUN_DIR" ]]; then
    printf '%s | state=%s | generation=%s | %s\n' \
      "$(date '+%Y-%m-%d %H:%M:%S')" "$state" "$RECOVERY_GENERATION" "$message" >> "$RUN_DIR/supervisor.log"
  fi
}

start_model_server() {
  local output_log="$1"
  (
    trap '' INT
    exec "$SERVER" \
      --model "$MODEL" \
      --alias "$MODEL_ID" \
      --rpc "$RPC_LIST" \
      --device "$RPC_DEVICE_LIST" \
      --n-gpu-layers 99 \
      --ctx-size 512 \
      --n-predict 80 \
      --parallel 1 \
      --host 127.0.0.1 \
      --port 8080 \
      --no-webui \
      --perf \
      --verbose
  ) > >(tee "$output_log") 2>&1 &
  SERVER_PID=$!
}

wait_for_model_server() {
  for attempt in $(seq 1 900); do
    if curl -fsS http://127.0.0.1:8080/health >/dev/null 2>&1; then
      return 0
    fi
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
      wait "$SERVER_PID" || true
      return 1
    fi
    sleep 1
  done

  kill -TERM "$SERVER_PID" 2>/dev/null || true
  wait "$SERVER_PID" 2>/dev/null || true
  return 1
}

wait_for_all_phones() {
  local attempt connected
  write_supervisor_status "waiting_for_devices" "Reconnect all three phones over USB to restart the distributed session."
  for attempt in $(seq 1 "$PHONE_WAIT_SECONDS"); do
    connected="$("$ADB" devices 2>/dev/null | awk 'NR > 1 && $2 == "device" { count++ } END { print count + 0 }' || printf '0')"
    if [[ "$connected" -eq 3 ]]; then
      return 0
    fi
    if (( attempt % 10 == 0 )); then
      printf 'Recovery is waiting for all three phones over USB (%s/3 detected).\n' "$connected"
    fi
    sleep 1
  done
  return 1
}

cleanup() {
  local exit_code=$?
  trap - INT TERM EXIT
  [[ -z "$API_PID" ]] || kill "$API_PID" 2>/dev/null || true
  if [[ -n "$SERVER_PID" ]]; then
    kill -TERM "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  if [[ -n "$RUN_DIR" && -f "$ACTIVE_FILE" && -x "$ADB" ]]; then
    local worker=0 serial model version port
    while IFS='|' read -r serial model version port; do
      [[ -n "$serial" ]] || continue
      worker=$((worker + 1))
      "$ADB" -s "$serial" exec-out cat /data/local/tmp/ggml-rpc-server-phase1.log \
        </dev/null > "$RUN_DIR/worker-$worker.log" 2>/dev/null || true
    done < "$ACTIVE_FILE"
  fi
  if [[ "$STARTED_CLUSTER" -eq 1 && -f "$ACTIVE_FILE" ]]; then
    "$CLUSTER" stop || true
  fi
  write_supervisor_status "stopped" "The inference session was stopped by the operator."
  if [[ -n "$RUN_DIR" ]]; then
    write_summary "$exit_code"
  fi
  exit "$exit_code"
}

write_summary() {
  local exit_code="$1" worker count model version serial port index=0
  local model1="unknown" model2="unknown" model3="unknown"
  local count1=0 count2=0 count3=0 chats=0 tool_calls=0
  local stream_ttfc_ms="not recorded" stream_event
  [[ -f "$RUN_DIR/chat-events.jsonl" ]] && chats="$(grep -c '"event": "chat_response"' "$RUN_DIR/chat-events.jsonl" || true)"
  if [[ -f "$RUN_DIR/chat-events.jsonl" ]]; then
    tool_calls="$(grep -c '"tool_calls": [1-9]' "$RUN_DIR/chat-events.jsonl" || true)"
    stream_event="$(grep '"event": "chat_response".*"streaming": true' "$RUN_DIR/chat-events.jsonl" | tail -n 1 || true)"
    stream_ttfc_ms="$(printf '%s\n' "$stream_event" | sed -nE 's/.*"time_to_first_content_ms": ([0-9]+([.][0-9]+)?).*/\1/p')"
    [[ -n "$stream_ttfc_ms" ]] || stream_ttfc_ms="not recorded"
  fi
  if [[ -f "$ACTIVE_FILE" ]]; then
    while IFS='|' read -r serial model version port; do
      [[ -n "$serial" ]] || continue
      index=$((index + 1))
      printf -v "model$index" '%s' "$model"
      if [[ -f "$RUN_DIR/worker-$index.log" ]]; then
        printf -v "count$index" '%s' "$(grep -c 'GRAPH_RECOMPUTE' "$RUN_DIR/worker-$index.log" || true)"
      fi
    done < "$ACTIVE_FILE"
  else
    for index in 1 2 3; do
      if [[ -f "$RUN_DIR/worker-$index.log" ]]; then
        printf -v "count$index" '%s' "$(grep -c 'GRAPH_RECOMPUTE' "$RUN_DIR/worker-$index.log" || true)"
      fi
    done
  fi
  local result="INCOMPLETE"
  local speed="not recorded" prompt_s="not recorded" request_s="not recorded" weights1="not recorded" weights2="not recorded" weights3="not recorded" host="not recorded"
  if [[ -f "$RUN_DIR/coordinator.log" ]]; then
    local eval_line prompt_line total_line row
    eval_line="$(grep 'eval time =' "$RUN_DIR/coordinator.log" | tail -n 1 || true)"
    prompt_line="$(grep 'prompt eval time =' "$RUN_DIR/coordinator.log" | tail -n 1 || true)"
    total_line="$(grep 'total time =' "$RUN_DIR/coordinator.log" | tail -n 1 || true)"
    speed="$(printf '%s\n' "$eval_line" | sed -nE 's/.*\([[:space:]]*[0-9]+[,.][0-9]+ ms per token,[[:space:]]*([0-9]+[,.][0-9]+) tokens per second\).*/\1/p' | tr ',' '.')"
    prompt_s="$(printf '%s\n' "$prompt_line" | sed -nE 's/.*=[[:space:]]*([0-9]+[,.][0-9]+)[[:space:]]*ms.*/\1/p' | awk 'NF { printf "%.2f", $1 / 1000 }' | tr ',' '.')"
    request_s="$(printf '%s\n' "$total_line" | sed -nE 's/.*=[[:space:]]*([0-9]+[,.][0-9]+)[[:space:]]*ms.*/\1/p' | awk 'NF { printf "%.2f", $1 / 1000 }' | tr ',' '.')"
    [[ -n "$speed" ]] || speed="not recorded"
    [[ -n "$prompt_s" ]] || prompt_s="not recorded"
    [[ -n "$request_s" ]] || request_s="not recorded"
    row="$(grep 'common_memory_breakdown_print: |   - RPC0' "$RUN_DIR/coordinator.log" | tail -n 1 || true)"
    weights1="$(printf '%s\n' "$row" | sed -nE 's/.*\([[:space:]]*[0-9]+[[:space:]]*=[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p')"
    row="$(grep 'common_memory_breakdown_print: |   - RPC1' "$RUN_DIR/coordinator.log" | tail -n 1 || true)"
    weights2="$(printf '%s\n' "$row" | sed -nE 's/.*\([[:space:]]*[0-9]+[[:space:]]*=[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p')"
    row="$(grep 'common_memory_breakdown_print: |   - RPC2' "$RUN_DIR/coordinator.log" | tail -n 1 || true)"
    weights3="$(printf '%s\n' "$row" | sed -nE 's/.*\([[:space:]]*[0-9]+[[:space:]]*=[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p')"
    row="$(grep 'common_memory_breakdown_print: |   - Host' "$RUN_DIR/coordinator.log" | tail -n 1 || true)"
    host="$(printf '%s\n' "$row" | sed -nE 's/.*=[[:space:]]*[0-9]+[[:space:]]*\+[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p')"
    [[ -n "$weights1" ]] || weights1="not recorded"
    [[ -n "$weights2" ]] || weights2="not recorded"
    [[ -n "$weights3" ]] || weights3="not recorded"
    [[ -n "$host" ]] || host="not recorded"
  fi
  local selected_workers_pass=1 worker_count_value weight_value count_name weight_name
  for index in 1 2 3; do
    if (( index <= INFERENCE_WORKER_COUNT )); then
      count_name="count$index"
      weight_name="weights$index"
      worker_count_value="${!count_name}"
      weight_value="${!weight_name}"
      if [[ ! "$worker_count_value" =~ ^[0-9]+$ ]] || (( worker_count_value == 0 )) || [[ ! "$weight_value" =~ ^[0-9]+$ ]] || (( weight_value == 0 )); then
        selected_workers_pass=0
      fi
    fi
  done
  if [[ -f "$RUN_DIR/devices.tsv" ]]; then
    index=0
    while IFS='|' read -r serial model version port; do
      [[ -n "$serial" ]] || continue
      index=$((index + 1))
      case "$model" in SM_A075M|SM-A075M) model="Galaxy A07" ;; SM_A065M|SM-A065M) model="Galaxy A06" ;; esac
      printf -v "model$index" '%s (Android %s)' "$model" "$version"
    done < "$RUN_DIR/devices.tsv"
  fi
  if [[ "$chats" -ge 1 && "$selected_workers_pass" -eq 1 && "$host" == "0" ]]; then
    result="INFERENCE OBSERVED"
    if [[ "$INFERENCE_WORKER_COUNT" -eq 3 && "$tool_calls" -ge 1 ]]; then result="PASSED"; fi
  elif [[ "$exit_code" -eq 0 ]]; then
    result="STOPPED"
  fi
  {
    printf 'Distributed chat session summary\n'
    printf 'Result: %s\n' "$result"
    printf 'Selected profile: %s\n' "$MODEL_PROFILE"
    printf 'Model: %s\n' "$(basename "$MODEL")"
    printf 'Devices: %s; %s; %s\n' "$model1" "$model2" "$model3"
    printf 'RPC workers used for inference: %s/3 (%s)\n' "$INFERENCE_WORKER_COUNT" "$RPC_DEVICE_LIST"
    printf 'Answered messages: %s\n' "$chats"
    printf 'Supervised restarts: %s\n' "$RECOVERY_COUNT"
    printf 'Status tool calls: %s\n' "$tool_calls"
    printf 'GRAPH_RECOMPUTE operations in cumulative worker logs: %s, %s, %s\n' "$count1" "$count2" "$count3"
    printf 'Server startup until ready: %s s\n' "$MODEL_READY_SECONDS"
    printf 'Last request: prompt %s s; total %s s; generation %s tokens/s\n' "$prompt_s" "$request_s" "$speed"
    printf 'Last streamed response, runtime-observed time to first content: %s ms\n' "$stream_ttfc_ms"
    printf 'Model weights: RPC0 %s MiB, RPC1 %s MiB, RPC2 %s MiB; Mac %s MiB\n' "$weights1" "$weights2" "$weights3" "$host"
    printf 'Raw technical logs: %s\n' "$RUN_DIR"
  } > "$RUN_DIR/summary-en-us.log"
  printf '\n'
  cat "$RUN_DIR/summary-en-us.log"
}

[[ -f "$MODEL" ]] || fail "Model GGUF not found: $MODEL"
[[ -x "$CLUSTER" ]] || fail "Cluster controller not found: $CLUSTER"
[[ -x "$SERVER" ]] || fail "llama-server not built. See phase-3/README.md."
command -v curl >/dev/null 2>&1 || fail "curl is required."
[[ "$MAX_RECOVERY_ATTEMPTS" =~ ^[0-9]+$ ]] || fail "INFERENCE_MAX_RECOVERIES must be a non-negative integer."
[[ "$PHONE_WAIT_SECONDS" =~ ^[0-9]+$ && "$PHONE_WAIT_SECONDS" -gt 0 ]] || fail "INFERENCE_PHONE_WAIT_SECONDS must be a positive integer."

if [[ ! -f "$ACTIVE_FILE" ]]; then
  "$CLUSTER" start
  STARTED_CLUSTER=1
fi
RUN_DIR="$(cat "$PHASE1_DIR/.runtime/active-run")/chat-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$RUN_DIR"
cp "$ACTIVE_FILE" "$RUN_DIR/devices.tsv"
trap cleanup INT TERM EXIT
"$CLUSTER" status || fail "The three RPC workers are not healthy."
export CHAT_EVENT_LOG="$RUN_DIR/chat-events.jsonl"
export LLAMA_API_URL="http://127.0.0.1:8080/v1/chat/completions"
export LLAMA_MODEL_ID="$MODEL_ID"
export LLAMA_MODEL_NAME="$MODEL_NAME"
export CHAT_PORT=8000
export INFERENCE_SUPERVISOR_STATUS_FILE="$SUPERVISOR_STATUS_FILE"
export PYTHONPATH="$PHASE_DIR/python${PYTHONPATH:+:$PYTHONPATH}"

if [[ -f "$SUPERVISOR_STATUS_FILE" ]]; then
  previous_generation="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("generation", 0))' "$SUPERVISOR_STATUS_FILE" 2>/dev/null || printf '0')"
  [[ "$previous_generation" =~ ^[0-9]+$ ]] || previous_generation=0
  RECOVERY_GENERATION=$((previous_generation + 1))
else
  RECOVERY_GENERATION=1
fi

printf 'Starting distributed model server. Raw llama.cpp output is preserved below.\n'
printf 'Selected profile: %s — %s\n' "$MODEL_PROFILE" "$MODEL_NAME"
printf 'RPC workers selected for inference: %s/3\n' "$INFERENCE_WORKER_COUNT"
write_supervisor_status "starting" "Loading the distributed model across ${INFERENCE_WORKER_COUNT} selected RPC workers."
start_model_server "$RUN_DIR/coordinator.log"

(
  trap '' INT
    exec python3 "$PHASE_DIR/python/api/chat_server.py"
) &
API_PID=$!

printf 'Waiting for llama-server to load the model; initial load may take a few minutes.\n'
SERVER_START_EPOCH="$(date +%s)"
while true; do
  if wait_for_model_server; then
    MODEL_READY_SECONDS="$(( $(date +%s) - SERVER_START_EPOCH ))"
    write_supervisor_status "ready" "Distributed model is ready; ${INFERENCE_WORKER_COUNT}/3 selected RPC workers are used for inference and all three processes are available."
    printf 'Model server ready. Chat UI: http://127.0.0.1:8000\n'
    printf 'Supervision is active. Press Ctrl+C here to stop this session.\n'
    wait "$SERVER_PID" || true
  fi

  SERVER_PID=""
  RECOVERY_COUNT=$((RECOVERY_COUNT + 1))
  printf '\nCoordinator/RPC failure detected. Starting supervised recovery %s/%s.\n' \
    "$RECOVERY_COUNT" "$MAX_RECOVERY_ATTEMPTS"
  write_supervisor_status "degraded" "The RPC session failed. Chat is blocked while the supervisor recovers the workers and coordinator."

  if (( RECOVERY_COUNT > MAX_RECOVERY_ATTEMPTS )); then
    write_supervisor_status "failed" "Automatic recovery exceeded its limit. Check the logs and restart the session manually."
    printf 'Restart limit reached. Chat will remain blocked; see %s/supervisor.log.\n' "$RUN_DIR"
    wait "$API_PID"
    break
  fi

  if ! wait_for_all_phones; then
    write_supervisor_status "failed" "The three phones did not reconnect over USB before the timeout. Reconnect them and restart the session manually."
    printf 'Phone reconnection timed out. Chat will remain blocked.\n'
    wait "$API_PID"
    break
  fi

  write_supervisor_status "restarting" "Restarting RPC workers and reloading the distributed model."
  if [[ -f "$ACTIVE_FILE" ]]; then
    "$CLUSTER" stop || true
  fi
  STARTED_CLUSTER=0
  if ! "$CLUSTER" start; then
    write_supervisor_status "failed" "Could not restart the RPC workers. Check the Terminal and restart the session."
    wait "$API_PID"
    break
  fi
  STARTED_CLUSTER=1
  cp "$ACTIVE_FILE" "$RUN_DIR/devices-recovery-$RECOVERY_COUNT.tsv"
  RECOVERY_GENERATION=$((RECOVERY_GENERATION + 1))
  SERVER_START_EPOCH="$(date +%s)"
  write_supervisor_status "restarting" "Workers restarted; reloading the model across ${INFERENCE_WORKER_COUNT} selected RPC workers."
  start_model_server "$RUN_DIR/coordinator-recovery-$RECOVERY_COUNT.log"
  printf 'Waiting for the model to become ready after restarting the workers.\n'
done
