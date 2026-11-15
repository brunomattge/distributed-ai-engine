#!/usr/bin/env bash
set -euo pipefail

PHASE2_DIR="$(cd "$(dirname "$0")" && pwd)"
PHASE1_DIR="$PHASE2_DIR/../phase-1"
PHASE0_DIR="$PHASE2_DIR/../phase-0"
CLUSTER="$PHASE1_DIR/clusterctl.sh"
ACTIVE_FILE="$PHASE1_DIR/.runtime/active.tsv"
ADB="${ANDROID_ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
CLI="$PHASE0_DIR/build-mac/bin/llama-completion"

usage() {
  cat <<'TEXT'
Usage: ./benchmark-workers.sh MODEL.gguf [PROMPT]

Compare one distributed model inference across 1, 2, and 3 workers.
Requires the three-worker USB cluster started by ../phase-1/clusterctl.sh.
Unused workers stay running but are excluded from that run’s RPC list.
TEXT
}

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

format_seconds() {
  local milliseconds="$1"
  if [[ -z "$milliseconds" ]]; then
    printf 'not recorded'
  else
    LC_NUMERIC=C awk -v ms="$milliseconds" 'BEGIN { printf "%.2f s", ms / 1000 }'
  fi
}

metric_ms() {
  printf '%s\n' "$1" | sed -nE 's/.*=[[:space:]]*([0-9]+[,.][0-9]+)[[:space:]]*ms.*/\1/p' | tr ',' '.'
}

weight_mib() {
  printf '%s\n' "$1" | sed -nE 's/.*\([[:space:]]*[0-9]+[[:space:]]*=[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p'
}

graph_count() {
  local count
  count="$(grep -c 'GRAPH_RECOMPUTE' "$1" 2>/dev/null || true)"
  printf '%s' "${count:-0}"
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi
[[ $# -ge 1 && $# -le 2 ]] || { usage >&2; exit 2; }

model_path="$1"
prompt="${2:-Explain what a distributed system is in one sentence.}"
requested_counts="${BENCHMARK_WORKER_COUNTS:-1,2,3}"
IFS=, read -r -a worker_counts <<< "$requested_counts"
[[ -f "$model_path" ]] || fail "GGUF model not found: $model_path"
[[ -x "$CLI" ]] || fail "llama-completion not found: $CLI"
[[ -x "$ADB" ]] || fail "ADB not found: $ADB"
[[ -x "$CLUSTER" ]] || fail "Cluster controller not found: $CLUSTER"
[[ -f "$ACTIVE_FILE" ]] || fail "Cluster is inactive. Start it with ../phase-1/clusterctl.sh start."
[[ ${#worker_counts[@]} -gt 0 ]] || fail "BENCHMARK_WORKER_COUNTS must list 1, 2, or 3."
for worker_count in "${worker_counts[@]}"; do
  [[ "$worker_count" =~ ^[123]$ ]] || fail "Invalid worker count in BENCHMARK_WORKER_COUNTS: $worker_count"
done

"$CLUSTER" status || fail "Cluster is unhealthy; benchmark cancelled."
run_dir="$(cat "$PHASE1_DIR/.runtime/active-run")"
[[ -d "$run_dir" ]] || fail "Active session has no valid log directory: $run_dir"

serials=()
models=()
versions=()
ports=()
while IFS='|' read -r serial model version port; do
  [[ -n "$serial" ]] || continue
  serials+=("$serial")
  models+=("$model")
  versions+=("$version")
  ports+=("$port")
done < "$ACTIVE_FILE"
[[ ${#serials[@]} -eq 3 ]] || fail "The active session must contain exactly three workers."

benchmark_dir="$run_dir/benchmark-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$benchmark_dir"
summary="$benchmark_dir/summary-en-us.log"
{
  printf 'Distributed inference comparison benchmark\n'
  printf 'Model: %s\n' "$(basename "$model_path")"
  printf 'Prompt: %s\n' "$prompt"
  printf 'Shared settings: context 512; generation limit 32 tokens; USB via ADB\n'
  printf 'Devices (RPC order): %s, %s, %s\n' "${models[0]}" "${models[1]}" "${models[2]}"
  printf 'Started at: %s\n\n' "$(date '+%Y-%m-%d %H:%M:%S')"
} > "$summary"

for position in "${!worker_counts[@]}"; do
  worker_count="${worker_counts[$position]}"
  result_dir="$benchmark_dir/workers-$worker_count"
  mkdir -p "$result_dir"

  rpc_endpoints=()
  rpc_devices=()
  for ((index = 0; index < worker_count; index++)); do
    rpc_endpoints+=("127.0.0.1:${ports[$index]}")
    rpc_devices+=("RPC$index")
  done
  rpc_list="$(IFS=,; printf '%s' "${rpc_endpoints[*]}")"
  device_list="$(IFS=,; printf '%s' "${rpc_devices[*]}")"

  before_counts=()
  for index in 0 1 2; do
    worker_log="$result_dir/worker-$((index + 1))-before.log"
    "$ADB" -s "${serials[$index]}" exec-out cat /data/local/tmp/ggml-rpc-server-phase1.log > "$worker_log" 2>/dev/null || true
    before_counts[$index]="$(graph_count "$worker_log")"
  done

  printf '\n========== Benchmark: %s worker(s) ==========\n' "$worker_count"
  printf 'Model: %s\nRPC workers: %s\n' "$(basename "$model_path")" "$rpc_list"
  started_at="$(date +%s)"
  inference_rc=0
  if GGML_RPC_DEBUG=2 "$CLI" \
    --model "$model_path" \
    --rpc "$rpc_list" \
    --device "$device_list" \
    --n-gpu-layers 99 \
    --ctx-size 512 \
    --predict 32 \
    --perf \
    --single-turn \
    --no-display-prompt \
    --prompt "$prompt" \
    --verbose \
    > >(tee "$result_dir/coordinator.stdout.log") \
    2> >(tee "$result_dir/coordinator.debug.log" >&2); then
    inference_rc=0
  else
    inference_rc=$?
  fi
  finished_at="$(date +%s)"
  wall_seconds=$((finished_at - started_at))

  delta_counts=()
  for index in 0 1 2; do
    worker_log="$result_dir/worker-$((index + 1))-after.log"
    "$ADB" -s "${serials[$index]}" exec-out cat /data/local/tmp/ggml-rpc-server-phase1.log > "$worker_log" 2>/dev/null || true
    after_count="$(graph_count "$worker_log")"
    delta_counts[$index]=$((after_count - before_counts[$index]))
  done

  response_nonempty=0
  if [[ -f "$result_dir/coordinator.stdout.log" ]] && grep -v '^\[' "$result_dir/coordinator.stdout.log" | grep -q '[^[:space:]]'; then
    response_nonempty=1
  fi

  load_line="$(grep -E 'common_perf_print: *load time =' "$result_dir/coordinator.debug.log" | tail -n 1 || true)"
  prompt_line="$(grep -E 'common_perf_print: *prompt eval time =' "$result_dir/coordinator.debug.log" | tail -n 1 || true)"
  generation_line="$(grep -E 'common_perf_print: *eval time =' "$result_dir/coordinator.debug.log" | tail -n 1 || true)"
  total_line="$(grep -E 'common_perf_print: *total time =' "$result_dir/coordinator.debug.log" | tail -n 1 || true)"
  load_ms="$(metric_ms "$load_line")"
  prompt_ms="$(metric_ms "$prompt_line")"
  generation_ms="$(metric_ms "$generation_line")"
  total_ms="$(metric_ms "$total_line")"
  generation_tps="$(printf '%s\n' "$generation_line" | sed -nE 's/.*\([[:space:]]*[0-9]+[,.][0-9]+ ms per token,[[:space:]]*([0-9]+[,.][0-9]+) tokens per second\).*/\1/p' | tr ',' '.')"

  host_line="$(grep 'common_memory_breakdown_print: |   - Host' "$result_dir/coordinator.debug.log" | tail -n 1 || true)"
  host_mib="$(printf '%s\n' "$host_line" | sed -nE 's/.*=[[:space:]]*[0-9]+[[:space:]]*\+[[:space:]]*([0-9]+)[[:space:]]*\+.*/\1/p')"
  [[ -n "$host_mib" ]] || host_mib="not recorded"

  allocations=()
  workers_computed=1
  allocations_valid=1
  for index in 0 1 2; do
    allocation="not used"
    if (( index < worker_count )); then
      rpc_line="$(grep "common_memory_breakdown_print: |   - RPC$index" "$result_dir/coordinator.debug.log" | tail -n 1 || true)"
      allocation="$(weight_mib "$rpc_line")"
      if [[ ! "$allocation" =~ ^[0-9]+$ ]] || (( allocation <= 0 )); then
        allocations_valid=0
        allocation="not recorded"
      else
        allocation="$allocation MiB"
      fi
      if (( delta_counts[index] <= 0 )); then workers_computed=0; fi
    fi
    allocations[$index]="$allocation"
  done
  [[ "$host_mib" == "0" ]] || allocations_valid=0

  passed=0
  if (( inference_rc == 0 && response_nonempty == 1 && workers_computed == 1 && allocations_valid == 1 )) && [[ -n "$total_line" ]]; then
    passed=1
  fi

  printf '\nRun summary for %s worker(s)\n' "$worker_count" | tee -a "$summary"
  if (( passed == 1 )); then
    printf 'Result: PASSED\n' | tee -a "$summary"
  else
    printf 'Result: FAILED or unverified (exit code %s)\n' "$inference_rc" | tee -a "$summary"
  fi
  printf 'Non-empty response: %s\n' "$([[ "$response_nonempty" -eq 1 ]] && printf 'yes' || printf 'no')" | tee -a "$summary"
  for index in 0 1 2; do
    printf 'Worker %s (%s): %s GRAPH_RECOMPUTE; model weights %s\n' \
      "$((index + 1))" "${models[$index]}" "${delta_counts[$index]}" "${allocations[$index]}" | tee -a "$summary"
  done
  printf 'Mac (Host): %s MiB of model weights\n' "$host_mib" | tee -a "$summary"
  printf 'Model load: %s | prompt: %s | generation: %s (%s tokens/s) | wall time: %s s\n' \
    "$(format_seconds "$load_ms")" \
    "$(format_seconds "$prompt_ms")" \
    "$(format_seconds "$generation_ms")" \
    "${generation_tps:-not recorded}" \
    "$wall_seconds" | tee -a "$summary"
  printf 'Raw technical logs: %s\n\n' "$result_dir" | tee -a "$summary"

  if (( position < ${#worker_counts[@]} - 1 )); then
    printf 'Restarting and validating all three RPC workers before the next configuration.\n'
    "$CLUSTER" stop
    "$CLUSTER" start
    "$CLUSTER" status
  fi
done

printf 'Finished at: %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" >> "$summary"
printf '\nComparison summary saved to: %s\n' "$summary"
cat "$summary"
