# Current architecture and verified baseline

**Last reviewed:** 2026-11-15
**Reference vision:** [Distributed AI Engine product vision](product-vision.md)
**Status:** Repository and recorded-run audit; not a claim that every run was reproduced during this review.

## Product and implementation today

The repository is still named `distributed-ai-agent`. It contains a working local prototype and several phase-specific experiments; it is not yet one packaged, backend-neutral inference engine.

```text
Angular SPA (cluster and chat routes)
              |
     same-origin HTTP / SSE
              v
ASP.NET Core platform (:5100)
  node registry | chat gateway | conversations | user-managed memories
       | ADB telemetry bridge (development host process)
       +----------------------> PostgreSQL (chat and memories)
              |
              | local HTTP contract
              v
Python chat/agent prototype (:8000)
  tool orchestration | provider protocol | cluster-status tool
              |
              | OpenAI-shaped local request
              v
llama.cpp llama-server on Mac (:8080)
       | RPC over USB ADB forwards
       +------ Android ggml-rpc-server workers
```

The diagram describes the current dashboard plus its intended/current chat integration. The guided experiment in `experiments/phase-3/start-mvp.sh` independently starts the llama.cpp model server and Python chat API; `tools/run-dashboard.sh` starts the .NET dashboard stack and ADB bridge. They are separate launch paths today, so a dashboard showing online devices is not evidence that the inference workers are ready or that they computed a request.

## Existing components

| Area | Present implementation | Current boundary/limitation |
| --- | --- | --- |
| Web client | Angular + TypeScript, lazy `/cluster` and `/chat` routes, streaming chat UI, conversation history and explicit memory-note controls | One local SPA; no authenticated user identity |
| Platform/control plane | ASP.NET Core minimal API; in-memory `NodeRegistry`; chat gateway to Python; conversation and memory endpoints | Heartbeat liveness and ADB-observed RPC process state are separate; no model/backend readiness, generic engine job, or scheduler API |
| AI/chat runtime | Python standard-library HTTP server; provider protocol; llama.cpp adapter; one read-only status tool | Prototype is app-oriented and not yet a stable public engine API; provider contract covers chat, not workload/capability negotiation |
| Inference | Upstream llama.cpp `llama-server` + `ggml-rpc-server` Android ARM64 workers; one GGUF distributed by RPC | Experimental, development scripts, USB ADB transport; RPC is unauthenticated |
| Android node telemetry | Mac-hosted Python ADB bridge reads CPU, available RAM and thermal service; hashes serials | It is a development bridge, not an Android-resident authenticated node agent; only listed Samsung models are accepted |
| Persistence | Dedicated local PostgreSQL container; chat snapshots and user-managed notes | No document/vector store; no multi-user tenancy |
| Packaging/operations | Shell launchers, experiment records, local build/test instructions, GitHub Actions CI for host-side checks | No versioned release artifact; components are not one installable product |

Relevant code: `apps/web/src/app/`, `src/dotnet/DistributedAI.Platform/`, `src/dotnet/DistributedAI.Contracts/`, `experiments/phase-3/python/inference/`, `experiments/phase-3/python/agent_runtime/`, `experiments/phase-0/`, `experiments/phase-1/`, and `tools/`.

## Evidence and confidence

### Verified from checked-in run records

- A single Qwen2.5 7B Q3_K_M model request completed over USB ADB with two Galaxy A07 and one Galaxy A06 (Android 16).
- The recorded 3-worker run shows model allocations on RPC0/RPC1/RPC2, zero MiB of model weights on the Mac Host, and nonzero `GRAPH_RECOMPUTE` operations in all three worker logs. Raw run logs are local and excluded from Git; the measurements are summarized in `experiments/phase-2/README.md`.
- Two-worker runs passed twice; a one-worker run failed during context setup for that model/configuration. In the two documented repeats, generation was 1.24 tokens/s with two workers and 1.12/1.14 tokens/s with three workers. These are small samples on a mixed-device cluster, not scaling guarantees.
- Phase 3's launcher explicitly selects `RPC0,RPC1,RPC2`, starts one model server, stores raw coordinator and worker logs, and emits an English summary. Its pass gate checks for a nonempty response, graph work on each worker, nonzero model allocations on all three RPCs, and zero Mac model-weight allocation.
- The ADB dashboard bridge detects supported devices, records online/offline transitions, and reports whether `ggml-rpc-server` has a process according to an ADB probe. This does not confirm RPC port reachability or that a requested model is loaded, so it is not inference readiness.

### Still unverified or incomplete

- The exact three-Galaxy-A07 hardware acceptance run; the available evidence uses two A07s and one A06.
- Wi-Fi/LAN inference transport; the owner has explicitly deferred it and selected USB for current experiments.
- A supported Android process/node agent that works independently of USB debugging.
- Whether the current model/API path is suitable as a stable, versioned engine API for external clients.
- Generalized placement across different hardware types, multiple clusters, independent model replicas, authentication/enrollment, job lifecycle and failure recovery.
- Model-only time-to-first-token and live per-device CPU/RAM/temperature correlated to the same inference request. The Python streaming runtime now records one runtime-observed first-content sample; it includes the request path and is not model-only TTFT.
- Reproduction of all historical measurements from a clean checkout on current hardware.
- A versioned release and contributor reproduction of the documented setup.

The recorded RPC evidence supports **single-model distributed execution on the current mixed three-phone cohort over USB**. It does not establish three A07 support, Wi-Fi support, fault-tolerant continuation after worker loss, or broad platform compatibility.

## Main architectural risks

1. **Capability claims can outrun evidence.** Dashboard `Online`, ADB visibility, RPC process state, RPC availability, and `ready-for-inference` are separate states and must not be conflated.
2. **Development tooling is part of today's runtime path.** USB forwarding and host-side worker startup are useful for the experiment but are not a portable node-enrollment design.
3. **The product boundary is currently blurred by naming and placement.** Agent/chat code is the current client and Python runtime, while the target product is the reusable inference engine. Preserve this code and introduce a seam; do not rename/rewrite first.
4. **Two languages and multiple processes need contract coverage.** .NET, Python, llama.cpp and browser streaming can fail independently. Existing unit tests do not substitute for a full request-path integration run.
5. **Experimental RPC is a security boundary.** It is unauthenticated and must remain loopback/USB or a trusted isolated network until authenticated transport is designed.
6. **Liveness and readiness differ.** A recent ADB heartbeat means the phone is connected; a running RPC process does not prove port availability or model readiness. The dashboard labels these observations without advertising inference readiness.
7. **Scaling modes differ.** Serving separate requests from independent model instances is not partitioning one model for one request; APIs, status and metrics need to say which mode occurred.
8. **Persistence scope is local-only.** PostgreSQL currently supports chat history and explicit memory notes, not multi-user authorization or engine job state.
9. **Hardware and model constraints are empirical.** RAM is not unified across phones; network latency and thermal behavior can make additional nodes slower.

## Latest integration validation

### Coordinator model readiness

The dashboard reads `llama-server /health` through the local Python runtime and displays coordinator status as ready, loading, or unavailable. This request touches only the host-local model server; it does not open extra connections to RPC worker ports. A ready coordinator is not proof of per-phone participation. The Python state mapping is tested for all three states, with .NET gateway and Angular display coverage. Physical unload/reload behavior remains unverified. See [ADR 0011](../adr/0011-report-coordinator-model-readiness.md).

The .NET gateway also exposes `/api/inference-status`. In the current fixed three-worker MVP, it reports distributed chat available only when the coordinator is ready and exactly three `adb-` nodes are `Online` with ADB-observed RPC processes `Running`; simulator nodes do not satisfy this hardware check. The chat and SSE endpoints return HTTP 503 before invoking Python when this guard fails, and Angular reports chat availability, coordinator state, and the number of ADB-observed active RPC processes separately; it does not claim per-phone model readiness. This is a minimum availability guard: process state does not prove the RPC channel is usable, and worker loss after the check can still interrupt a request. The standalone Python runtime on port 8000 bypasses this .NET gate. See [ADR 0013](../adr/0013-gate-chat-on-distributed-inference-readiness.md).

On 2026-11-15, after restarting only the .NET gateway and ADB bridge (the model server stayed up), the new endpoint reported chat admission `ready`, 3/3 ADB-observed active RPC processes, and the Qwen2.5 0.5B Q4_K_M coordinator. This does not claim per-device model readiness. The same status came through the Angular proxy, and a short non-streaming chat through `/api/chat` returned `OK`. This validates the gateway integration with the currently connected cohort (two Galaxy A07s and one Galaxy A06); it does not add a per-request worker attribution or a new performance benchmark. Automated checks cover 56 .NET tests (two PostgreSQL integration tests skipped without their opt-in environment) and 15 Angular tests. Machine-specific validation logs are local artifacts excluded from Git; the result is summarized here.

A controlled disconnect/reconnect check then exercised the guard. With one phone physically unplugged, the API reported `cluster_incomplete`, 2/3, and blocked `/api/chat` with HTTP 503. After reconnect, all three ADB nodes/processes returned online and readiness briefly reported `ready`, but the first chat failed with HTTP 502; the RPC coordinator logged a send failure and became unavailable. Restarting the three workers and coordinator restored RPC reachability, model readiness, and a successful `OK` chat in 9.28 s. The guard therefore rejects a known incomplete cluster, but process observations cannot detect a stale coordinator/RPC session until the actual request fails. Recovery remains manual. Machine-specific reconnection logs are local artifacts excluded from Git; the outcome and limitation are summarized here.

The Phase 3 launcher supervises coordinator exits with a bounded restart policy, waits for all three authorized USB phones before replacing workers, and publishes a monotonic session generation. The .NET gateway latches inference failures and only clears the degraded state after a newer generation reports ready; chat UI blocks resubmission while degraded and preserves the failed text in the composer without replaying it. Python, .NET and Angular tests pass. On 2026-11-15, the live hardware cycle completed with two Galaxy A07s and one Galaxy A06 over USB: a stale RPC session failed, chat became degraded, the supervisor brought up generation 2, and fresh API and Angular chat requests returned `OK`. Worker logs recorded graph computation on all three phones after recovery. This validates supervised process recovery and a new user-submitted request; it does not replay the failed request or continue generation from a checkpoint. Exact unplug-to-offline timing was not measured. See [ADR 0014](../adr/0014-supervised-rpc-session-recovery.md); machine-specific validation logs are local artifacts excluded from Git.

The provider-neutral request/response contract and llama.cpp adapter live in `experiments/phase-3/python/inference/`, separate from chat orchestration. The old Python import paths re-export the same classes for compatibility. On 2026-11-14, the existing Angular proxy returned an SSE response through ASP.NET Core, Python and the same three-worker llama.cpp RPC run. The integration exposed a concrete request-framing defect: .NET's streaming request used chunked transfer encoding, but the Python HTTP handler reads `Content-Length`. The .NET client now serializes JSON into buffered `ByteArrayContent`, used for both chat and streaming requests. The proxy smoke test returned `OK` with HTTP 200; a separately isolated Angular → .NET → Python integration also passed.

The recorded hardware cohort was two Galaxy A07s and one Galaxy A06 on USB, not three A07s. The successful inference used the single Qwen2.5 7B Q3_K_M model across RPC0/RPC1/RPC2. Per-worker logs show graph compute in all three devices during the session; counts are cumulative and are not attributed to the individual response. Run summaries and raw worker logs are local artifacts excluded from Git; the results and limitations are summarized here.

After adding the ADB process probe, a live API read reported all three nodes `Online` and all three worker processes `Running`. This confirms process observation on the available USB cohort; the stopped/start transition and model/backend readiness remain unverified.

A controlled process-only check on one Galaxy A07 then observed `Online/Stopped` → `Online/Running` → `Online/Stopped` → `Online/Running`; cleanup returned it to `Online/Stopped`. The test used the bridge's existing ADB observation, no port forwards, and no TCP probes. Raw logs are local and excluded from Git. This closes the process-state transition check for the tested phone; disconnect/reconnect timing remains open.

On 2026-11-15, two physically disconnected phones appeared `Offline/RPC-Unknown` while the remaining Galaxy A07 stayed `Online/RPC-Running` across three API samples over 10 seconds. Their heartbeats were already 30/37 seconds old at first observation, so unplug-to-offline latency cannot be inferred. After reconnection, all three returned to `Online/RPC-Running`, but the old coordinator's next chat request failed and the local health later became unavailable. A clean restart of the workers and coordinator restored `ready`; the 0.5B distributed chat returned `OK` in 9.10 s and each worker logged graph work. No automatic RPC-session recovery is demonstrated. Raw disconnect/recovery logs are local and excluded from Git.

The current run was restarted after removing a recurring TCP probe that correlated with ADB shell timeouts. The original Angular → .NET → Python → llama.cpp route returned `OK` again; the English run summary records a 130.73-second uncached prompt evaluation and a 1.79-second cached follow-up. The probe limitation and recovery evidence are recorded in [ADR 0010](../adr/0010-separate-node-liveness-from-rpc-process-health.md).

The next USB run validates the new readiness display through the live .NET and Angular proxies (`ready`) while all three ADB nodes report online and their RPC processes running. One streamed `OK` chat response then completed through Angular → .NET → Python → llama.cpp. The run's worker logs each recorded one `GRAPH_RECOMPUTE`; coordinator allocations were 1,068.55/1,060.02/1,274.39 MiB on RPC0/1/2 and 0 MiB host model weights. Prompt evaluation took 131.98 s for 234 tokens, generation 1.06 s for two tokens (0.94 tokens/s), and llama.cpp total was 133.05 s. Run summaries and raw worker logs are local artifacts excluded from Git; the measured result is summarized here. This current cohort remains two Galaxy A07s plus one Galaxy A06.

The follow-up measured the available 0.5B GGUF on the same USB cohort with the same short prompt and two-token output: 9.08 s total, 19 s model load, and one graph-work record on each phone; host model weights remained zero. The launcher now selects between this faster measured profile and the 7B profile at session startup, defaulting to the 0.5B. The chat and cluster show the same active model. This is one sample per model and does not compare answer quality. The decision and limits are in [ADR 0012](../adr/0012-cluster-scoped-model-profiles.md); raw measurements are local and excluded from Git.

On 2026-11-15, an exploratory same-model sweep selected 1, 2 and 3 RPC devices through the Phase 3 launcher. One fresh `Reply with only OK.` request per setting returned `OK` in 8.38/8.40/8.92 s. The model was Qwen2.5 0.5B Q4_K_M, context 512, on two Galaxy A07s and one Galaxy A06 over USB. The selected RPCs each logged graph work (1/0/0, 1/1/0, 1/1/1); model weights were 373 MiB across selected phones and 0 MiB on the Mac in every run. This is a single sample per setting, with no separate TTFT measurement; it does not show that more phones improve speed. The launcher control defaults to 3 and is not a scheduler. See [ADR 0015](../adr/0015-experimental-rpc-worker-count.md); raw comparison logs are local artifacts excluded from Git. The default three-RPC session was restarted afterward and the API returned ready at 3/3.


The hardware plan is phones first, other CPU platforms when available, and GPU hosts last. Only the abstraction boundary is prepared for those future additions; each one requires a specific adapter and tested support matrix.

The completed slice validated:

- the existing chat UI and stream working through the same request path;
- existing tool, history and explicit-memory behavior remaining intact;
- provider and HTTP contract tests passing with the fake provider;
- hardware smoke evidence for the three selected phone workers and host model allocation.

**Acceptance:** the current Angular chat streams through the existing distributed llama.cpp setup; existing tool, history and explicit-memory behavior remains covered; contract tests pass using a fake provider; an on-device run records phone worker evidence and coordinator allocation without reporting Mac model weights as phone work. These criteria are satisfied for the currently available mixed phone cohort. No agent framework, RAG, vector store, scheduler, GPU adapter, or new service was required for this increment.

The package boundary is implemented and covered by unit tests. Clean-checkout reproduction and the strict three-A07 hardware target remain open in Milestone 0; no wider hardware support is implied by this integration run. The current node-lifecycle slice exposes the ADB-observed RPC process separately from heartbeat liveness; it deliberately does not claim model readiness.
