# Phase 0: distributed inference feasibility

This directory contains the smallest repeatable experiment for determining whether compatible Android phones can participate in one `llama.cpp` inference. It does not implement the application, cluster control plane, or agent.

## Objective

Load one GGUF model through one `llama-completion` process and direct inference operations to three separate phone-hosted `ggml-rpc-server` processes. The Mac is the coordinator only; it must not perform model inference for a successful result.

## Scope and risks

- Use upstream `llama.cpp` RPC and Android `arm64-v8a` builds.
- Run a single short prompt, collecting the coordinator and worker logs.
- Risks: RPC is explicitly described upstream as proof-of-concept, fragile, and insecure; Android RPC server support may not work with the A07's MediaTek Helio G99; 4 GB nominal RAM is not 4 GB available to the process; Wi-Fi latency can dominate token generation.
- Keep RPC on a trusted isolated LAN. The protocol is not authenticated and must not be exposed to the internet.

## Acceptance criteria

1. Three distinct A07 worker processes start and report available backend memory.
2. A single request against a single GGUF produces a non-empty response.
3. The coordinator is configured to use only the three remote RPC devices for model layers; the Mac CPU/Metal must not hold model layers.
4. Coordinator and each worker log evidence of graph compute/tensor work for that request. Three open TCP connections alone do not count.
5. Record model filename, exact llama.cpp commit, phone model/Android build, available worker memory, prompt/context/token settings, time to first token, generation speed, and failures.

If any criterion cannot be proven, record Phase 0 as blocked/failed with the exact logs and do not claim distributed inference works. The current hardware run is over USB ADB; direct Wi-Fi transport is still unverified.

## Experiment procedure

On macOS, double-click `Iniciar experimento.command` in Finder to open a prepared Terminal session. It prompts for the `llama-completion` path, GGUF path, and phone IPs, then checks all three RPC ports before running the experiment. The workers still need to be built and started on the phones first.

1. Build upstream `llama.cpp` on the Mac with `-DGGML_RPC=ON`, and build its Android `arm64-v8a` worker binaries with `-DGGML_RPC=ON`. The upstream Android CMake recipe is in [build.md](https://github.com/ggml-org/llama.cpp/blob/master/docs/build.md); RPC usage and limitations are in [tools/rpc/README.md](https://github.com/ggml-org/llama.cpp/blob/master/tools/rpc/README.md). Pin and record one commit for both builds; do not mix versions.
2. Copy/run `ggml-rpc-server` on each phone using Termux or an Android native shell. Bind it to the private Wi-Fi interface on port `50052`; verify the process stays alive with the screen off only if background operation is in scope for this experiment.
3. On the Mac, provide three `IP:PORT` endpoints and a local GGUF file. The model and context must be small enough for the available worker RAM. Do not download or redistribute a model without checking its license.
4. Run `./run-inference.sh /path/to/llama-completion /path/to/model.gguf PHONE1:50052 PHONE2:50052 PHONE3:50052`.
5. Save each phone's complete server log separately. Review the coordinator log and all three worker logs; confirm compute/tensor RPC activity on every worker, and confirm model layer assignment excludes local Mac devices. A generated answer without those proofs is inconclusive.

### Tested USB transport on macOS

Wi-Fi device-to-device traffic was not available on the current router, so the successful feasibility run used ADB over USB with one forwarded host port per phone. This keeps one RPC worker per phone while avoiding Wi-Fi routing. With the three phones connected and USB debugging authorized:

For a guided run in Terminal, double-click [`Run Phase 0 USB experiment.command`](Run%20Phase%200%20USB%20experiment.command). It checks for exactly three authorized phones, starts their workers, streams coordinator milestones and graph-compute progress as they happen, then prints the result and saves an English `summary-en-us.log` alongside the raw logs in a timestamped local results directory. Run artifacts are excluded from Git.

To reproduce the larger-model boundary test, double-click [`Run larger model USB experiment.command`](Run%20larger%20model%20USB%20experiment.command). It resumes the Qwen2.5 7B Q3_K_M download if needed and runs the same USB experiment with that GGUF.

```sh
ADB="$HOME/Library/Android/sdk/platform-tools/adb"
ANDROID_BIN="$(pwd)/build-android/bin/ggml-rpc-server"

"$ADB" -s SERIAL1 push "$ANDROID_BIN" /data/local/tmp/ggml-rpc-server
"$ADB" -s SERIAL2 push "$ANDROID_BIN" /data/local/tmp/ggml-rpc-server
"$ADB" -s SERIAL3 push "$ANDROID_BIN" /data/local/tmp/ggml-rpc-server

for serial in SERIAL1 SERIAL2 SERIAL3; do
  "$ADB" -s "$serial" shell chmod 755 /data/local/tmp/ggml-rpc-server
  "$ADB" -s "$serial" shell 'GGML_RPC_DEBUG=2 nohup /data/local/tmp/ggml-rpc-server -H 127.0.0.1 -p 50052 > /data/local/tmp/ggml-rpc-server.log 2>&1 < /dev/null &'
done

"$ADB" -s SERIAL1 forward tcp:50052 tcp:50052
"$ADB" -s SERIAL2 forward tcp:50053 tcp:50052
"$ADB" -s SERIAL3 forward tcp:50054 tcp:50052

./run-inference.sh build-mac/bin/llama-completion \
  models/qwen2.5-0.5b-instruct-q4_k_m.gguf \
  127.0.0.1:50052 127.0.0.1:50053 127.0.0.1:50054
```

Replace `SERIAL1` through `SERIAL3` with the IDs shown by `adb devices -l`. The Wi-Fi-based `Iniciar experimento.command` remains available for direct LAN runs; it does not configure USB forwarding.

The runner writes the coordinator log to stdout/stderr. Capture it with shell redirection when saving results. Start workers with `GGML_RPC_DEBUG=2` and retain their logs. Do not publish logs before removing private IPs and device identifiers.

## Build record

- Upstream source: `ggml-org/llama.cpp`, commit `9183a552e5fdcbdb5243f187f3a63204eca7dba9` (recorded for reproducibility; this is a moving upstream development revision).
- Mac binaries: `build-mac/bin/llama-completion` and `build-mac/bin/ggml-rpc-server`; configured with RPC and Metal. The CLI help reports `--rpc`.
- Android worker: `build-android/bin/ggml-rpc-server`, built for `arm64-v8a` / Android API 28 with RPC enabled and static C++ runtime. `file` identifies it as an Android AArch64 ELF executable; its only dynamic dependencies are Android system libraries (`libc`, `libm`, `libdl`).
- Model: [Qwen2.5-0.5B-Instruct-GGUF](https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF), file `qwen2.5-0.5b-instruct-q4_k_m.gguf`, pinned repository revision `df5bf01389a39c743ab467d734bf501681e041c5`, Apache-2.0. SHA-256: `74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db`.

## Results

Status: **distributed inference demonstrated over USB**. Three Android RPC workers started, and one `llama-completion` process generated a response from the same Qwen2.5-0.5B GGUF while distributing model layers and graph recomputation across all three remote workers. The coordinator log shows `RPC0`, `RPC1`, and `RPC2`, records tensor transfers and graph execution, and reports 0 MiB of model weights on the Mac host. In the guided run, each worker log contains tensor operations and 24 `GRAPH_RECOMPUTE` requests; the coordinator exited with code 0.

- Phones: two Galaxy A07 (`SM-A075M`) and one Galaxy A06 (`SM-A065M`), all Android 16. No exact Samsung model is an MVP requirement; these are the devices used for this run.
- Transport: USB ADB forwarding from Mac ports 50052, 50053, and 50054 to port 50052 on each phone. Direct Wi-Fi pairing/traffic remains unresolved on the current router.
- Model: `qwen2.5-0.5b-instruct-q4_k_m.gguf`, Q4_K_M, 462.96 MiB; context 512; prompt “In one sentence, explain what a distributed system is.” The guided run generated 18 tokens and returned a non-empty answer.
- Android `/proc/meminfo` reported about 1.16–1.23 GiB `MemAvailable` per phone before the run. RPC worker model allocations were about 90, 78, and 205 MiB; the Mac memory breakdown reports zero model MiB on Host (about 7 MiB host compute workspace).
- Performance: model load 18.04 s; prompt processing 0.93 s; generation 4.41 tokens/s; final inference request 5.03 s. Time to first token was not measured separately. This is a small feasibility measurement over USB, not a Wi-Fi performance estimate.
- The guided run stores `coordinator.stdout.log`, `coordinator.debug.log`, and one raw log per worker in its local results directory. Those device-specific artifacts are excluded from Git.

### Larger single-model test

The Qwen2.5 7B Instruct Q3_K_M candidate also completed a real distributed inference across the same three phones. Its 3.81 GB GGUF loaded with model allocations of 1,068 MiB on RPC0, 1,060 MiB on RPC1, and 1,274 MiB on RPC2; the Mac Host model allocation was 0 MiB. Each worker recorded 30 `GRAPH_RECOMPUTE` operations, and the model returned a non-empty answer.

- Phones and transport: two Galaxy A07 (`SM-A075M`) and one Galaxy A06 (`SM-A065M`), Android 16, connected by USB ADB forwarding.
- Model: [`Qwen2.5-7B-Instruct-Q3_K_M.gguf`](https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF), file `qwen2.5-7b-instruct-q3_k_m.gguf`; context 512 and 32-token generation limit.
- Performance: model load 156.91 s; prompt processing 10.998 s for 19 tokens (1.73 tokens/s); generation 26.72 s for 31 tokens (1.16 tokens/s); request total 37.75 s after model load.
- The run produced a local `summary-en-us.log` and raw coordinator/worker logs. These device-specific run artifacts are excluded from Git; the measured outcome and limitations are recorded in this guide.

This establishes that the current 7B Q3_K_M candidate can load and compute across the three available Android phones. It does not establish that this is the absolute largest viable model, that other Android ABIs/backends are compatible, or that Wi-Fi transport works.

The core Phase 0 proof is demonstrated on the current devices. Phases 1–3 have since added cluster lifecycle, distributed chat, and one read-only status tool. Compatibility with other Android ABIs/backends and direct Wi-Fi transport remain open checks; neither is required to use a specific Galaxy model for the MVP.

## Alternatives if upstream RPC fails

Do not replace the requirement with three independent models. First isolate whether the failure is Android build/runtime, RPC transport, or backend compatibility. Then evaluate (a) a small native RPC compatibility fix/fork with an explicit maintenance cost, or (b) a different single-model distributed inference runtime that supports Android ARM64 workers. If neither can execute one model across all three phones, document the hardware/runtime blocker and ask whether to revise the hardware constraint before starting later MVP phases.
