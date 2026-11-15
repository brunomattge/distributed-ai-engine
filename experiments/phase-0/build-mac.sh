#!/usr/bin/env bash
set -euo pipefail

PHASE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE="$PHASE_DIR/upstream-llama.cpp"
BUILD="$PHASE_DIR/build-mac"

[[ -d "$SOURCE" ]] || { printf 'Run ./bootstrap-llama.sh first.\n' >&2; exit 1; }
cmake -S "$SOURCE" -B "$BUILD" -DGGML_RPC=ON -DLLAMA_BUILD_SERVER=ON -DCMAKE_BUILD_TYPE=Release
cmake --build "$BUILD" --target llama-server llama-completion ggml-rpc-server --parallel 4
