#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 /path/to/llama-completion /path/to/model.gguf PHONE1:PORT PHONE2:PORT PHONE3:PORT [PROMPT]" >&2
}

if [[ $# -lt 5 || $# -gt 6 ]]; then
  usage
  exit 2
fi

cli=$1
model=$2
endpoint1=$3
endpoint2=$4
endpoint3=$5
prompt="${6:-Explain what a distributed system is in one sentence.}"
endpoints=("$endpoint1" "$endpoint2" "$endpoint3")

if [[ ! -x "$cli" ]]; then
  echo "llama-completion is not executable: $cli" >&2
  exit 2
fi
if [[ ! -f "$model" ]]; then
  echo "GGUF model not found: $model" >&2
  exit 2
fi

unique_endpoints=$(printf '%s\n' "${endpoints[@]}" | sort -u | wc -l | tr -d ' ')
if [[ "$unique_endpoints" != 3 ]]; then
  echo "Expected three distinct PHONE:PORT endpoints." >&2
  exit 2
fi
for endpoint in "${endpoints[@]}"; do
  if [[ ! "$endpoint" =~ ^[^:,[:space:]]+:[0-9]{1,5}$ ]]; then
    echo "Invalid endpoint: $endpoint (expected PHONE:PORT)" >&2
    exit 2
  fi
done

rpc_list=$(IFS=,; echo "${endpoints[*]}")
echo "Starting one-model RPC experiment across: $rpc_list" >&2
echo "Review logs to prove all three workers computed and local Mac devices were excluded." >&2

# llama.cpp RPC is experimental. Explicitly list only remote devices so a local
# Mac backend cannot silently participate in model-layer execution.
GGML_RPC_DEBUG=2 "$cli" \
  --model "$model" \
  --rpc "$rpc_list" \
  --device RPC0,RPC1,RPC2 \
  --n-gpu-layers 99 \
  --ctx-size 512 \
  --predict 32 \
  --perf \
  --single-turn \
  --no-display-prompt \
  --prompt "$prompt" \
  --verbose
