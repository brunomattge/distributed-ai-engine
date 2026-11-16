# Phase 2: distributed LLM integration

## Objective

Compare the same GGUF, prompt, and context while one, two, and three phone workers participate in a single llama.cpp RPC inference. Each run explicitly selects only its RPC workers, so unused workers remain idle and the Mac cannot silently provide model layers.

## Scope and risks

- Requires an active, healthy Phase 1 USB cluster.
- Uses the existing Mac llama-completion binary, RPC worker endpoints, context size 512, and generation limit 32.
- Keeps coordinator and worker logs in their original format and language. Only the final consolidated result is written in English.
- A one-shot completion loads the model for each request. For Qwen2.5 7B Q3_K_M this took about 157 seconds in Phase 0.
- Current hardware remains two Galaxy A07s and one Galaxy A06. The owner deferred Wi-Fi transport; current experiments use USB only.
- Does not add a chat interface, persistent conversation, or agent tool; those belong to Phase 3.
- One or two workers may not have enough memory for a model that succeeds on three; a failed run is captured as a result rather than hidden.

## Acceptance criteria

1. The command refuses to run unless a healthy three-worker cluster is active.
2. The same model and prompt are attempted with one, two, and three RPC workers.
3. Each configuration records graph recomputation per phone and model-weight allocation per RPC; successful results require the Mac host to have zero model weights.
4. The terminal shows raw logs as they arrive and saves coordinator/worker logs without translation.
5. An English summary records success or failure, worker calculations, allocations, and timings in a separate `summary-en-us.log`.

## Use

From this directory, start the Phase 1 cluster in the same Mac Terminal:

    ../phase-1/clusterctl.sh start

Run a prompt using the 7B model:

    ./infer-distributed.sh ../phase-0/models/qwen2.5-7b-instruct-q3_k_m.gguf "Summarize each phone’s role in this distributed inference in one sentence."

When finished, stop the cluster:

    ../phase-1/clusterctl.sh stop

The raw inference logs and final summary are stored in a timestamped inference directory under the active Phase 1 session in results/.

## Validation

The integration run on 2026-11-14 passed using the Qwen2.5 7B Q3_K_M model over USB on two Galaxy A07s and one Galaxy A06, Android 16:

- The command required a healthy 3/3 Phase 1 cluster before inference.
- The model returned a non-empty response in one distributed request.
- Each phone logged 30 GRAPH_RECOMPUTE operations. Model allocations were 1,068 MiB, 1,060 MiB, and 1,274 MiB on RPC0/RPC1/RPC2; the Mac Host held 0 MiB of model weights.
- Model load took 156.01 s; prompt processing 13.24 s; generation 1.15 tokens/s; the request took 40.20 s after model load.
- The run summary and raw coordinator/worker logs are local artifacts excluded from Git. The measured outcome and limitations are recorded here.

These results validate the available two-A07/one-A06 Android cohort; other Android ABIs/backends remain unvalidated. The owner deferred Wi-Fi transport; the results below are USB-only. Phase 3 adds the local chat and read-only status tool in its own directory.

### Worker-count comparison — 2026-11-14

A corrected comparison and one repeat used the same Qwen2.5 7B Q3_K_M GGUF, prompt, 512-token context, and 32-token generation limit over USB. The coordinator restarted all three RPC workers between cases. Hardware was two Galaxy A07s and one Galaxy A06; the Mac held 0 MiB of model weights in every successful case.

| Workers | Result | Phone graph work | RPC model weights | Load (run 1 / run 2) | Prompt eval (run 1 / run 2) | Generation (run 1 / run 2) |
| --- | --- | --- | --- | ---: | ---: | ---: |
| 1 | Failed once during context setup; RPC connection crashed after weight allocation | 0 | 3,402 MiB on one A07 | Not completed | Not completed | No response |
| 2 | Passed twice | 30 per A07 in both runs | 1,598 MiB + 1,804 MiB | 149.55 / 149.66 s | 15.04 / 14.75 s | 1.24 / 1.24 tokens/s |
| 3 | Passed twice | 30 per phone, then 24 per phone | 1,068 MiB + 1,060 MiB + 1,274 MiB | 157.89 / 158.47 s | 15.58 / 15.45 s | 1.12 / 1.14 tokens/s |

Two phones were sufficient to load and execute this model in both runs; one phone failed after allocating weights. Two-worker generation measured 1.24 tokens/s in both runs. Three-worker generation measured 1.12 and 1.14 tokens/s, about 9% lower on average for this mixed A07/A06 cluster. These two samples are useful evidence, not a general performance guarantee. They do not measure time-to-first-token or live phone RAM/temperature. The corrected run summaries and raw coordinator/worker logs are local artifacts excluded from Git. The measured outcomes and limitations are recorded in this section. An earlier incomplete harness attempt is retained separately and excluded because its RPC servers were not restarted between cases.

## Comparative worker-count benchmark

Start a healthy three-phone USB cluster, then run:

    ./benchmark-workers.sh ../phase-0/models/qwen2.5-7b-instruct-q3_k_m.gguf "Summarize the phones’ role in this inference in one sentence."

By default, the script runs worker counts 1, 2, and 3 sequentially with the same model, prompt, context, and token limit. Set `BENCHMARK_WORKER_COUNTS=3,2,1` to run the higher-capacity case first. It stops and restarts all RPC servers between configurations so a failed/OOM worker cannot invalidate the next case. It records original coordinator and phone logs under the active Phase 1 session's `benchmark-*` directory and writes `summary-en-us.log`. Start model and worker monitoring with `../phase-1/clusterctl.sh` as usual; stop with Ctrl+C only if you need to cancel an in-progress model load or inference.

The corrected harness completed the comparison and repeat described above. One-worker failure, both two-worker successes, and both three-worker successes are preserved. More repeat trials, live RAM/temperature, and time-to-first-token remain open. Wi-Fi transport is deferred by owner.

### Repeated fast-profile worker-count sweep — 2026-11-15

Two samples per worker count used the same Qwen2.5 0.5B Q4_K_M GGUF, prompt (`Reply with exactly: OK.`), 512-token context, and 32-token generation limit over USB. The second pass reversed the order (1, 2, 3, then 3, 2, 1). Hardware was two Galaxy A07s and one Galaxy A06, Android 16. The model SHA-256 was `74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db`; llama.cpp was version `1.0.0-dev`, source commit `9183a552e5fdcbdb5243f187f3a63204eca7dba9`.

| Workers | Results | GRAPH_RECOMPUTE per selected worker | RPC model weights | Load (run 1 / run 2) | Prompt eval (run 1 / run 2) | Generation (run 1 / run 2) | Wall time (run 1 / run 2) |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: |
| 1 | Passed twice | 1 each | 373 MiB on A07 | 19.84 / 19.81 s | 0.65 / 0.66 s | 0.43 / 0.43 s (4.62 / 4.62 tokens/s) | 22 / 21 s |
| 2 | Passed twice | 1 each | 129 MiB + 244 MiB on A07s | 15.66 / 15.66 s | 0.72 / 0.68 s | 0.46 / 0.56 s (4.37 / 3.59 tokens/s) | 18 / 17 s |
| 3 | Passed twice | 1 each | 90 MiB + 78 MiB on A07s, 205 MiB on A06 | 18.13 / 18.14 s | 0.73 / 0.74 s | 0.50 / 0.42 s (3.97 / 4.76 tokens/s) | 20 / 21 s |

The Mac reported 0 MiB of model weights in all six runs. This confirms selected-worker participation for this small model and prompt; it does not establish a performance benefit from additional workers. Two samples per configuration are exploratory, the output is very short, and time-to-first-token, device RAM and temperature were not measured. Other Android ABI/backend combinations remain unvalidated. The English summary and raw coordinator/worker logs are local artifacts excluded from Git.
