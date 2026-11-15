#!/usr/bin/env bash
set -euo pipefail

PHASE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$PHASE_DIR/upstream-llama.cpp"
PINNED_COMMIT="9183a552e5fdcbdb5243f187f3a63204eca7dba9"

if [[ -e "$DEST" ]]; then
  if [[ ! -d "$DEST/.git" ]]; then
    printf 'Refusing to replace existing non-Git directory: %s\n' "$DEST" >&2
    exit 1
  fi
  current="$(git -C "$DEST" rev-parse HEAD)"
  if [[ "$current" != "$PINNED_COMMIT" ]]; then
    printf 'Existing llama.cpp checkout is at %s; expected pinned commit %s.\n' "$current" "$PINNED_COMMIT" >&2
    printf 'Move it aside manually if you want to fetch a fresh pinned checkout.\n' >&2
    exit 1
  fi
  printf 'llama.cpp already matches pinned commit %s\n' "$PINNED_COMMIT"
  exit 0
fi

git clone --filter=blob:none https://github.com/ggml-org/llama.cpp.git "$DEST"
git -C "$DEST" checkout --detach "$PINNED_COMMIT"
printf 'Checked out llama.cpp commit %s\n' "$PINNED_COMMIT"
