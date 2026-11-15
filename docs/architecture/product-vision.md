# Distributed AI Engine — Product Vision & Architecture Evolution

**Status:** Architecture proposal / RFC 001
**Origin:** Existing `distributed-ai-agent` project
**Target:** Open-source, hardware-agnostic distributed AI inference platform with optional RAG and agent applications
**Document date:** 2026-11-14

## 1. Executive Summary

We have a working prototype currently called **Distributed AI Agent**. It offers a chatbot and reports three Samsung Galaxy A07 smartphones (4 GB physical RAM each) participating when a prompt is submitted. The dashboard also shows node online/offline status when USB connections are interrupted.

We want to evolve this existing project into **Distributed AI Engine**, a reusable, extensible platform for orchestrating compute and AI inference across heterogeneous devices. The chatbot remains the first client of the engine. RAG pipelines and agent-based applications are optional clients, not dependencies or core identity of the platform. **LLM inference is a core product capability**; RAG and agents are planned extensions.

**Non-negotiable:** Inspect and preserve the existing implementation. Do not rewrite or rename the repository before proposing a compatible migration plan. The reported multi-device behavior is a working user observation; verify whether it is truly *single-model distributed inference*, concurrent independent requests, or another form of coordination using instrumentation.

## 2. Product Thesis

> Turn available computing devices into a managed, observable, secure pool of resources capable of running compatible AI workloads and, where supported, partitioning one model across multiple nodes.

The platform should eventually connect Android, Linux, Windows, macOS, ARM single-board computers, and CPU/GPU servers, subject to actual runtime support. The design must not claim that arbitrary hardware can perform arbitrary inference operations.

### What it is

- A **distributed computation and inference platform** with explicit control-plane and execution-plane boundaries.
- A resource-aware scheduler and node lifecycle manager.
- A set of adapters for compatible inference backends and hardware.
- An open platform exposing documented APIs for LLM inference, chatbots, optional agents, RAG applications, batch jobs, and external integrations.

### What it is not

- Not necessarily an autonomous AI agent.
- Not a guaranteed shared-memory machine: 3 x 4 GB RAM is **not** one unified 12 GB pool.
- Not a promise that adding devices makes single-request inference faster.
- Not a guarantee of hot-removing a node mid-generation and continuing seamlessly.
- Not a reason to build Kubernetes, microservices, or a custom inference kernel prematurely.

## 3. Current State — Verify Before Changes

**Reported as working:**

- A chatbot receives prompts and returns generated answers.
- Three Galaxy A07 devices participate in the request according to the current implementation.
- A dashboard reports connectivity changes when USB cables are disconnected.

**Unknown; investigate in repository and runtime:**

- Whether model weights/layers/tensors are partitioned across devices for a **single** inference request.
- Whether the host machine performs meaningful inference computation.
- Which inference runtime and transport are actually used.
- Whether online/offline means ADB/USB connectivity, process heartbeat, RPC availability, or genuine inference readiness.
- Available hardware telemetry and benchmark accuracy.
- Whether disconnection/reconnection is supported during an active inference.

Do not present any unverified capability as proven.

## 4. Product Boundaries

### Core platform: Distributed AI Engine

Responsibilities:

- Node enrollment, authentication, identity, registration, and lifecycle.
- Hardware and capability discovery (CPU architecture, physical/usable RAM, supported accelerators, backend version, inference capabilities).
- Node health/readiness and telemetry.
- Cluster membership, scheduling, placement, resource reservations, and workload routing.
- Model artifact management and compatibility validation.
- Execution orchestration through adapters (not hardcoded to a single device or engine).
- Job states, bounded retries, cancellation, failure reporting, metrics, logs, traces.
- Public API for model inference, with stable contracts, streaming, errors and clear versioning.
- Model catalog, quantization compatibility metadata and lifecycle management based on real runtime capabilities.

### Applications built on top

- Chatbot (existing application and first example client).
- Optional AI Agent application with tools and memory.
- Reference RAG application for document ingestion, retrieval and grounded answers (planned).
- Reference AI agent application with controlled tools, approvals and memory (planned).
- Code assistant, batch inference, embeddings and other specialized clients (optional).

**Architecture principle:** Agent behavior belongs to an optional application/service layer; the engine must not depend on an agent framework.

## 5. LLM, RAG, and AI Agent Scope

The project should explicitly communicate three distinct layers rather than using **LLM**, **RAG**, and **agent** interchangeably.

### 5.1 LLM inference — CORE

**What it is:** loading supported models and producing outputs from prompts, with distributed execution across compatible nodes when the chosen runtime can genuinely support it.

Core responsibilities and planned interfaces:
- Inference endpoints for text generation/chat, including streaming and cancellation.
- Backend-neutral model identifiers, runtime capability checks, model status, and clear error reporting.
- Model lifecycle: selecting model artifacts, verifying checksums, loading/unloading where supported, and reporting quantization/context constraints.
- Observability for per-request and per-node participation, latency, time to first token, tokens/second, memory, backend, and network conditions.
- A small, documented external API. **OpenAI-compatible chat completions** is a desirable interoperability goal, not a prerequisite or a claim of existing compatibility.
- Clear distinction between (a) one model partitioned across several nodes and (b) multiple independent replicas serving separate requests.

For the first supported models, prefer reproducible GGUF-based experiments with a small, appropriately licensed model and expand only after testing memory, backend and model compatibility. Model size targets are experimental, not promises of support.

### 5.2 Embeddings — OPTIONAL INFERENCE CAPABILITY

Embedding generation may later be exposed by the engine if supported by an appropriate model/runtime adapter. Embeddings do **not** imply that the core platform must own a vector database, document store or indexing pipeline.

Any embeddings endpoint requires its own evaluation, licensing, performance, and model-compatibility tests. It is not part of the initial migration.

### 5.3 RAG — OPTIONAL REFERENCE APPLICATION

**RAG = Retrieval-Augmented Generation**: retrieve relevant context from indexed sources and supply it to the LLM to produce grounded answers.

Planned reference workflow:

```text
Documents -> ingestion -> chunking -> embeddings -> vector/keyword index
                                                       |
User question -> retrieval -> contextual prompt -> Engine LLM API -> response with sources
```

Rules:
- Build a `rag-example` or equivalent optional module outside the engine core.
- Prefer a replaceable storage/retrieval interface. PostgreSQL + pgvector is a possible implementation, **not** a mandatory engine dependency.
- Support attribution to retrieved documents and clearly identify retrieval failures.
- Address untrusted document content/prompt injection and permissions for retrieval.
- Measure retrieval quality and answer grounding; do not advertise RAG as inherently accurate.
- RAG belongs in a **post-core MVP milestone**, not in the initial three-device inference validation.

### 5.4 AI Agents — OPTIONAL REFERENCE APPLICATION

An agent is more than a conversational LLM: it plans or selects actions and invokes tools under explicit control.

Planned agent example:
- Use the same engine inference API as the chatbot.
- Implement narrow, allowlisted tools (e.g., `get_cluster_status`, or read-only repository inspection).
- Limit tool permissions, time/budget, retries and execution depth.
- Require human approval for filesystem writes, shell commands and consequential actions.
- Keep conversation memory and tool audit logs in the application boundary.
- Treat tool-calling reliability as model-dependent and evaluate it empirically, particularly with small quantized LLMs.

Do not put LangGraph, LangChain, LlamaIndex, a vector database or any agent framework in the core merely to justify the project's name or its future roadmap.

### 5.5 API and Product Positioning

**Core:** distributed AI inference, node orchestration, runtime adapters, model management and metrics.

**First app:** existing chatbot, maintained throughout migration.

**Later examples:** RAG document Q&A, tool-using agent, code assistant and third-party integrations.

Suggested repository short description:

> Open-source distributed AI inference engine for heterogeneous devices, with extensible APIs for LLMs, RAG and agents.

The README must separate **Implemented / Experimental / Planned** capabilities. The short repository description is product positioning, not proof that every platform or feature is currently supported.

---

## 6. Target Architecture

```text
Applications / SDKs
  Chat UI | RAG App | Agent App | REST clients | Batch API
                    |
             Public API / Gateway
                    |
         Control Plane / Orchestrator
   Node Registry | Capability Discovery | Scheduler
   Cluster Manager | Model Catalog | Job Manager
   Auth / Policy  | Telemetry | Audit
                    |
         Runtime Abstraction / Adapters
          |             |             |
    llama.cpp RPC   Local runtime   Future adapters
          |             |             |
               Execution Plane
     Android | macOS | Linux | Windows | GPU Servers
```

Use a **modular monolith for the control plane initially**, with clearly defined module boundaries; separate services only for a demonstrated operational need. Preserve existing technology choices where they work. Python is appropriate for inference/AI-specific integration; introducing .NET is optional and must be justified, not required by the diagram.

### Node Agent (cross-platform concept)

Each node runs the appropriate native agent/adapter and reports a versioned capability contract such as:

```json
{
  "nodeId": "galaxy-a07-01",
  "platform": "android",
  "architecture": "arm64",
  "totalPhysicalMemoryMb": 4096,
  "availableMemoryMb": 1700,
  "capabilities": ["health", "telemetry", "cpu-inference", "llama-rpc"],
  "runtime": {"name": "llama.cpp", "version": "to-be-detected"},
  "status": "ready"
}
```

Numbers and runtime names above are **illustrative**, not measured device facts. Capabilities must be detected and validated rather than guessed.

### Control Plane vs Execution Plane

- **Control plane:** enrolls, monitors, schedules and coordinates resources.
- **Execution plane:** performs actual inference on capable devices.
- An `online` node may not be `ready-for-inference`; model compatibility, memory, thermal state and runtime health should be independently reported.
- Keep communication transport distinct from inference backend (USB/ADB may be development-only; supported Wi-Fi/LAN operation is an explicit goal).

## 7. Three Distinct Scaling Modes

**A. Scale-out / throughput:** Deploy multiple independent inference instances or groups of compatible nodes and distribute separate requests. Useful for more simultaneous users.

**B. Single-model distributed inference:** Partition one model across compatible nodes, if the backend supports it. Useful for fitting models or experimenting with parallel compute; network latency and synchronization may reduce speed. A three-node demo is not proof that scaling to hundreds of nodes is efficient.

**C. Functional extensibility:** Support chat, agents, embeddings, vision and other workloads using separate plugins and appropriate runtimes. Do not assume every node supports every workload.

The scheduler must distinguish workload types and routing policies. Avoid conflating load balancing of independent requests with partitioning one inference.

## 8. Key Engineering Decisions to Explore

1. **Connection independence:** Progress from USB/ADB-specific visibility to optional authenticated network connectivity over local Wi-Fi/LAN, without discarding existing USB workflow.
2. **Capability-based scheduling:** Match workload requirements with node architecture, memory budget, runtime/accelerator support, topology and current load.
3. **Cluster membership:** Node enrollment, heartbeats, liveness vs readiness, lease expiry, reconnection, drain and removal.
4. **Fault handling:** Timeouts, cancellation, explicit failure states, re-queuing when safe, and limitations of restarting single-model distributed inference after a node is lost.
5. **Security:** Authenticated enrollment, trusted networks, least privilege, encrypted control traffic when appropriate, no public exposure of experimental inference RPC endpoints, no arbitrary code execution from untrusted nodes.
6. **Model placement:** Explicit model version/hash, artifacts, memory estimates, compatibility validation, placement logs and cleanup.
7. **Observability:** Time to first token, output tokens/sec, total latency, node CPU/available memory, communication time, queue depth, successful/failed requests; metrics must show which nodes actually executed operations.
8. **API stability:** Backend-neutral inference API; versioned node capability schemas; a small SDK may follow once contracts stabilize.
9. **Hardware heterogeneity:** Android ARM CPUs, desktops and GPU hosts may require different backends; don't claim one runtime uniformly supports them all.
10. **Cost and performance:** Compare one, two and three Galaxy A07 nodes, repeat trials, record model/context/quantization/backend/network and host participation.

## 9. Incremental Roadmap — Not a Big-Bang Rewrite

### Milestone 0 — Architecture audit and baseline

- Read the repository and map its actual components, dependencies and execution flow.
- Reproduce the current chat request end-to-end.
- Trace where inference runs and measure participation of each Galaxy.
- Establish a reproducible benchmark baseline and record known limitations.
- Produce one current-state architecture diagram and a risks list.

**Exit criteria:** Evidence-backed description of what exists; successful regression run; clear distinction between verified features and assumptions.

### Milestone 1 — Decouple platform from chatbot/agent

- Define an inference service interface used by the current chatbot.
- Extract control-plane concepts into modules, reusing current code.
- Preserve current API/UI behavior via automated regression checks.
- Introduce capability contracts without premature generalized frameworks.

**Exit criteria:** Existing chatbot functions unchanged through an explicit engine API.

### Milestone 2 — Universal Nodes, beginning with LAN

- Add independent node identities and liveness/readiness.
- Implement LAN connectivity and secure node registration for currently supported devices.
- Add reconnection behavior and telemetry validation.
- Add another hardware class only after testing the adapter against actual hardware or a faithful simulation.

**Exit criteria:** An A07 can run with its USB cable disconnected while connected by Wi-Fi, assuming Android process constraints permit; the dashboard distinguishes connected vs inferencing-capable.

### Milestone 3 — Scheduler and multi-cluster foundation

- Register clusters/groups with capacity and compatible backend metadata.
- Implement simple resource-aware placement and job queueing.
- Add basic admission control and explicit job state transitions.
- Support multiple inference instances **separately** from single-model partitioning.

**Exit criteria:** Multiple independent requests can be assigned according to capability rules without incorrectly claiming single-model distributed execution.

### Milestone 4 — Distributed inference optimization

- Evaluate runtime/backend choices on real devices.
- Benchmark one/two/three phones with same model and reproducible settings.
- Determine whether heterogenous nodes can participate in one request and under what restrictions.
- Record bottlenecks and validate fallback/retry assumptions.

**Exit criteria:** A documented benchmark report with evidence of computation placement and an honest statement of throughput/latency trade-offs.

### Milestone 5 — AI interoperability and optional reference applications

- Publish an inference API contract and, if practical, add an OpenAI-compatible chat-completions adapter with streaming support.
- Add automated tests for protocol interoperability, failures, streaming, and model/backend capability reporting.
- **Optional RAG reference application:** ingestion, retrieval, source attribution, and grounded Q&A through the inference API; keep all indexing and vector-storage dependencies outside the engine.
- **Optional tool-using agent reference application:** a small, permissioned set of tools and auditable execution; rely on proven model capability instead of assuming tool calling works reliably.
- Consider embeddings as a separate runtime/API capability only when a compatible model and backend have been validated.
- Keep each application separately deployable and do not block an engine release on RAG or agents.

**Exit criteria:** At least one external application can consume the documented inference API; optional RAG/agent examples have independent acceptance criteria and cannot destabilize the core.

### Milestone 6 — Public open-source platform

- Install guides, setup scripts, API documentation, security disclosures, ADRs, CI, integration tests, compatible OSS licenses, and contribution guide.
- Package the existing chatbot as an example client; document LLM support as core and RAG/agent examples as optional.
- Release with accurate support matrix and reproducible demos.

**Exit criteria:** Another developer can reproduce documented functionality without proprietary paid APIs.

## 10. Product/Engineering Principles

- Preserve working software; prefer migration over rewrite.
- Separate proven capabilities, experiments and aspirations.
- Build for extensibility, not theoretical thousands of nodes on day one.
- Do not equate total physical RAM with unified usable model memory.
- Avoid framework-driven architecture and unnecessary microservices.
- Security before wide-area or public node connectivity.
- Runtime-agnostic control plane; pluggable inference backends.
- Start with simple scheduling and evolve based on evidence.
- Keep the platform usable without any AI agent or RAG feature.
- Require no paid API for core functionality; document third-party model and tool licenses.
- Ship verified behavior before advertising aspirational support.
- Maintain a hybrid engineering focus: distributed systems skills remain valuable even if the AI market changes.

## 11. Codex Execution Request

You are acting as a Staff/Principal Software Engineer and AI Systems Architect.

1. **First inspect the current repository**; do not assume its stack or infer the existing architecture from this RFC.
2. Produce `docs/architecture/current-state.md`, `docs/architecture/target-state.md`, and an ADR for the platform-versus-agent boundary **only if appropriate given repository conventions**.
3. Identify the smallest safe migration toward `Distributed AI Engine`, preserving the existing `distributed-ai-agent` interfaces and chatbot behavior.
4. Propose a sequenced implementation backlog with dependencies, acceptance tests and risks; explicitly separate the core LLM inference roadmap from optional RAG and agents.
5. Recommend an incremental first pull request. Implement it only if the current code and tests make the change safe; otherwise explain the blocker.
6. Do not claim that a device supports distributed inference without technical evidence. Instrument the execution path to prove where computation occurs.
7. Do not rename the repository, replace working technologies, introduce infrastructure dependencies or delete code automatically.
8. Report changes, tests run, remaining unknowns, and exactly what must be tested on the three real devices.
9. Update the README feature matrix with Implemented / Experimental / Planned, and describe LLM as core, RAG and agents as optional future applications.
10. Do not implement RAG, a vector database, LangGraph, new service boundaries, or model fine-tuning unless a specific accepted increment calls for them.

**Immediate priority:** audit the working implementation, establish an inference baseline, define modular contracts, and clarify scope in documentation. **Not an immediate priority:** Kubernetes, autoscaling, cross-internet node federation, a custom LLM inference kernel, or broad multi-tenant support.
