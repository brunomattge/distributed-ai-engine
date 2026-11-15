#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
MODEL="$SCRIPT_DIR/models/qwen2.5-7b-instruct-q3_k_m.gguf"
URL="https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q3_K_M.gguf"

cd "$SCRIPT_DIR"
printf '\nPhase 0 — largest candidate model test in one distributed process\n'
printf 'Model: Qwen2.5 7B Instruct Q3_K_M (3.81 GB)\n'
printf 'The download may take a few minutes. curl resumes partial downloads.\n\n'

curl --fail --location --retry 3 --continue-at - --progress-bar "$URL" --output "$MODEL"
printf '\nDownload complete. File: '
du -h "$MODEL" | awk '{print $1}'
printf '\nStarting all three workers and distributed inference...\n\n'
MODEL_PATH="$MODEL" ./run-usb-experiment.sh

printf '\nExperiment finished. Press Enter to close this session. '
read -r _
