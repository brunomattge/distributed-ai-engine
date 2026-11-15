#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
RUNNER="$SCRIPT_DIR/run-inference.sh"

clear
cat <<'BANNER'
Distributed AI Agent — Phase 0 Experiment
=========================================

Experimental Wi-Fi path. Start ggml-rpc-server on all three phones and connect
them to the same Wi-Fi network as the Mac. This launcher does not install binaries.

BANNER

DEFAULT_CLI="$SCRIPT_DIR/build-mac/bin/llama-completion"
read -r -p "Path to llama-completion [$DEFAULT_CLI]: " LLAMA_CLI
LLAMA_CLI=${LLAMA_CLI:-$DEFAULT_CLI}
DEFAULT_MODEL="$SCRIPT_DIR/models/qwen2.5-0.5b-instruct-q4_k_m.gguf"
read -r -p "Path to the .gguf model [$DEFAULT_MODEL]: " MODEL
MODEL=${MODEL:-$DEFAULT_MODEL}
read -r -p "Phone 1 IP address (for example, the address shown in Android network settings): " PHONE1
read -r -p "Phone 2 IP address: " PHONE2
read -r -p "Phone 3 IP address: " PHONE3

if [[ -z "$LLAMA_CLI" || -z "$MODEL" || -z "$PHONE1" || -z "$PHONE2" || -z "$PHONE3" ]]; then
  echo "\nComplete every field."
  read -r -p "Press Enter to close..."
  exit 2
fi

echo "\nChecking files and all three phones..."
if [[ ! -x "$LLAMA_CLI" ]]; then
  echo "Could not find an executable llama-completion at this path: $LLAMA_CLI"
  read -r -p "Press Enter to close..."
  exit 2
fi
if [[ ! -f "$MODEL" ]]; then
  echo "Could not find a model at this path: $MODEL"
  read -r -p "Press Enter to close..."
  exit 2
fi

for IP in "$PHONE1" "$PHONE2" "$PHONE3"; do
  if ! nc -z -w 2 "$IP" 50052 >/dev/null 2>&1; then
    echo "Could not connect to $IP:50052. Check Wi-Fi and confirm that the RPC server is running."
    read -r -p "Press Enter to close..."
    exit 1
  fi
done

echo "\nAll three ports are responding. Starting inference; this may take a while."
if "$RUNNER" "$LLAMA_CLI" "$MODEL" \
  "$PHONE1:50052" "$PHONE2:50052" "$PHONE3:50052"; then
  RESULT=0
else
  RESULT=$?
fi

echo "\nExperiment finished (exit code $RESULT). Check the logs on all three phones"
echo "to confirm that all three participated in computation. An open connection alone does not prove inference."
read -r -p "Press Enter to close Terminal..."
exit "$RESULT"
