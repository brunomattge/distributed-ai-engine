# Third-party components and models

The repository's MIT license applies to original project code and documentation only.

- **llama.cpp:** fetched separately at the commit recorded in `experiments/phase-0/README.md`; its upstream source is MIT licensed. The checkout and compiled binaries are excluded from this repository. Upstream maintains additional notices for its dependencies.
- **Qwen2.5 0.5B Instruct GGUF:** the recommended fast profile is downloaded separately from [Qwen's official model repository](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF), which declares Apache-2.0. The measured artifact was `qwen2.5-0.5b-instruct-q4_k_m.gguf`, SHA-256 `74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db`.
- **Qwen2.5 7B Instruct GGUF:** the optional larger profile is downloaded separately from [Qwen's official model repository](https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF), which declares Apache-2.0.
- GGUF model weights are not included or redistributed by this repository. These notices apply to the named upstream model repositories; users must verify the source, exact artifact, and license of any replacement GGUF.
- **Android/macOS toolchains:** CMake, the Android NDK, ADB, and system Python are external tools, not bundled libraries.

Revisit this inventory before distributing binaries, model weights, vendored code, or packaged containers.
