#!/usr/bin/env bash
set -euo pipefail

PHASE2_DIR="$(cd "$(dirname "$0")" && pwd)"
PHASE1_DIR="$PHASE2_DIR/../phase-1"
PHASE0_DIR="$PHASE2_DIR/../phase-0"
CLUSTER="$PHASE1_DIR/clusterctl.sh"
ACTIVE_FILE="$PHASE1_DIR/.runtime/active.tsv"
ADB="$HOME/Library/Android/sdk/platform-tools/adb"
CLI="$PHASE0_DIR/build-mac/bin/llama-completion"
RUNNER="$PHASE0_DIR/run-inference.sh"

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

friendly_model() {
  case "$1" in
    SM_A075M|SM-A075M) printf 'Galaxy A07' ;;
    SM_A065M|SM-A065M) printf 'Galaxy A06' ;;
    *) printf '%s' "$1" ;;
  esac
}

format_seconds() {
  if [[ -z "$1" ]]; then
    printf 'not recorded'
  else
    LC_NUMERIC=C awk -v ms="$1" 'BEGIN { printf "%.2f s", ms / 1000 }'
  fi
}

[[ $# -ge 1 && $# -le 2 ]] || fail "Usage: ./infer-distributed.sh MODEL.gguf [PROMPT]"
model_path="$1"
if [[ $# -eq 2 ]]; then
  prompt="$2"
else
  prompt="Explain what a distributed system is in one sentence."
fi

[[ -f "$model_path" ]] || fail "GGUF model not found: $model_path"
[[ -x "$CLI" ]] || fail "llama-completion not found: $CLI"
[[ -x "$RUNNER" ]] || fail "Inference runner not found: $RUNNER"
[[ -x "$ADB" ]] || fail "ADB not found at $ADB."
[[ -x "$CLUSTER" ]] || fail "Cluster controller not found: $CLUSTER"
[[ -f "$ACTIVE_FILE" ]] || fail "Cluster is inactive. Start it with ../phase-1/clusterctl.sh start."

"$CLUSTER" status || fail "Cluster is unhealthy; inference cancelled."
run_dir="$(cat "$PHASE1_DIR/.runtime/active-run")"
[[ -d "$run_dir" ]] || fail "Active session has no valid log directory: $run_dir"

IFS='|' read -r serial1 model1 version1 port1 < <(sed -n '1p' "$ACTIVE_FILE")
IFS='|' read -r serial2 model2 version2 port2 < <(sed -n '2p' "$ACTIVE_FILE")
IFS='|' read -r serial3 model3 version3 port3 < <(sed -n '3p' "$ACTIVE_FILE")
[[ -n "$serial1" && -n "$serial2" && -n "$serial3" ]] || fail "Active session does not contain three workers."
endpoint1="127.0.0.1:$port1"
endpoint2="127.0.0.1:$port2"
endpoint3="127.0.0.1:$port3"

inference_dir="$run_dir/inference-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$inference_dir"
printf 'Starting inference; raw technical output is preserved.\n'
printf 'Model: %s\nPrompt: %s\n' "$(basename "$model_path")" "$prompt"
printf '%s | inference started | model=%s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$(basename "$model_path")" >> "$run_dir/status.log"

inference_rc=0
if "$RUNNER" "$CLI" "$model_path" "$endpoint1" "$endpoint2" "$endpoint3" "$prompt" \
  > >(tee "$inference_dir/coordinator.stdout.log") \
  2> >(tee "$inference_dir/coordinator.debug.log" >&2); then
  inference_rc=0
else
  inference_rc=$?
fi

"$ADB" -s "$serial1" exec-out cat /data/local/tmp/ggml-rpc-server-phase1.log </dev/null > "$inference_dir/worker-1.log" 2>/dev/null || true
"$ADB" -s "$serial2" exec-out cat /data/local/tmp/ggml-rpc-server-phase1.log </dev/null > "$inference_dir/worker-2.log" 2>/dev/null || true
"$ADB" -s "$serial3" exec-out cat /data/local/tmp/ggml-rpc-server-phase1.log </dev/null > "$inference_dir/worker-3.log" 2>/dev/null || true

count1=$(grep -c 'GRAPH_RECOMPUTE' "$inference_dir/worker-1.log" || true)
count2=$(grep -c 'GRAPH_RECOMPUTE' "$inference_dir/worker-2.log" || true)
count3=$(grep -c 'GRAPH_RECOMPUTE' "$inference_dir/worker-3.log" || true)
all_workers_computed=1
[[ "$count1" -gt 0 && "$count2" -gt 0 && "$count3" -gt 0 ]] || all_workers_computed=0

response_nonempty=0
if grep -v '^\[' "$inference_dir/coordinator.stdout.log" | grep -q '[^[:space:]]'; then response_nonempty=1; fi

load_line=$(grep -E 'common_perf_print: *load time =' "$inference_dir/coordinator.debug.log" | tail -n 1 || true)
prompt_line=$(grep -E 'common_perf_print: *prompt eval time =' "$inference_dir/coordinator.debug.log" | tail -n 1 || true)
generation_line=$(grep -E 'common_perf_print: *eval time =' "$inference_dir/coordinator.debug.log" | tail -n 1 || true)
total_line=$(grep -E 'common_perf_print: *total time =' "$inference_dir/coordinator.debug.log" | tail -n 1 || true)
load_ms=$(printf '%s\n' "$load_line" | sed -nE 's/.*=[[:space:]]*([0-9]+[,.][0-9]+)[[:space:]]*ms.*/\1/p' | tr ',' '.')
prompt_ms=$(printf '%s\n' "$prompt_line" | sed -nE 's/.*=[[:space:]]*([0-9]+[,.][0-9]+)[[:space:]]*ms.*/\1/p' | tr ',' '.')
generation_ms=$(printf '%s\n' "$generation_line" | sed -nE 's/.*=[[:space:]]*([0-9]+[,.][0-9]+)[[:space:]]*ms.*/\1/p' | tr ',' '.')
total_ms=$(printf '%s\n' "$total_line" | sed -nE 's/.*=[[:space:]]*([0-9]+[,.][0-9]+)[[:space:]]*ms.*/\1/p' | tr ',' '.')
generation_tps=$(printf '%s\n' "$generation_line" | sed -nE 's/.*\([[:space:]]*[0-9]+[,.][0-9]+ ms per token,[[:space:]]*([0-9]+[,.][0-9]+) tokens per second\).*/\1/p' | tr ',' '.')

row1=$(grep 'common_memory_breakdown_print: |   - RPC0' "$inference_dir/coordinator.debug.log" | tail -n 1 || true)
row2=$(grep 'common_memory_breakdown_print: |   - RPC1' "$inference_dir/coordinator.debug.log" | tail -n 1 || true)
row3=$(grep 'common_memory_breakdown_print: |   - RPC2' "$inference_dir/coordinator.debug.log" | tail -n 1 || true)
host_line=$(grep 'common_memory_breakdown_print: |   - Host' "$inference_dir/coordinator.debug.log" | tail -n 1 || true)
weights1=$(printf '%s\n' "$row1" | sed -nE 's/.*\([[:space:]]*[0-9]+[[:space:]]*=[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p')
weights2=$(printf '%s\n' "$row2" | sed -nE 's/.*\([[:space:]]*[0-9]+[[:space:]]*=[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p')
weights3=$(printf '%s\n' "$row3" | sed -nE 's/.*\([[:space:]]*[0-9]+[[:space:]]*=[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p')
host_mib=$(printf '%s\n' "$host_line" | sed -nE 's/.*=[[:space:]]*[0-9]+[[:space:]]*\+[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p')

weights1_value="$weights1"
weights2_value="$weights2"
weights3_value="$weights3"
host_value="$host_mib"
tps_value="$generation_tps"
[[ -n "$weights1_value" ]] || weights1_value="not recorded"
[[ -n "$weights2_value" ]] || weights2_value="not recorded"
[[ -n "$weights3_value" ]] || weights3_value="not recorded"
[[ -n "$host_value" ]] || host_value="not recorded"
[[ -n "$tps_value" ]] || tps_value="not recorded"
tps_value="$(printf '%s' "$tps_value" | tr '.' ',')"

all_layers_on_rpc=1
if [[ ! "$weights1" =~ ^[0-9]+$ || "$weights1" -le 0 || ! "$weights2" =~ ^[0-9]+$ || "$weights2" -le 0 || ! "$weights3" =~ ^[0-9]+$ || "$weights3" -le 0 || "$host_mib" != "0" ]]; then
  all_layers_on_rpc=0
fi

test_passed=0
if [[ "$inference_rc" -eq 0 && "$response_nonempty" -eq 1 && "$all_workers_computed" -eq 1 && "$all_layers_on_rpc" -eq 1 && -n "$total_line" ]]; then
  test_passed=1
fi

summary="$inference_dir/summary-en-us.log"
{
  printf 'Distributed inference summary\n'
  if [[ "$test_passed" -eq 1 ]]; then printf 'Result: PASSED\n'; else printf 'Result: FAILED\n'; fi
  printf 'Model: %s\n' "$(basename "$model_path")"
  printf 'Transport: USB via ADB; one model across three workers\n'
  if [[ "$response_nonempty" -eq 1 ]]; then printf 'Response generated: yes\n'; else printf 'Response generated: not confirmed\n'; fi
  printf 'Configuration: context 512; generation limit 32 tokens\n'
  printf '\nDevices:\n'
  printf '  Worker 1: %s (%s), Android %s\n' "$(friendly_model "$model1")" "$model1" "$version1"
  printf '  Worker 2: %s (%s), Android %s\n' "$(friendly_model "$model2")" "$model2" "$version2"
  printf '  Worker 3: %s (%s), Android %s\n' "$(friendly_model "$model3")" "$model3" "$version3"
  printf '\nAllocated model weights:\n'
  printf '  Worker 1 (RPC0): %s MiB\n' "$weights1_value"
  printf '  Worker 2 (RPC1): %s MiB\n' "$weights2_value"
  printf '  Worker 3 (RPC2): %s MiB\n' "$weights3_value"
  printf '  Mac (Host): %s MiB\n' "$host_value"
  printf '\nCompute operations (GRAPH_RECOMPUTE):\n'
  printf '  Worker 1: %s\n' "$count1"
  printf '  Worker 2: %s\n' "$count2"
  printf '  Worker 3: %s\n' "$count3"
  printf '\nTimings reported by llama.cpp:\n'
  printf '  Model load: %s\n' "$(format_seconds "$load_ms")"
  printf '  Prompt processing: %s\n' "$(format_seconds "$prompt_ms")"
  printf '  Generation: %s (%s tokens/s)\n' "$(format_seconds "$generation_ms")" "$tps_value"
  printf '  Total request after model load: %s\n' "$(format_seconds "$total_ms")"
  printf '\nRaw technical logs: %s\n' "$inference_dir"
} > "$summary"

printf '\nFinal summary saved to: %s\n\n' "$summary"
cat "$summary"
printf '%s | inference ended | result=%s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$([[ "$test_passed" -eq 1 ]] && printf 'PASSED' || printf 'FAILED')" >> "$run_dir/status.log"
[[ "$test_passed" -eq 1 ]] || exit 1
