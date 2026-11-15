# Phase 3: local chat and minimal agent

## Objective

Provide a local browser chat backed by one llama.cpp server that keeps the distributed GGUF loaded across turns, plus one allowlisted, read-only cluster-status tool.

## Scope and risks

- The llama.cpp server and the Python chat API bind only to 127.0.0.1.
- The model remains distributed over the active Phase 1 RPC workers.
- Chat history lives only in the browser tab; reloading clears it.
- The only agent tool calls the Phase 1 status command. It cannot run arbitrary commands or change device state.
- The model takes about 160 seconds to become ready, and generation is about 1 token/second on the current USB setup.
- Current devices are two Galaxy A07s and one Galaxy A06. USB is the selected transport; Wi-Fi evaluation is deferred by the owner. The exact three-A07 target remains open.
- Hardware priority is the available Android phones first, other CPU platforms when they are available for validation, and GPU hosts last. The inference contract is separated for future adapters, but only the current llama.cpp phone path exists today.

## Acceptance criteria

1. A local browser page can send a chat message and receive a non-empty answer without reloading the model between turns.
2. A request for cluster status invokes the allowlisted status tool, returns live worker state to the model, and displays the tool use.
3. The llama.cpp server uses RPC0/RPC1/RPC2 for model weights and logs graph calculation on all three phones, with zero Host model weights.
4. The HTTP services bind to loopback only.
5. Raw engine and worker logs stay unchanged; a separate final English summary is saved with the run.

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

Open http://127.0.0.1:8000 in a browser. Ctrl+C stops the chat API and model server and stops the workers started by the launcher. The session writes raw coordinator and worker logs plus a separate English summary with startup and request timings, worker graph counts, and weight allocation.

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

The exact target of three Galaxy A07s remains unverified. Wi-Fi is deferred by the owner. The prototype passed its chat, tool, loopback, and distributed-inference checks on the available two-A07 plus one-A06 cluster. Current validation: 35 Python tests passed; 48 .NET tests passed and 2 PostgreSQL integration tests were skipped; 14 Angular tests passed.

An exploratory profile comparison used one identical short prompt and a
two-token output on each local model: Qwen2.5 0.5B Q4_K_M returned in 9.08 s
with a 19 s load; Qwen2.5 7B Q3_K_M returned in 133.05 s with a 163 s load.
Each worker logged graph work for both runs and the Mac held 0 MiB of model
weights. The launcher therefore defaults to the fastest profile measured so
far, the 0.5B; the larger model remains selectable. See the [comparison
recorded result. The exact measurements are local artifacts excluded from Git. This single
sample per profile is not a repeated benchmark or a quality assessment.
A separate tool-selection smoke check showed the 0.5B model calling the
allowlisted cluster-status tool with a simulated callback; it did not validate
the reliability of tool selection or query the physical workers.

### Exploratory RPC worker-count sweep

On 2026-11-15, one fresh chat request per configuration used the same Qwen2.5
0.5B Q4_K_M, prompt, context and token limit, first with 1 RPC, then 2, then 3.
All three answered `OK`; total llama.cpp times were 8.38 s, 8.40 s and 8.92 s.
Each selected phone logged one `GRAPH_RECOMPUTE`, and the Mac held 0 MiB of
model weights. This is one sample per configuration, not enough to compare
performance. TTFT was not recorded separately. See the [English comparison
summary in this guide; raw device logs remain local and are excluded from Git. The selection control is experimental; it is
not scheduler placement or proof of per-phone model readiness.

### .NET gateway regression after inference-contract extraction

On 2026-11-14, the Angular chat proxy was exercised through the current .NET API, Python runtime and the same llama.cpp model server/RPC workers. It returned the streamed answer `OK` with HTTP 200. The end-to-end HTTP request took about 5.2 seconds. A separate isolated run of the same four-component path also returned HTTP 200 in 5.63 seconds.

This regression found and fixed an HTTP framing mismatch. `PythonAgentClient` had supplied JSON using chunked transfer encoding, while the Python HTTP handler expects a valid `Content-Length`; requests were rejected with HTTP 400. The .NET client now sends buffered JSON with an explicit length for both regular and streaming chat calls. The change does not alter the provider contract or the llama.cpp execution setup.

The corresponding llama.cpp request used a mostly cached prompt: task 9 evaluated one prompt token in 1.12 seconds and generated two tokens in 0.90 seconds (1.12 tokens/s), for 2.01 seconds total in llama.cpp. A prior task in the same run evaluated seven prompt tokens and generated two in 5.55 seconds total. Do not compare these as equivalent fresh prompts. Over the session, each worker log contained five `GRAPH_COMPUTE` records; these are cumulative session totals and are not a per-response counter. The workers were two Galaxy A07s and one Galaxy A06 over USB. Per-worker allocations were 1,068.55, 1,060.02 and 1,274.39 MiB; the coordinator memory breakdown reported 0 MiB of host model weights (with host-mapped and compute buffers reported separately).

The following live readiness smoke test returned `ready` through `/api/model-status` and through the Angular proxy while all three ADB nodes were online with their RPC processes running. A streamed chat call through Angular → .NET → Python → llama.cpp returned `OK`. For that request, each of the three worker logs recorded one `GRAPH_RECOMPUTE`; RPC allocations were 1,068.55/1,060.02/1,274.39 MiB and host model weights were 0 MiB. Prompt evaluation took 131.98 s for 234 tokens; generation took 1.06 s for two tokens at 0.94 tokens/s; total llama.cpp time was 133.05 s. The English summary and raw logs are local artifacts excluded from Git; the measured outcome is recorded in this guide. This is a mixed cohort (two A07, one A06), not the three-A07 acceptance target.

The .NET suite passed 47 tests and the focused Python provider/server suites passed 19 tests for this integration change. Run summaries and raw coordinator/worker logs are local artifacts excluded from Git. This validates the current local integration only; it does not close the clean-checkout or three-A07 acceptance items.

After the follow-up RPC-probe experiment was removed, the original launcher restarted the same mixed USB cohort. Model startup took about 160 seconds; the Angular → .NET → Python → llama.cpp stream again returned `OK` (HTTP 200). The uncached 235-token prompt took 130.73 seconds to evaluate; generation took 0.88 seconds at 1.14 tokens/s. A cached follow-up took 1.79 seconds in llama.cpp. The same worker allocations were observed (1,068.55, 1,060.02 and 1,274.39 MiB; host model weights 0 MiB). Run summaries and raw coordinator/worker logs are local artifacts excluded from Git.
