# Repository Structure and Architectural Boundaries

This document describes the current repository layout for review. It reflects the implementation that exists today; future platform, GPU, RAG, and agent capabilities are not represented as implemented modules.

## Complete source tree

```text
.
├── apps/
│   └── web/
│       ├── .vscode/
│       │   ├── extensions.json
│       │   ├── launch.json
│       │   └── tasks.json
│       ├── public/
│       │   └── favicon.ico
│       ├── src/
│       │   ├── app/
│       │   │   ├── features/
│       │   │   │   ├── chat/
│       │   │   │   │   ├── services/
│       │   │   │   │   │   └── chat-api.service.ts
│       │   │   │   │   ├── chat-page.html
│       │   │   │   │   ├── chat-page.spec.ts
│       │   │   │   │   └── chat-page.ts
│       │   │   │   └── cluster/
│       │   │   │       ├── services/
│       │   │   │       │   └── cluster-api.service.ts
│       │   │   │       ├── cluster-page.html
│       │   │   │       ├── cluster-page.spec.ts
│       │   │   │       └── cluster-page.ts
│       │   │   ├── shared/
│       │   │   │   └── sse.ts
│       │   │   └── shell/
│       │   │       ├── app.config.ts
│       │   │       ├── app.css
│       │   │       ├── app.html
│       │   │       ├── app.routes.ts
│       │   │       ├── app.spec.ts
│       │   │       └── app.ts
│       │   ├── index.html
│       │   ├── main.ts
│       │   └── styles.css
│       ├── .editorconfig
│       ├── .gitignore
│       ├── .prettierrc
│       ├── angular.json
│       ├── package-lock.json
│       ├── package.json
│       ├── proxy.conf.json
│       ├── README.md
│       ├── tsconfig.app.json
│       ├── tsconfig.json
│       └── tsconfig.spec.json
├── docs/
│   ├── adr/
│   │   ├── 0001-single-model-distributed-inference.md
│   │   ├── 0002-hybrid-platform-and-ai-stack.md
│   │   ├── 0003-usb-first-worker-transport.md
│   │   ├── 0004-loopback-and-tool-policy.md
│   │   ├── 0005-mac-hosted-adb-telemetry-bridge.md
│   │   ├── 0006-angular-feature-routes.md
│   │   ├── 0007-user-managed-agent-memory.md
│   │   ├── 0008-engine-core-and-optional-applications.md
│   │   ├── 0009-separate-inference-contract-from-chat-runtime.md
│   │   ├── 0010-separate-node-liveness-from-rpc-process-health.md
│   │   ├── 0011-report-coordinator-model-readiness.md
│   │   ├── 0012-cluster-scoped-model-profiles.md
│   │   ├── 0013-gate-chat-on-distributed-inference-readiness.md
│   │   ├── 0014-supervised-rpc-session-recovery.md
│   │   ├── 0015-experimental-rpc-worker-count.md
│   │   ├── 0016-model-agnostic-android-discovery.md
│   │   ├── 0017-loopback-openai-shaped-inference-preview.md
│   │   └── 0018-single-cluster-workload-admission.md
│   ├── architecture/
│   │   ├── current-state.md
│   │   ├── product-vision.md
│   │   ├── repository-structure.md
│   │   └── target-state.md
│   └── architecture.md
├── examples/
│   └── clients/
│       └── stream_completion.py
├── experiments/
│   ├── phase-0/
│   │   ├── bootstrap-llama.sh
│   │   ├── build-android.sh
│   │   ├── build-mac.sh
│   │   ├── README.md
│   │   ├── Run larger model USB experiment.command
│   │   ├── Run Phase 0 USB experiment.command
│   │   ├── Run Wi-Fi experiment.command
│   │   ├── run-inference.sh
│   │   └── run-usb-experiment.sh
│   ├── phase-1/
│   │   ├── clusterctl.sh
│   │   └── README.md
│   ├── phase-2/
│   │   ├── benchmark-workers.sh
│   │   ├── infer-distributed.sh
│   │   └── README.md
│   └── phase-3/
│       ├── python/
│       │   ├── agent_runtime/
│       │   │   ├── __init__.py
│       │   │   ├── contracts_v1.py
│       │   │   ├── llama_cpp_provider.py
│       │   │   └── runtime_v1.py
│       │   ├── api/
│       │   │   ├── __init__.py
│       │   │   ├── chat_server.py
│       │   │   └── index.html
│       │   └── inference/
│       │       ├── __init__.py
│       │       ├── contracts_v1.py
│       │       └── llama_cpp_provider.py
│       ├── README.md
│       └── start-mvp.sh
├── infra/
│   ├── postgres/
│   │   ├── 001_chat_history.sql
│   │   └── 002_agent_memories.sql
│   ├── .env.example
│   └── compose.yaml
├── src/
│   └── dotnet/
│       ├── DistributedAI.Contracts/
│       │   ├── DistributedAI.Contracts.csproj
│       │   └── Nodes.cs
│       └── DistributedAI.Platform/
│           ├── Api/
│           │   └── Endpoints/
│           │       ├── AgentMemoryEndpoints.cs
│           │       ├── ChatConversationEndpoints.cs
│           │       ├── ChatEndpoint.cs
│           │       └── InferenceApiEndpoints.cs
│           ├── Application/
│           │   ├── Cluster/
│           │   │   └── NodeRegistry.cs
│           │   ├── Inference/
│           │   │   ├── InferenceJobStore.cs
│           │   │   ├── InferenceReadiness.cs
│           │   │   └── InferenceWorkloadScheduler.cs
│           │   └── Ports/
│           │       ├── IAgentMemoryStore.cs
│           │       ├── IChatConversationStore.cs
│           │       ├── IPythonAgentClient.cs
│           │       ├── IPythonInferenceClient.cs
│           │       └── StorageUnavailableException.cs
│           ├── Infrastructure/
│           │   ├── AgentRuntime/
│           │   │   └── PythonAgentClient.cs
│           │   ├── InferenceRuntime/
│           │   │   └── PythonInferenceClient.cs
│           │   └── Persistence/
│           │       ├── AgentMemoryStore.cs
│           │       ├── ChatConversationStore.cs
│           │       └── PersistenceExceptionMapper.cs
│           ├── DistributedAI.Platform.csproj
│           └── Program.cs
├── tests/
│   ├── dotnet/
│   │   └── DistributedAI.Platform.Tests/
│   │       ├── Api/
│   │       │   ├── AgentMemoryEndpointTests.cs
│   │       │   ├── ChatConversationEndpointTests.cs
│   │       │   └── InferenceApiEndpointTests.cs
│   │       ├── Application/
│   │       │   ├── Cluster/
│   │       │   │   └── NodeRegistryTests.cs
│   │       │   └── Inference/
│   │       │       ├── InferenceJobStoreTests.cs
│   │       │       ├── InferenceReadinessTests.cs
│   │       │       └── InferenceWorkloadSchedulerTests.cs
│   │       ├── Infrastructure/
│   │       │   ├── PythonAgentClientTests.cs
│   │       │   └── PythonInferenceClientTests.cs
│   │       └── DistributedAI.Platform.Tests.csproj
│   └── python/
│       ├── __init__.py
│       ├── test_adb_node_bridge.py
│       ├── test_chat_server.py
│       ├── test_local_mvp_launcher.py
│       ├── test_inference_smoke_client.py
│       └── test_llama_cpp_provider.py
├── tools/
│   ├── DistributedAI.NodeSimulator/
│   │   ├── DistributedAI.NodeSimulator.csproj
│   │   └── Program.cs
│   ├── adb_node_bridge.py
│   ├── phase-1-smoke.sh
│   ├── run-dashboard.sh
│   ├── run-local-mvp.sh
│   └── start-postgres.sh
├── .gitignore
├── CODE_OF_CONDUCT.md
├── CONTRIBUTING.md
├── DistributedAI.slnx
├── LICENSE
├── README.md
├── SECURITY.md
└── THIRD_PARTY_NOTICES.md
```

Generated dependencies and local-only artifacts are omitted: `node_modules/`, .NET `bin/` and `obj/`, Angular `dist/`, Python caches, downloaded model weights, upstream llama.cpp checkout/builds, runtime state, local environment files, internal development roadmap/release checklist, and experiment result logs. These are excluded from the public source tree.

## Dependency direction

```text
Angular feature pages
        │
        ├── ChatApiService ── HTTP + SSE ──┐
        └── ClusterApiService ─ HTTP ──────┤
                                           ▼
                                  .NET HTTP endpoint adapters
                                           │
                                      Application
                                     /           \
                              Contracts       Ports
                                                ▲
                                                │
                                      Infrastructure adapters
                                       ├── Python agent/inference HTTP clients
                                       └── PostgreSQL stores

Python chat adapter ── Agent runtime ── Inference provider contract ── llama.cpp adapter
ASP.NET `/v1` adapter ── Python inference HTTP client ── Python provider API
```

The .NET project is a small modular monolith with Clean Architecture/ports-and-adapters boundaries: HTTP endpoints and infrastructure depend inward on application behavior and contracts; application ports describe required external capabilities; concrete PostgreSQL and Python HTTP adapters implement those ports. `Program.cs` is the composition root. Minimal API endpoint modules are the HTTP adapter; MVC controllers would add ceremony without a current need for controller-specific behavior.

There is no separate .NET Domain project today. The current node lifecycle and inference-readiness rules are small application use cases, while wire-level records/enums live in `DistributedAI.Contracts`. A dedicated domain model/project should be introduced only when invariants or domain behavior become substantial enough to justify it.

Angular uses route-level feature folders. Components own presentation and UI state; feature `services` folders own network operations, DTOs, and stream handling. Shared code is limited to behavior genuinely used across features.

The Python code is an experimental AI runtime scoped to Phase 3. `api` handles HTTP/SSE and adapts requests, `agent_runtime` contains the single supported tool and chat orchestration, and `inference` isolates the provider contract from the llama.cpp-specific HTTP implementation. It is not yet a general public inference engine SDK or a separate production service.

## Test layout

- `tests/dotnet/DistributedAI.Platform.Tests/Api/` covers endpoint behavior.
- `tests/dotnet/DistributedAI.Platform.Tests/Application/` covers cluster lifecycle and readiness rules.
- `tests/dotnet/DistributedAI.Platform.Tests/Infrastructure/` covers the Python HTTP adapter and failure mapping.
- `tests/python/` covers Python provider/runtime/API behavior and the ADB bridge.
- Angular component tests remain beside their feature and shell source so UI behavior is easy to find.

## Local execution boundaries

All current services bind to loopback by default. The Android worker RPC path is USB ADB port forwarding; Wi-Fi transport is deferred. The Mac hosts the coordinator/control plane and the model file, while the llama.cpp RPC workers perform distributed model work. This structure does not imply GPU, Windows, Linux, macOS worker, or multiple-cluster support today.
