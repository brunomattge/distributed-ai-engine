#!/usr/bin/env bash
set -euo pipefail

PHASE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCE="$PHASE_DIR/upstream-llama.cpp"
BUILD="$PHASE_DIR/build-android"
NDK="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-/opt/homebrew/share/android-ndk}}"
TOOLCHAIN="$NDK/build/cmake/android.toolchain.cmake"

[[ -d "$SOURCE" ]] || { printf 'Run ./bootstrap-llama.sh first.\n' >&2; exit 1; }
[[ -f "$TOOLCHAIN" ]] || { printf 'Android NDK toolchain not found: %s\nSet ANDROID_NDK_HOME to your NDK path.\n' "$TOOLCHAIN" >&2; exit 1; }
cmake -S "$SOURCE" -B "$BUILD" \
  -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
  -DANDROID_ABI=arm64-v8a \
  -DANDROID_PLATFORM=android-28 \
  -DBUILD_SHARED_LIBS=OFF \
  -DCMAKE_BUILD_TYPE=Release \
  -DGGML_RPC=ON \
  -DGGML_RPC_RDMA=OFF \
  -DGGML_NATIVE=OFF \
  -DGGML_OPENMP=OFF \
  -DGGML_LLAMAFILE=OFF
cmake --build "$BUILD" --target ggml-rpc-server --parallel 4
