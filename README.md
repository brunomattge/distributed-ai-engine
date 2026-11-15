# Distributed AI Engine

A platform for distributed AI inference across connected hardware, starting with Android RPC workers. The existing chatbot is its first application; RAG and AI agents are optional applications built on the engine.

## Product direction

The product and architecture reference is [Distributed AI Engine — Product Vision & Architecture RFC 001](docs/architecture/product-vision.md). The repository name and working components are preserved while the project evolves incrementally. The [current-state audit](docs/architecture/current-state.md) distinguishes checked-in evidence from open hardware and integration work. The [repository structure](docs/architecture/repository-structure.md) documents the source tree and dependency boundaries.

## Feature status

| Capability | Status | Notes |
| --- | --- | --- |
| Single-model inference distributed across Android RPC workers | **Experimental / demonstrated** | One GGUF was measured over USB on two Galaxy A07s and one Galaxy A06; three A07s and Wi-Fi remain unverified/deferred |
| Cluster dashboard and USB device monitoring | **Implemented locally** | ADB liveness, telemetry, and observed RPC-process state; neither online nor process-running means model inference-ready |
| Chat application with streaming | **Implemented locally** | Angular chat through the local platform/runtime and llama.cpp path |
| Conversation history and explicit memory notes | **Implemented locally** | Existing chatbot application features, not engine state |
| Python provider adapter and read-only cluster-status tool | **Experimental** | Local prototype; not a stable public engine API or general agent platform |
| Backend-neutral versioned inference API | **Planned** | First migration target; preserve current chatbot behavior |
| OpenAI-compatible public API | **Planned** | Interoperability goal subject to contract tests |
| Authenticated LAN nodes, additional operating systems, multiple clusters | **Planned** | Progressive support after real adapter/security validation |
| Embeddings | **Planned / optional capability** | Separate model/backend acceptance; no required vector database in engine |
| RAG application | **Planned / optional application** | Post-core; storage/indexing remains outside engine |
| Tool-using agent application | **Experimental now; optional future application** | Current chatbot has one read-only tool; side effects require future controls |
| Fine-tuning | **Out of scope** | No current MVP commitment |

The target stack is:

- **Angular + TypeScript** for the web application.
- **ASP.NET Core / C#** for the main platform and control plane: APIs, device registry, health, policy, and persistence.
- **Python** for AI-specific runtime and provider integration.
- **llama.cpp RPC** for the experimental Android ARM64 inference adapter.
- **PostgreSQL** for existing chatbot history/memory; **pgvector is optional** and belongs to a future RAG application if that application selects it.
- **OpenTelemetry and other infrastructure** only when a stable event/metric requirement justifies them.

Hardware will be validated progressively: the available Android phones come first; other CPU platforms follow when hardware is available; GPU hosts are last. The adapter boundary is being prepared for those possibilities, but only platforms with their own build/run evidence will be listed as supported.

## Current validated prototype

The current local prototype uses a small Python API and browser UI to validate chat and one read-only cluster-status tool. It starts one Qwen2.5 7B Q3_K_M model through llama.cpp and distributes its weights and graph work across USB-connected Android workers.

**Validated hardware:** macOS, two Samsung Galaxy A07s and one Galaxy A06 (Android 16), USB ADB. The three phones each performed graph work and held model weights; the Mac held 0 MiB of model weights. The required target of three A07s remains unverified. Wi-Fi transport tests are deferred by the owner; current inference experiments use USB.

Inference is experimental and slow on this setup: model startup takes about 160 seconds, and generation measured around 1 token/s. Keep RPC on USB or a trusted isolated LAN. The RPC protocol has no authentication and must not be exposed to the internet.

## Requirements

- Mac with Xcode Command Line Tools, CMake, Git, Python 3, `curl`, `nc`, and ADB.
- .NET 10 SDK for the current ASP.NET Core platform slice.
- Node.js and npm versions supported by the selected Angular release.
- Android SDK platform tools and three authorized Android phones connected over USB.
- Android NDK to build the phone worker.
- A compatible local GGUF model. Model files are not included; review the selected model's license before downloading or sharing it.

## Build and run

From the project root:

```sh
./experiments/phase-0/bootstrap-llama.sh
./experiments/phase-0/build-mac.sh
./experiments/phase-0/build-android.sh
```

Connect and authorize all three phones, then launch the MVP in one Terminal window:

```sh
cd experiments/phase-3
./start-mvp.sh
```

The default model path is `experiments/phase-0/models/qwen2.5-7b-instruct-q3_k_m.gguf`. To use another model, pass its path as the first argument. Open [http://127.0.0.1:8000](http://127.0.0.1:8000). Press Ctrl+C in Terminal to stop the chat, model server, workers started by the launcher, and USB forwards.

See [Phase 0](experiments/phase-0/README.md), [Phase 1](experiments/phase-1/README.md), [Phase 2](experiments/phase-2/README.md), and [Phase 3](experiments/phase-3/README.md) for prototype details and evidence. [Architecture decisions](docs/adr/) explain why the prototype and product target use these boundaries.

## Phase 1 dashboard preview

Install the Angular dependencies once, build the .NET solution, then start the API, three simulated nodes, and Angular dashboard together in one Terminal:

```sh
cd apps/web && npm ci
cd ../..
dotnet build DistributedAI.slnx
./tools/run-dashboard.sh
```

Open [http://127.0.0.1:4200/cluster](http://127.0.0.1:4200/cluster) for the cluster overview or [http://127.0.0.1:4200/chat](http://127.0.0.1:4200/chat) for the agent chat. The root URL redirects to the cluster overview. Dashboard simulator data is labeled and does not represent phone measurements. Press Ctrl+C to stop all three processes.

For live ADB telemetry instead of simulated nodes, connect supported Samsung devices over USB and run `./tools/run-dashboard.sh adb`. The bridge reads Android `/proc/stat`, `/proc/meminfo`, and `dumpsys thermalservice`, then sends heartbeats to the loopback API. When a successful ADB scan no longer lists a phone, the bridge immediately tells the API; the six-second heartbeat timeout remains as a fallback. It hashes ADB serials for stable local node IDs, excludes unsupported models, and never logs raw serials. Current connected hardware is two Galaxy A07s plus one Galaxy A06; that is a useful physical test, but it does not satisfy the three-A07 target. The “ADB collection” metric is command collection time, not Wi-Fi or inference RPC latency.

### Local PostgreSQL for conversation persistence

The platform's conversation API stores message snapshots in a dedicated project container. It does not reuse another Docker container or volume. Docker must be running. Start the database once from the project root:

```sh
./tools/start-postgres.sh
```

The script creates a random local password in the ignored file `infra/.env` (permissions `0600`), creates the project-only volume `distributed-ai-postgres-data`, and binds PostgreSQL only to `127.0.0.1:5432`. `./tools/run-dashboard.sh` loads this local configuration for the API. The schema is applied automatically when the project's database volume is first initialized. Stop the container while keeping its data with:

```sh
docker compose --env-file infra/.env -f infra/compose.yaml stop postgres
```

The conversation endpoints are `GET /api/conversations`, `GET /api/conversations/{id}`, and `PUT /api/conversations/{id}`. The memory endpoints are `GET /api/memories`, `POST /api/memories`, and `DELETE /api/memories/{id}`. The Angular chat persists completed message snapshots, resumes the last conversation, lists up to 50 recent conversations, and can start a new chat without deleting earlier ones. On the chat route, users can save up to 10 explicit memory notes; the .NET API adds them to both normal and streaming chat requests, and the Python runtime treats them as untrusted context. RAG remains future work.

To build and validate the current .NET platform slice without phones or a model:

```sh
dotnet build DistributedAI.slnx
dotnet test DistributedAI.slnx --no-build
./tools/phase-1-smoke.sh
```

The project license is MIT; third-party materials keep their own terms as recorded in [Third-party notices](THIRD_PARTY_NOTICES.md).

## Tests

Run the local API tests without phones or a model:

```sh
python3 -m unittest discover -s tests -v
```

The release checks also include shell syntax checks for the launch/build scripts and Python bytecode compilation. End-to-end model inference requires the connected devices.

Run the Angular checks from `apps/web`:

```sh
npm run build
npm test -- --watch=false
```

## Project license

The project uses the [MIT license](LICENSE) for original code. The pinned llama.cpp source and downloaded model have separate terms; see [third-party notices](THIRD_PARTY_NOTICES.md). The source repository does not bundle model weights or the upstream checkout.
