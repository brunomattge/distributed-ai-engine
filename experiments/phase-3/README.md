# Phase 3: local chat and minimal agent

## Objective

Provide a local browser chat backed by one llama.cpp server that keeps the distributed GGUF loaded across turns, plus one allowlisted, read-only cluster-status tool.

## Scope and risks

- The llama.cpp server and the Python chat API bind only to 127.0.0.1.
- The model remains distributed over the active Phase 1 RPC workers.
- Chat history lives only in the browser tab; reloading clears it.
- The only agent tool calls the Phase 1 status command. It cannot run arbitrary commands or change device state.
- The model takes about 160 seconds to become ready, and generation is about 1 token/second on the current USB setup.
- Current devices are two Galaxy A07s and one Galaxy A06. USB is the supported transport. Other Android ABIs/backends remain unvalidated; no exact Samsung model is required by the MVP.
- Hardware priority is compatible Android phones first, other CPU platforms when they are available for validation, and GPU hosts in a later platform milestone. The inference contract is separated for future adapters, but only the current llama.cpp Android path exists today.

## Acceptance criteria

1. A local browser page can send a chat message and receive a non-empty answer without reloading the model between turns.
2. A request for cluster status invokes the allowlisted status tool, returns live worker state to the model, and displays the tool use.
3. The llama.cpp server uses RPC0/RPC1/RPC2 for model weights and logs graph calculation on all three phones, with zero Host model weights.
4. The HTTP services bind to loopback only.
5. Raw engine and worker logs stay unchanged; a separate final English summary is saved with the run.

## Complete local stack

From the repository root, use `./tools/run-local-mvp.sh` to start the distributed model, the .NET platform, the ADB bridge, and Angular in one Terminal session. The launcher requires exactly three authorized USB Android devices, defaults to the measured fast model, waits for model and cluster readiness, and stops its child launchers and their RPC workers on Ctrl+C. Use `./tools/run-local-mvp.sh large` for the 7B profile or pass a supported GGUF path. The lower-level `start-mvp.sh` remains useful for inference-only experiments; its Python port 8000 is internal to the .NET gateway for integrated use.

On 2026-11-16, the one-terminal launcher was validated on the available three-phone USB cohort (two Galaxy A07 and one Galaxy A06, Android 16). The fast Qwen2.5 0.5B model reached ready state in 19 seconds, and the .NET status endpoint reported 3/3 active RPC workers. Model buffers were allocated as 90/78/205 MiB on RPC0/RPC1/RPC2 and 0 MiB on the Mac. Ctrl+C stopped the coordinator and all three RPC workers; ports 4200, 5100, 8000, 8080, 50052, 50053, and 50054 were free afterward. Worker logs contained one graph-recompute record each during startup. No chat completion was requested during this launcher lifecycle check, so it is not a generation benchmark or a response-quality test.

## Local inference API preview

The ASP.NET Core platform exposes a small OpenAI-shaped API at `http://127.0.0.1:5100/v1` for independent client experiments. Run the Phase 3 model launcher and the .NET platform with the ADB bridge (`./tools/run-dashboard.sh adb`) so all three Android nodes are registered. `GET /models` lists the configured model, and `POST /chat/completions` supports text messages, `temperature`, `max_completion_tokens`, legacy `max_tokens`, and SSE streaming. Send only one token-limit field; values from 1 through 2048 are accepted. The request `model` must match the currently loaded model. The optional engine field `workload_mode` accepts only `single_model_distributed`; `independent_replicas` is rejected because this runtime does not launch separate replicas. Concurrent requests each receive a separate job ID and are not evidence of replica throughput. Successful and failed inference responses include `X-Distributed-AI-Job-Id`, `X-Distributed-AI-Workload-Mode`, `X-Distributed-AI-Model`, `X-Distributed-AI-Backend`, and `X-Distributed-AI-Selected-Nodes` headers. Query `GET http://127.0.0.1:5100/api/inference/jobs/{jobId}` for accepted/running/completed/failed/cancelled/timed-out state; the last 256 process-local records are retained, terminal jobs are evicted first, and no job survives a platform restart. Admission requires the distributed-readiness gate plus an eligible placement for the loaded model and `llama.cpp-rpc`. An upstream structured SSE error marks the session degraded. The Python routes on port 8000 are internal and must not be used by clients because they bypass the .NET session-failure latch. Both services remain loopback-only. Tool calling, authentication, model negotiation, usage accounting, and full OpenAI API compatibility are not implemented. The existing Angular chat continues through the .NET gateway and agent runtime.

With the Phase 3 launcher running and the model ready, a local client can try:

```sh
curl http://127.0.0.1:5100/v1/models

curl http://127.0.0.1:5100/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -d '{"model":"MODEL_ID_FROM_MODELS","messages":[{"role":"user","content":"Explain RPC in one sentence."}],"stream":true}'
```

The repository also includes a dependency-free terminal client that discovers the first model, prints streamed text as it arrives, and reports client-side first-content and elapsed times:

```sh
python3 examples/clients/stream_completion.py
```

This exercises the HTTP contract independently of Angular. The real distributed run still requires the model launcher, the .NET platform, the ADB bridge, and all three compatible Android workers.

On 2026-11-16, the dependency-free reference client completed a physical streamed request through the .NET `/v1` gateway and all three USB Android RPC workers using Qwen2.5 0.5B Q4_K_M. The prompt `Reply with only OK.` returned `OK` with a two-token limit. The client observed 1,786.9 ms to first content and 2.019 s total; the server summary measured 1.47 s prompt evaluation, 1.71 s total, and 4.24 generated tokens/s. These timings come from one short request and are not a benchmark. The model loaded 90/78/205 MiB of weights on RPC0/RPC1/RPC2 and 0 MiB on the Mac.

The official OpenAI Python SDK was installed only in a temporary environment and tested on the same USB cohort. With SDK version 2.48.0, model listing, streamed chat completion, and structured `tools_not_supported` HTTP 422 errors worked. A physical streamed request using `max_completion_tokens=2` returned `OK`; Python HTTP tests verify the field maps to the provider's token limit. The legacy `max_tokens` field remains covered by the same tests. Sending both fields or an out-of-range limit returns HTTP 400. This is evidence for those specific operations, not full OpenAI API compatibility. Shutdown released all local ports and stopped all three Android RPC processes; no SDK dependency was added to the repository.

## Use

The Python API delegates chat orchestration to `agent_runtime`, which consumes
the provider-neutral v1 request/result contract and experimental llama.cpp
adapter from the separate `inference` package. The adapter is injectable for
unit tests. Compatibility imports remain at the old `agent_runtime` paths.
Tests cover provider request/response
mapping, HTTP and connection errors, and the Python HTTP chat route with a fake
runtime. The llama.cpp SSE text-delta capability is now connected to the Python
runtime and exposed at `POST /api/chat/stream`. It emits `delta`, `tool_events`,
`done`, and `error` events; a status question runs the allowlisted status tool
before the streamed final answer. The endpoint is tested with a fake provider.
The .NET platform exposes both `POST /api/chat` and a raw SSE proxy at
`POST /api/chat/stream`. The proxy uses response-header streaming, forwards
request cancellation, and maps failures before the first stream bytes to 502.
Configure the Python API base URL with `AGENT_RUNTIME_URL` (default
`http://127.0.0.1:8000/`). The non-streaming chat request timeout is 20 minutes
to accommodate measured phone-cluster latencies. Angular now consumes the SSE
route through the same-origin proxy and renders text as it arrives. The
separate Angular platform dashboard persists completed message snapshots via
the .NET API in PostgreSQL, lists recent conversations, resumes one, and starts
new ones; the standalone prototype launched by `start-mvp.sh` remains
session-only. The dashboard also provides up to 10 explicit user-managed
memory notes, which the .NET API forwards to the Python runtime for both
streaming and non-streaming requests. The runtime treats them as untrusted
context. RAG and additional provider implementations remain later increments.
The .NET suite validates gateway, memory and conversation persistence, and
route behavior; the Python suite covers the SSE adapter and endpoint.

The cluster dashboard separately displays model-server readiness (`ready`,
`loading`, or `unavailable`) from the local `llama-server /health` endpoint,
proxied through the Python runtime and ASP.NET Core. This status describes the
coordinator only; it is not a per-phone readiness signal or proof of worker
participation. The polling path does not open additional connections to RPC
workers.

Run the full local suite from the repository root with:

    python3 -m unittest discover -s tests -q

The HTTP API tests bind a temporary server to loopback; environments that
restrict local sockets must allow that loopback bind for the full suite.

Build the local llama-server target once:

    cmake -S ../phase-0/upstream-llama.cpp -B ../phase-0/build-mac -DLLAMA_BUILD_SERVER=ON
    cmake --build ../phase-0/build-mac --target llama-server --parallel 4

Then start the app from a Mac Terminal:

    ./start-mvp.sh

The launcher offers a cluster-wide model choice: **1** selects the fast,
measured Qwen2.5 0.5B Q4_K_M profile (default); **2** selects the larger Qwen2.5
7B Q3_K_M profile. Non-interactive launches default to `fast`. You can also
select explicitly with `./start-mvp.sh fast`, `./start-mvp.sh large`, or pass
the path to another local GGUF. The chat and cluster pages show the same active
model. Changing it requires restarting the inference session; it is not a
per-conversation setting.

For an exploratory same-model worker-count run, set `INFERENCE_WORKER_COUNT=1`,
`2`, or `3` before launching. The default is `3`. For example:

    INFERENCE_WORKER_COUNT=1 ./start-mvp.sh fast

All three RPC daemons still run for the fixed USB cluster; this setting tells
llama.cpp which leading RPC endpoints to use for that model session. The final
English run summary records the selected RPC count and each worker's graph
activity. This is an experiment control, not a scheduler or a claim that a
single-worker run is distributed across the whole cluster.

To run repeatable measurements from the same Terminal session, set
`INFERENCE_BENCHMARK_REPETITIONS` and the metadata below before starting the
launcher. The launcher waits for model readiness, runs the requested number of
uncached streamed requests, writes a JSON report under the ignored local results
folder, then stops the model and workers through its normal cleanup path. Repeat
the command with `INFERENCE_WORKER_COUNT=1`, `2`, and `3` as separate runs.

```sh
INFERENCE_WORKER_COUNT=3 \
INFERENCE_BENCHMARK_REPETITIONS=3 \
INFERENCE_BENCHMARK_MODEL_LICENSE=Apache-2.0 \
INFERENCE_BENCHMARK_RUNTIME_REVISION=9183a552e5fdcbdb5243f187f3a63204eca7dba9 \
INFERENCE_BENCHMARK_HARDWARE_COHORT='two Galaxy A07 and one Galaxy A06, Android 16, USB' \
./start-mvp.sh fast
```

The report calculates the selected GGUF's SHA-256 and records the model ID,
license, runtime revision, cohort, context, prompt, temperature, token limit,
selected worker count, each request's first-content latency and total time,
failures, and median/range summary. For each request, it samples selected
Android workers before, during, and after inference. Every sample records a UTC
timestamp, RPC worker, model family, Android version, `MemAvailable`, the `SKIN`
temperature when exposed, and collection duration. The JSON includes raw samples
and per-request per-worker ranges/means; ADB serials are never copied into the
report. It sends requests directly to the local llama.cpp OpenAI-shaped endpoint
for inference measurements; it does not measure the .NET or Angular request
path. Use a non-sensitive prompt because the exact prompt is stored in the
local report. These are device-wide values, not model-process RAM or internal
chip temperatures, and polling adds a small amount of USB traffic that can
slightly affect request timings. The client does not infer token throughput
from SSE chunk counts. Retain the launcher-generated English session summary
and coordinator/worker logs alongside the JSON report when publishing a
benchmark.

Open http://127.0.0.1:8000 in a browser. Ctrl+C stops the chat API and model server and stops the workers started by the launcher. When the first streamed content arrives, the Terminal prints its runtime-observed latency in English and the event is appended to `chat-events.jsonl`. The session summary records completed response count, startup and request timings, worker graph counts, weight allocation, and the last streamed response's time to first content. This latency starts when the Python chat runtime begins processing the stream and includes any tool round trip; it is not a model-only TTFT measurement.

### Supervised RPC recovery

The launcher watches the llama-server process. If an RPC failure aborts it, the chat reports a degraded session and blocks new messages. The launcher waits for all three authorized USB phones, restarts the three RPC workers and loads a new coordinator session. The default limit is two recovery attempts and ten minutes waiting for the phones; configure these with `INFERENCE_MAX_RECOVERIES` and `INFERENCE_PHONE_WAIT_SECONDS`. If a limit is reached, the chat stays blocked and the Terminal gives the manual recovery status.

The failed request is not replayed. After the UI reports the model ready again, the user can submit it again. This is supervised process restart, not checkpoint-based continuation of generated tokens.

## Validation

Validated on 2026-11-14 with the Qwen2.5 7B Q3_K_M model over USB on two Galaxy A07s and one Galaxy A06, all Android 16:

- The browser chat returned a non-empty answer.
- The “Consultar cluster” control called the allowlisted tool. The tool queried the live Phase 1 status and the model reported all three workers healthy.
- The server bound to 127.0.0.1 on ports 8080 and 8000.
- RPC0/RPC1/RPC2 held 1,068/1,060/1,274 MiB of model weights; Host held 0 MiB. Each worker logged 30 GRAPH_RECOMPUTE operations during the final session.
- Server readiness took 160 s. The status tool round-trip took 254.21 s across its two model calls (153.25 s + 100.96 s); the final answer was “All three cluster phones are ready and healthy.” A cached follow-up took 20.03 s at 0.90 tokens/s.
- Local run summaries and raw worker logs are excluded from Git; the measured outcomes and limitations are recorded in this guide.

The prototype passed its chat, tool, loopback, and distributed-inference checks on the available two-A07 plus one-A06 cluster. USB is the supported transport; other Android ABIs/backends need separate compatibility runs. The counts in this historical validation are from that earlier point in development.

An exploratory profile comparison used one identical short prompt and a
two-token output on each model: Qwen2.5 0.5B Q4_K_M returned in 9.08 s
with a 19 s load; Qwen2.5 7B Q3_K_M returned in 133.05 s with a 163 s load.
Each worker logged graph work for both runs and the Mac held 0 MiB of model
weights. The launcher defaults to the 0.5B profile based on its lower observed
latency in this single sample; the larger model remains selectable. This is
not a repeated benchmark or a quality assessment. Run logs are local and
excluded from Git.
A separate tool-selection smoke check showed the 0.5B model calling the
allowlisted cluster-status tool with a simulated callback; it did not validate
the reliability of tool selection or query the physical workers.

### Exploratory RPC worker-count sweep

On 2026-11-15, one fresh chat request per configuration used the same Qwen2.5
0.5B Q4_K_M, prompt, context and token limit, first with 1 RPC, then 2, then 3.
All three answered `OK`; total llama.cpp times were 8.38 s, 8.40 s and 8.92 s.
Each selected phone logged one `GRAPH_RECOMPUTE`, and the Mac held 0 MiB of
model weights. This is one sample per configuration, not enough to compare
performance. TTFT was not recorded separately. Raw device logs remain local
and are excluded from Git. The selection control is experimental; it is not
scheduler placement or proof of per-phone model readiness.

### Repeated cold RPC worker-count measurements — 2026-11-16

The worker-count comparison was repeated on the same USB cohort (two Galaxy
A07s and one Galaxy A06, Android 16) with Qwen2.5 0.5B Q4_K_M. Each setting
received three identical requests through the local llama.cpp OpenAI-compatible
streaming endpoint. The request used the same system prompt, status-tool schema,
user prompt (`Reply with only the word OK.`), sampling settings, and
`cache_prompt: false`; all nine requests returned HTTP 200 and `OK`. Each input
contained 218 tokens and generated two tokens.

| Selected RPC workers | Median complete HTTP response | Range | Median llama.cpp total | Model-weight allocation |
| --- | ---: | ---: | ---: | --- |
| RPC0 (one Galaxy A07) | 8.057 s | 7.774–8.104 s | 8.052 s | RPC0 373 MiB; Mac 0 MiB |
| RPC0 + RPC1 (two Galaxy A07s) | 7.997 s | 7.923–8.007 s | 7.991 s | RPC0 129 MiB; RPC1 244 MiB; Mac 0 MiB |
| RPC0 + RPC1 + RPC2 (two Galaxy A07s and one Galaxy A06) | 8.198 s | 8.177–8.415 s | 8.192 s | RPC0 90 MiB; RPC1 78 MiB; RPC2 205 MiB; Mac 0 MiB |

The coordinator log recorded graph work on every selected RPC worker (one
`GRAPH_RECOMPUTE` record per selected worker in each session). The three-worker
configuration was 0.141 s slower than the one-worker configuration by median
HTTP response time. This small difference is not enough to establish a general
performance trend; the small 0.5B model and USB transport were used only to
validate participation and produce a controlled baseline. Phone temperature,
clock state, and repeated sessions on different days were not measured.

The prompt cache was initially observed to reduce repeated identical requests
from about 8 s to about 0.4 s. Those cached measurements were excluded from the
cold comparison. Raw request, coordinator, and worker logs are retained locally
under `experiments/phase-1/results/worker-count-cold-benchmark-*` and remain
excluded from Git. The launcher’s per-session chat summary reports `STOPPED`
for this low-level benchmark because requests went directly to llama.cpp and
did not pass through the Python chat event logger; success was confirmed from
the HTTP responses and coordinator/worker logs.

The reproducible benchmark mode was run on 2026-11-16 for one, two, and three
selected RPC workers on the same cohort. Each session sent three cache-disabled
streaming requests using `Reply with only the word OK.`, a two-token limit,
temperature 0.2, and context 512. All nine returned `OK.` with zero failures.
Model startup was measured separately from request latency.

| Selected workers | Median HTTP first content | Median total | Total range | Model-weight allocation | Mac model weights |
| --- | ---: | ---: | ---: | --- | ---: |
| RPC0 | 1,411.651 ms | 1.628 s | 1.615–1.637 s | RPC0 373 MiB | 0 MiB |
| RPC0 + RPC1 | 1,445.160 ms | 1.699 s | 1.675–1.709 s | RPC0 129 MiB; RPC1 244 MiB | 0 MiB |
| RPC0 + RPC1 + RPC2 | 1,451.276 ms | 1.737 s | 1.709–1.797 s | RPC0 90 MiB; RPC1 78 MiB; RPC2 205 MiB | 0 MiB |

The model was Qwen2.5 0.5B Q4_K_M (SHA-256
`74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db`), and the
llama.cpp revision was `9183a552e5fdcbdb5243f187f3a63204eca7dba9`. Startup took
22/17/20 s for the 1/2/3-worker sessions. These three short samples per
configuration do not establish a performance trend; one worker was fastest in
this specific run. The coordinator reported graph work on each selected RPC,
but those session totals do not identify work per individual request. The JSON
reports and raw technical logs remain local and excluded from Git. Device RAM
and temperature were not sampled during these requests.

### Repeated worker-count run with per-request Android telemetry — 2026-11-16

The same USB cohort (two Galaxy A07s and one Galaxy A06, Android 16) ran three
cache-disabled requests for each selection of 1, 2, and 3 RPC workers. All nine
requests returned `OK.`. During each request the sampler polled every selected
worker over ADB at 0.5-second intervals; each worker contributed nine
during-request samples across its three requests, in addition to before/after
samples. RAM is Android `MemAvailable`; temperature is the `SKIN` sensor.

| Selected workers | Median first content | Median total | During-request telemetry by selected worker (RAM range, mean; SKIN temperature) |
| --- | ---: | ---: | --- |
| RPC0 | 1,330.183 ms | 1.552 s | RPC0: 1,092–1,107 MiB (1,099.11 mean); 40.1 °C |
| RPC0 + RPC1 | 1,409.903 ms | 1.618 s | RPC0: 1,355–1,368 MiB (1,360.22 mean); 40.1 °C. RPC1: 1,167–1,180 MiB (1,172.78 mean); 38.0 °C |
| RPC0 + RPC1 + RPC2 | 1,516.533 ms | 1.718 s | RPC0: 1,396–1,403 MiB (1,399.67 mean); 40.1 °C. RPC1: 1,340–1,360 MiB (1,347.22 mean); 38.0 °C. RPC2: 1,153–1,176 MiB (1,163.11 mean); 42.0 °C |

Each cell summarizes samples collected during that configuration's three
requests; exact timestamped readings and before/during/after phases are in the
local JSON reports. The numbers are device-wide, short-window observations,
not process memory or model-only heat. ADB sampling shares the USB link and may
slightly affect latency. These three requests per configuration do not support
a general performance or thermal trend. Reports are under the ignored local
`experiments/phase-1/results/benchmarks/` directory and contain no ADB serials.
Each JSON report has a sibling `-summary-en-us.log` with concise per-request
latency and worker telemetry.

### Final integrated MVP run — 2026-11-16

The root launcher was run with the default fast profile on the attached USB
cohort (two Galaxy A07s and one Galaxy A06, Android 16). The Python model API
and ASP.NET Core inference-status API both reported `ready` with 3/3 selected
RPC workers. A non-streaming `/v1/chat/completions` request through the .NET
gateway returned `OK`. The response used Qwen2.5 0.5B Instruct Q4_K_M; startup
took 19 seconds and the request took 1.71 seconds (1.48 seconds prompt
processing, 4.29 generated tokens/s). RPC0/RPC1/RPC2 held 90/78/205 MiB of
model weights, and the Mac held 0 MiB. Each worker log recorded one
`GRAPH_RECOMPUTE` operation during the session. Ctrl+C stopped the model
server, API, ADB bridge and all three workers; the session summary is English
and raw technical logs remain local and excluded from Git.

### Runtime-observed first-content smoke — 2026-11-15

The updated streaming chat path was exercised with the fast Qwen2.5 0.5B Q4_K_M profile and all three USB workers (two Galaxy A07s and one Galaxy A06, Android 16). The prompt was `Reply with exactly: OK.` The response streamed `OK.`; each selected worker logged two `GRAPH_RECOMPUTE` operations. Model weights were allocated as 90 MiB, 78 MiB, and 205 MiB on RPC0/RPC1/RPC2, with 0 MiB on the Mac Host. Model startup took 20 s; prompt evaluation took 8.14 s; generation took 0.55 s at 3.66 tokens/s; total llama.cpp request time was 8.69 s.

The Python runtime observed 8,150.9 ms from starting stream processing until the first non-empty content chunk and printed that value to Terminal and `chat-events.jsonl`. This includes the runtime-to-llama-server request path and is not model-only TTFT. This is one smoke sample; it does not measure per-device RAM/temperature during inference or establish a performance trend. The English session summary and raw coordinator/worker logs are local artifacts excluded from Git.

### Supervised recovery process-failure injection — 2026-11-16

With the same two-A07/one-A06 USB cohort, the `ggml-rpc-server` process on worker 1 was stopped through ADB while the USB device remained connected. The Angular-to-.NET gateway immediately rejected a request with `inference_unavailable` and reported 2/3 active RPC processes. One internal Python request then caused the existing llama-server session to fail; the supervisor logged `degraded`, waited for all three authorized devices, restarted the workers, and loaded a new model-server generation. The status moved from generation 8 to generation 9 and back to `ready` in about 22 seconds. A fresh Angular-to-.NET streamed chat returned `RECOVERED.`.

In the recovered coordinator log, RPC0/RPC1/RPC2 held 90/78/205 MiB of model weights and Host held 0 MiB. Each worker recorded four `GRAPH_RECOMPUTE` operations across the two successful chat requests. The final request observed 8,124.6 ms to first content in the Python runtime; llama.cpp reported 8.12 s prompt processing, 0.94 s generation at 4.27 tokens/s, and 9.06 s total. This was a controlled RPC-process failure, distinct from the physical USB recovery verified below. Local run logs are excluded from Git.

### Supervised physical USB recovery — 2026-11-16

The launcher now checks the selected Android RPC processes while llama-server is serving. A worker missing from two consecutive ADB checks causes the launcher to stop the coordinator and enter the existing supervised-recovery path; one failed check alone does not trigger a restart.

On the mixed two-Galaxy-A07/one-Galaxy-A06 USB cohort, a physical cable removal moved the supervisor from generation 14 to `degraded` and `waiting_for_devices`. The .NET gateway reported 2/3 and kept chat unavailable. After the same phone returned to ADB, the launcher restarted all three RPC workers and loaded a new coordinator generation without an internal request to force failure. It reached generation 15 `ready` in about 22 seconds from restart start. A fresh Angular-to-.NET streamed chat returned `Physical Recovery OK.` The successful retry used `INFERENCE_PHONE_WAIT_SECONDS=1800`; an earlier attempt exceeded the default ten-minute wait before reconnection and required an operator restart.

The recovered coordinator reported 90/78/205 MiB of model weights on RPC0/RPC1/RPC2 and 0 MiB of model weights on the Mac Host. Each worker recorded four `GRAPH_RECOMPUTE` operations across the baseline and recovery requests. The final request took 8.27 s for prompt processing and 9.20 s total at 4.30 tokens/s; the Python runtime observed 8,267.3 ms to first content. This is one recovery sample on two A07s and one A06; it does not imply compatibility with other Android ABIs/backends. Raw logs and the English session summary are local artifacts excluded from Git.

### .NET gateway regression after inference-contract extraction

On 2026-11-14, the Angular chat proxy was exercised through the current .NET API, Python runtime and the same llama.cpp model server/RPC workers. It returned the streamed answer `OK` with HTTP 200. The end-to-end HTTP request took about 5.2 seconds. A separate isolated run of the same four-component path also returned HTTP 200 in 5.63 seconds.

This regression found and fixed an HTTP framing mismatch. `PythonAgentClient` had supplied JSON using chunked transfer encoding, while the Python HTTP handler expects a valid `Content-Length`; requests were rejected with HTTP 400. The .NET client now sends buffered JSON with an explicit length for both regular and streaming chat calls. The change does not alter the provider contract or the llama.cpp execution setup.

The corresponding llama.cpp request used a mostly cached prompt: task 9 evaluated one prompt token in 1.12 seconds and generated two tokens in 0.90 seconds (1.12 tokens/s), for 2.01 seconds total in llama.cpp. A prior task in the same run evaluated seven prompt tokens and generated two in 5.55 seconds total. Do not compare these as equivalent fresh prompts. Over the session, each worker log contained five `GRAPH_COMPUTE` records; these are cumulative session totals and are not a per-response counter. The workers were two Galaxy A07s and one Galaxy A06 over USB. Per-worker allocations were 1,068.55, 1,060.02 and 1,274.39 MiB; the coordinator memory breakdown reported 0 MiB of host model weights (with host-mapped and compute buffers reported separately).

The following live readiness smoke test returned `ready` through `/api/model-status` and through the Angular proxy while all three ADB nodes were online with their RPC processes running. A streamed chat call through Angular → .NET → Python → llama.cpp returned `OK`. For that request, each of the three worker logs recorded one `GRAPH_RECOMPUTE`; RPC allocations were 1,068.55/1,060.02/1,274.39 MiB and host model weights were 0 MiB. Prompt evaluation took 131.98 s for 234 tokens; generation took 1.06 s for two tokens at 0.94 tokens/s; total llama.cpp time was 133.05 s. The English summary and raw logs are local artifacts excluded from Git; the measured outcome is recorded in this guide. This validates the mixed two-A07/one-A06 cohort; other Android ABI/backend combinations remain untested.

The .NET suite passed 47 tests and the focused Python provider/server suites passed 19 tests for this integration change. Run summaries and raw coordinator/worker logs are local artifacts excluded from Git. At the time, this validated the local integration only; the clean-checkout reproduction is recorded below, while wider Android compatibility remains out of scope.

After the follow-up RPC-probe experiment was removed, the original launcher restarted the same mixed USB cohort. Model startup took about 160 seconds; the Angular → .NET → Python → llama.cpp stream again returned `OK` (HTTP 200). The uncached 235-token prompt took 130.73 seconds to evaluate; generation took 0.88 seconds at 1.14 tokens/s. A cached follow-up took 1.79 seconds in llama.cpp. The same worker allocations were observed (1,068.55, 1,060.02 and 1,274.39 MiB; host model weights 0 MiB). Run summaries and raw coordinator/worker logs are local artifacts excluded from Git.

### Clean-checkout end-to-end reproduction — 2026-11-16

A detached checkout of repository commit `fedd3f85e19d226657c4d61b53d27101fe6fcac9` built the pinned llama.cpp source (`9183a552e5fdcbdb5243f187f3a63204eca7dba9`) for macOS and Android `arm64-v8a`, restored the .NET solution, installed Angular dependencies from the lockfile, and passed the Python, .NET, and Angular checks. The external Qwen2.5 0.5B Instruct Q4_K_M model was supplied separately and verified as Apache-2.0, SHA-256 `74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db`.

With two Galaxy A07s and one Galaxy A06 (Android 16) on USB, the live dashboard reported 3/3 nodes Online and all three RPC worker processes Running. A streamed chat request through Angular → ASP.NET Core → Python → llama.cpp returned HTTP 200 and a non-empty answer. The user prompt was `Explain in one short sentence what a distributed system is.`; context was 512 tokens, temperature 0.2, and the generation limit was 80 tokens. The end-to-end request took 14.376 s; the client observed first content after 8,355.4 ms. llama.cpp evaluated 223 prompt tokens in 8.330 s and generated 28 tokens in 6.022 s (4.48 tokens/s), for 14.352 s total.

The same coordinator session recorded 27 `GRAPH_RECOMPUTE` operations in each of RPC0/RPC1/RPC2 worker logs. Model weights were allocated as 90/78/205 MiB on RPC0/RPC1/RPC2, with 0 MiB on the Mac Host. A nearby dashboard telemetry sample recorded CPU/available RAM/surface temperature of 14.1%/1,051 MiB/42.0 °C for the A06 and 18.5%/1,304 MiB/40.1 °C and 11.5%/1,248 MiB/38.0 °C for the two A07s; these are device snapshots, not inference-only utilization.

### Single-cluster admission and job-status smoke — 2026-11-16

With two Galaxy A07s and one Galaxy A06 connected over USB, the integrated launcher loaded Qwen2.5 0.5B Q4_K_M in 20 s. A request through the .NET `/v1/chat/completions` route returned `OK`; the response identified `single_model_distributed`, `llama.cpp-rpc`, and three selected ADB nodes. `GET /api/inference/jobs/{id}` returned the same workload and node list with state `completed`. The three worker logs each recorded one graph-recompute operation; model allocations were 90/78/205 MiB on RPC0/RPC1/RPC2 and 0 MiB on the Mac. The request took 1.77 s end to end (1.49 s prompt processing; 3.54 generated tokens/s). Sending `independent_replicas` returned HTTP 501 before inference, as intended. This is one short API smoke sample, not a benchmark or evidence of replica throughput. Ctrl+C stopped the coordinator, APIs, ADB bridge and all three workers.

At that clean-checkout point, 60 .NET tests passed and two PostgreSQL integration tests were skipped because the opt-in database was not configured; 40 Python tests and 16 Angular tests passed, the Angular production build succeeded, and `npm audit` reported no vulnerabilities. The .NET test SDK then in use (17.3.2) brought in an affected transitive Newtonsoft.Json version. The SDK has since been upgraded to 18.12.0; see the current local release checklist for the follow-up vulnerability scan. These are historical clean-checkout results, not the final validation for the present working tree.
