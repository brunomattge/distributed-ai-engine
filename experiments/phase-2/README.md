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

The exact three-A07 hardware target remains unverified. The owner deferred Wi-Fi transport; the results below are USB-only. Phase 3 adds the local chat and read-only status tool in its own directory.

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

The corrected harness completed the comparison and repeat described above. One-worker failure, both two-worker successes, and both three-worker successes are preserved. More repeat trials, live RAM/temperature, time-to-first-token, and the exact three-A07 target remain open. Wi-Fi transport is deferred by owner.
