# Third-party components and models

The repository's MIT license applies to original project code and documentation only.

- **llama.cpp:** fetched separately at the commit recorded in `experiments/phase-0/README.md`; its upstream source is MIT licensed. The checkout and compiled binaries are excluded from this repository. Upstream maintains additional notices for its dependencies.
- **GGUF model weights:** not included. The current Qwen2.5 quantized model is downloaded separately; review the model publisher's license and attribution before using or redistributing it.
- **Android/macOS toolchains:** CMake, the Android NDK, ADB, and system Python are external tools, not bundled libraries.

Revisit this inventory before distributing binaries, model weights, vendored code, or packaged containers.
