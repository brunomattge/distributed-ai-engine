#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ADB="${ANDROID_ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
ANDROID_BIN="$SCRIPT_DIR/build-android/bin/ggml-rpc-server"
CLI="$SCRIPT_DIR/build-mac/bin/llama-completion"
MODEL="${MODEL_PATH:-$SCRIPT_DIR/models/qwen2.5-0.5b-instruct-q4_k_m.gguf}"
RUNNER="$SCRIPT_DIR/run-inference.sh"
PORTS=(50052 50053 50054)
SERIALS=()
PHONE_MODELS=()
ANDROID_VERSIONS=()
RUN_DIR=""
INFERENCE_RC=1

say() { printf '\n\033[1;36m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

cleanup() {
  local code=$?
  trap - EXIT
  if [[ -n "$ADB" && -x "$ADB" ]]; then
    for port in "${PORTS[@]}"; do
      "$ADB" forward --remove "tcp:$port" >/dev/null 2>&1 || true
    done
    for serial in "${SERIALS[@]}"; do
      "$ADB" -s "$serial" shell 'pid="$(pidof ggml-rpc-server)"; if [ -n "$pid" ]; then kill $pid; fi' >/dev/null 2>&1 || true
    done
  fi
  if [[ -n "$RUN_DIR" ]]; then
    printf '\nLogs saved to: %s\n' "$RUN_DIR"
  fi
  exit "$code"
}
trap cleanup EXIT

[[ -x "$ADB" ]] || fail "ADB not found at $ADB. Install Android SDK Platform-Tools or set ANDROID_ADB."
[[ -x "$ANDROID_BIN" ]] || fail "Android binary not found: $ANDROID_BIN"
[[ -x "$CLI" ]] || fail "llama-completion not found: $CLI"
[[ -f "$MODEL" ]] || fail "GGUF model not found: $MODEL"
[[ -x "$RUNNER" ]] || fail "Runner is not executable: $RUNNER"
command -v nc >/dev/null || fail "The nc command is unavailable on this Mac."

say "Phase 0: distributed inference over USB"
cat <<'TEXT'
The Mac is the coordinator. The GGUF model stays on the Mac; model layer weights and
compute operations are sent to three Android workers over ADB/USB.
USB creates a local TCP tunnel, so this experiment does not depend on Wi-Fi.
TEXT

say "1/4 — Checking ADB-authorized devices"
while IFS= read -r serial; do
  [[ -n "$serial" ]] && SERIALS+=("$serial")
done < <("$ADB" devices | awk 'NR > 1 && $2 == "device" { print $1 }')

[[ ${#SERIALS[@]} -eq 3 ]] || {
  "$ADB" devices -l
  fail "Exactly 3 authorized phones are required; found ${#SERIALS[@]}. Unlock each phone and accept “Allow USB debugging.”"
}

for i in 0 1 2; do
  serial="${SERIALS[$i]}"
  model_name=$("$ADB" -s "$serial" shell getprop ro.product.model | tr -d '\r')
  android_version=$("$ADB" -s "$serial" shell getprop ro.build.version.release | tr -d '\r')
  PHONE_MODELS+=("$model_name")
  ANDROID_VERSIONS+=("$android_version")
  printf 'Worker %s = %s — %s (Android %s)\n' "$((i + 1))" "$serial" "$model_name" "$android_version"
done

RUN_DIR="$SCRIPT_DIR/results/usb-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$RUN_DIR"

say "2/4 — Installing and starting one worker on each phone"
printf 'Each process opens port 50052 on its phone.\n'
for serial in "${SERIALS[@]}"; do
  "$ADB" -s "$serial" push "$ANDROID_BIN" /data/local/tmp/ggml-rpc-server >/dev/null
  "$ADB" -s "$serial" shell chmod 755 /data/local/tmp/ggml-rpc-server
  "$ADB" -s "$serial" shell 'pid="$(pidof ggml-rpc-server)"; if [ -n "$pid" ]; then kill $pid; fi'
  "$ADB" -s "$serial" shell 'GGML_RPC_DEBUG=2 nohup /data/local/tmp/ggml-rpc-server -H 127.0.0.1 -p 50052 > /data/local/tmp/ggml-rpc-server.log 2>&1 < /dev/null &'
done

say "3/4 — Creating three USB tunnels"
for i in 0 1 2; do
  serial="${SERIALS[$i]}"
  port="${PORTS[$i]}"
  "$ADB" forward --remove "tcp:$port" >/dev/null 2>&1 || true
  "$ADB" -s "$serial" forward "tcp:$port" tcp:50052 >/dev/null
  if ! nc -z -w 3 127.0.0.1 "$port" >/dev/null 2>&1; then
    "$ADB" -s "$serial" shell 'cat /data/local/tmp/ggml-rpc-server.log' > "$RUN_DIR/worker-$((i + 1)).log" || true
    fail "Worker $((i + 1)) did not respond on USB port $port. See $RUN_DIR/worker-$((i + 1)).log"
  fi
  printf '127.0.0.1:%s → Worker %s ✓\n' "$port" "$((i + 1))"
done

say "4/4 — Loading one model and generating a response"
cat <<'TEXT'
llama.cpp uses RPC0/RPC1/RPC2 to store model layers.
Concise compute progress appears below, and the model response
streams live. Full details are saved in the experiment files.
Initial model loading may take a few minutes.
TEXT

if "$RUNNER" "$CLI" "$MODEL" \
  "127.0.0.1:${PORTS[0]}" "127.0.0.1:${PORTS[1]}" "127.0.0.1:${PORTS[2]}" \
  > >(tee "$RUN_DIR/coordinator.stdout.log") \
  2> >(tee "$RUN_DIR/coordinator.debug.log" | awk '
    /Starting one-model RPC experiment/ {
      print "[Mac] One model is distributed across all three RPC workers"; fflush(); next
    }
    /llama backend init/ {
      print "[Mac] Starting the llama.cpp coordinator"; fflush(); next
    }
    /llama_model_loader: loaded meta data/ {
      print "[Model] GGUF loaded; metadata and tensors are ready"; fflush(); next
    }
    /llama_prepare_model_devices: using device RPC[012]/ {
      print "[Worker] " $0; fflush(); next
    }
    /sched_reserve: RPC0\[/ && /compute buffer size/ {
      print "[Scheduler] " $0; fflush(); next
    }
    /common_memory_breakdown_print: \| memory breakdown/ {
      print "[Memory] " $0; fflush(); next
    }
    /common_memory_breakdown_print: \|   - (RPC[012]|Host)/ {
      print "[Memory] " $0; fflush(); next
    }
    /ggml_backend_rpc_buffer_set_tensor/ {
      if ($0 ~ /127\.0\.0\.1:50052/) worker = 1
      else if ($0 ~ /127\.0\.0\.1:50053/) worker = 2
      else if ($0 ~ /127\.0\.0\.1:50054/) worker = 3
    }
    /\[work\] GRAPH_COMPUTE/ && worker > 0 && !started[worker] {
      started[worker] = 1
      printf "[Compute] Worker %d computed its part of the prompt\n", worker
      fflush()
    }
    /common_perf_print: (load time|prompt eval time|eval time|total time)/ {
      print "[Tempo] " $0; fflush(); next
    }
    /\b(error|failed|fatal)\b/ {
      print "[Diagnostic] " $0; fflush(); next
    }
  '); then
  INFERENCE_RC=0
else
  INFERENCE_RC=$?
fi
sleep 1

for i in 0 1 2; do
  serial="${SERIALS[$i]}"
  "$ADB" -s "$serial" exec-out cat /data/local/tmp/ggml-rpc-server.log > "$RUN_DIR/worker-$((i + 1)).log" || true
done

say "Result"
printf 'Inference exit code: %s\n' "$INFERENCE_RC"
printf '\nThe response streamed live and is in coordinator.stdout.log.\n'

printf '\nModel layers on the phones (the model column should be greater than zero):\n'
grep -E 'memory breakdown|RPC[012] \(|Host[[:space:]]*\|' "$RUN_DIR/coordinator.stdout.log" "$RUN_DIR/coordinator.debug.log" | tail -n 5 || true

printf '\nGraph operations on each phone (GRAPH_RECOMPUTE):\n'
all_workers_computed=1
GRAPH_COUNTS=()
for i in 0 1 2; do
  worker_log="$RUN_DIR/worker-$((i + 1)).log"
  count=$(grep -c 'GRAPH_RECOMPUTE' "$worker_log" || true)
  GRAPH_COUNTS+=("$count")
  printf 'Worker %s: %s\n' "$((i + 1))" "$count"
  [[ "$count" -gt 0 ]] || all_workers_computed=0
done

printf '\nTimings reported by llama.cpp:\n'
grep -E 'load time|prompt eval time|eval time|total time' "$RUN_DIR/coordinator.stdout.log" "$RUN_DIR/coordinator.debug.log" | tail -n 4 || true

response_nonempty=0
if grep -v '^\[' "$RUN_DIR/coordinator.stdout.log" | grep -q '[^[:space:]]'; then
  response_nonempty=1
fi

total_time_line=$(grep -E 'common_perf_print:.*total time' "$RUN_DIR/coordinator.debug.log" | tail -n 1 || true)
has_total_time=0
[[ -n "$total_time_line" ]] && has_total_time=1

test_passed=0
if [[ "$INFERENCE_RC" -eq 0 && "$all_workers_computed" -eq 1 && "$response_nonempty" -eq 1 && "$has_total_time" -eq 1 ]]; then
  test_passed=1
fi

metric_line() {
  grep -E "$1" "$RUN_DIR/coordinator.debug.log" | tail -n 1 || true
}

metric_ms() {
  printf '%s\n' "$1" | sed -nE 's/.*=[[:space:]]*([0-9]+[,.][0-9]+)[[:space:]]*ms.*/\1/p' | tr ',' '.'
}

ms_to_seconds() {
  LC_NUMERIC=C awk -v ms="$1" 'BEGIN { if (ms == "") print "not recorded"; else printf "%.2f s", ms / 1000 }'
}

load_ms=$(metric_ms "common_perf_print: *load time =")
prompt_ms=$(metric_ms "common_perf_print: *prompt eval time =")
generation_ms=$(metric_ms "common_perf_print: *eval time =")
total_ms=$(metric_ms "common_perf_print: *total time =")
generation_tps=$(printf '%s\n' "$(metric_line 'common_perf_print: *eval time =')" | sed -nE 's/.*\([[:space:]]*[0-9]+[,.][0-9]+ ms per token,[[:space:]]*([0-9]+[,.][0-9]+) tokens per second\).*/\1/p' | tr ',' '.')

rpc_model_mib() {
  local rpc="$1"
  local line
  line=$(grep "common_memory_breakdown_print: |   - $rpc" "$RUN_DIR/coordinator.debug.log" | tail -n 1 || true)
  printf '%s\n' "$line" | sed -nE 's/.*\([[:space:]]*[0-9]+[[:space:]]*=[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p'
}

host_model_mib() {
  local line
  line=$(grep 'common_memory_breakdown_print: |   - Host' "$RUN_DIR/coordinator.debug.log" | tail -n 1 || true)
  printf '%s\n' "$line" | sed -nE 's/.*=[[:space:]]*[0-9]+[[:space:]]*\+[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p'
}

SUMMARY="$RUN_DIR/summary-en-us.log"
{
  printf 'Phase 0 experiment summary\n'
  printf 'Result: %s\n' "$([[ "$test_passed" -eq 1 ]] && printf 'PASSED' || printf 'FAILED')"
  printf 'Model: %s\n' "$(basename "$MODEL")"
  printf 'Transport: USB via ADB, three concurrent workers\n'
  printf 'Model response: %s\n' "$([[ "$response_nonempty" -eq 1 ]] && printf 'generated (non-empty)' || printf 'not confirmed')"
  printf '\nDevices:\n'
  for i in 0 1 2; do
    case "${PHONE_MODELS[$i]}" in
      SM_A075M|SM-A075M) friendly_model='Galaxy A07' ;;
      SM_A065M|SM-A065M) friendly_model='Galaxy A06' ;;
      *) friendly_model="${PHONE_MODELS[$i]}" ;;
    esac
    printf '  Worker %s: %s (%s), Android %s\n' "$((i + 1))" "$friendly_model" "${PHONE_MODELS[$i]}" "${ANDROID_VERSIONS[$i]}"
  done
  printf '\nAllocated model weights:\n'
  for i in 0 1 2; do
    rpc="RPC$i"
    model_mib=$(rpc_model_mib "$rpc")
    printf '  Worker %s (%s): %s MiB\n' "$((i + 1))" "$rpc" "${model_mib:-not recorded}"
  done
  host_mib=$(host_model_mib)
  printf '  Mac (Host): %s MiB\n' "${host_mib:-not recorded}"
  printf '\nRecorded compute operations (GRAPH_RECOMPUTE):\n'
  for i in 0 1 2; do
    printf '  Worker %s: %s\n' "$((i + 1))" "${GRAPH_COUNTS[$i]}"
  done
  printf '\nTimings reported by llama.cpp:\n'
  printf '  Model load: %s\n' "$(ms_to_seconds "$load_ms")"
  printf '  Prompt processing: %s\n' "$(ms_to_seconds "$prompt_ms")"
  printf '  Generation: %s (%s tokens/s)\n' "$(ms_to_seconds "$generation_ms")" "${generation_tps:-not recorded}"
  printf '  Total request after model load: %s\n' "$(ms_to_seconds "$total_ms")"
  printf '\nFull logs: %s\n' "$RUN_DIR"
  printf 'Note: these measurements use USB and do not represent Wi-Fi performance.\n'
} > "$SUMMARY"

printf '\nEnglish summary saved to: %s\n\n' "$SUMMARY"
cat "$SUMMARY"

if [[ "$test_passed" -ne 1 ]]; then
  printf '\nThe experiment did not meet all acceptance criteria. Review the logs in %s\n' "$RUN_DIR" >&2
  exit 1
fi

cat <<'TEXT'

How to interpret the results:
- RPC0/RPC1/RPC2 and the weights in the “model” column show that model layers
  were distributed to the phones.
- GRAPH_RECOMPUTE in each log shows that the phones computed parts of the graph.
- The “Host” row should show 0 MiB in “model”; the Mac coordinates but does not store
  model layers in this run.
- These measurements use USB and do not represent Wi-Fi performance.
TEXT

exit 0
