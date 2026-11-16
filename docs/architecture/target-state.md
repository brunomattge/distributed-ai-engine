# Target architecture: Distributed AI Engine

**Source of truth:** [Product vision and architecture RFC 001](product-vision.md).
This is an evolutionary target, not a description of current implementation or a commitment to support every platform at once.

## Product boundary

The **Distributed AI Engine** is the core product: it discovers and manages compatible compute nodes, coordinates workloads, and exposes a documented inference API. Single-model distributed inference is a first-class capability when a backend and compatible nodes support it.

The existing chatbot remains the first application/client and must keep working during migration. RAG and AI-agent experiences are optional, separately testable applications that consume the engine API. Embeddings may become an optional inference workload; a vector database, document pipeline, agent framework, and fine-tuning are not core engine dependencies.

## Logical target

```text
Clients and optional applications
  Existing chat | OpenAI-compatible clients | Optional RAG | Optional agent
                              |
                    Versioned inference API
                              |
                 Modular control plane (start simple)
  Node lifecycle | capabilities | cluster membership | jobs | model catalog
  placement policy | health/readiness | auth/policy | metrics and events
                              |
                   Runtime adapter boundary
                 llama.cpp RPC | future adapters
                              |
                         Execution plane
      Android phones (first) | other CPU platforms (later) | GPU hosts (last)
```

Start by reusing the existing Angular client, ASP.NET Core platform, Python AI integration, and llama.cpp experiment. The RFC does not require replacing the current stack or creating separate microservices. Keep a modular monolith for control-plane concerns until operational evidence justifies another process boundary. Python remains appropriate for AI-specific provider integration; .NET remains the current platform/control-plane implementation. Hardware order is Android phones first, other CPU platforms when available to validate, and GPU hosts last. The runtime-adapter boundary is prepared for extension; no future platform is claimed as supported before its own acceptance run.

## Responsibilities and contracts

| Boundary | Responsibility | Must not imply |
| --- | --- | --- |
| Application/client | Chat UX, optional RAG ingestion/retrieval, optional agent tools and memory | That these features are required to start or use the engine |
| Public inference API | Versioned request/response, streaming, cancellation/error semantics, model/runtime status; OpenAI-compatible adapter when accepted | That compatibility already exists or that all model features work across providers |
| Control plane | Node identity/lifecycle, capability inventory, cluster membership, workload admission/placement, job state and observability | That connected/online means ready for a particular model |
| Runtime adapter | Translate an accepted workload to a runtime-specific execution request and report actual placement/results | That independent replicas equal one partitioned model |
| Execution plane | Perform model/tensor/graph work on compatible selected devices | That every OS/device can run every backend or that phone RAM is a shared pool |
| Optional embedding capability | Run a compatible embedding model through a validated adapter/API | That the engine owns document indexing or vector storage |

Node state must distinguish at minimum transport connectivity, process health, inference readiness for a particular workload/model, and participation in a specific request. Version capability contracts only when a concrete consumer and test need exist.

## Scale modes

1. **Single-model distributed inference:** one request/model partitioned across multiple compatible devices. Prove this with coordinator and per-worker execution evidence.
2. **Scale-out/throughput:** independent model instances or compatible groups serve different requests. This is a separate feature and must be labeled as such.
3. **Functional extension:** separate optional clients/adapters provide RAG, agent tools, embeddings, vision or batch behavior when appropriate runtimes exist.

The initial scheduler can be a small deterministic placement policy. Multi-cluster support comes only after cluster identity, capability reporting and job semantics are clear. Do not introduce Kubernetes, a general workflow engine, or broad plugin infrastructure without a specific operational need.

## Progressive platform support

Android devices over USB/ADB are the MVP hardware target; no specific manufacturer or model is required. The worker binary, Android ABI, available memory, and llama.cpp backend must still be compatible, and support claims must match actual build/run evidence. The next transport target is authenticated LAN connectivity, but it remains deferred until the USB baseline and security boundary are stable. Other CPU operating systems/hardware follow when available for validation; GPU hosts are a later platform milestone, after the Android MVP. All require a real build/run test or a faithful, explicitly labeled simulator. The adapter boundary is intended to make those additions possible, not to imply support today.

## Security, failure behavior, observability

- Keep llama.cpp RPC private and isolated; current RPC workers have no authentication.
- Require node identity/enrollment and least privilege before enabling LAN or remote access.
- Model worker loss as an explicit job failure unless the selected runtime proves that resumption/retry is safe.
- Record request ID, model artifact/version, backend, selected workers, worker operation evidence, placement, timings, and failure status. Avoid leaking device serials or user prompt content into routine logs.
- Separate model load time, time to first token, prompt evaluation, generation speed, and total request latency.
- Build metrics from stable events and make clear whether a measurement comes from an actual device, an estimate, or a simulator.

## Optional application contracts

- **Chatbot:** keep the existing UI and behavior as a reference client; migrate its provider call to the documented inference API without a big-bang rewrite. Current explicit memory notes remain application data and are not engine state.
- **RAG:** post-core optional reference application. Own document ingestion, chunking, retrieval, source attribution and grounding evaluation outside the engine. PostgreSQL + pgvector is one possible application storage adapter, not a mandatory engine dependency. Embeddings are an independent capability to validate.
- **AI agent:** post-core optional reference application. Own bounded planning, allowlisted tools, memory and audit trail. Require approval for side effects. The engine must not depend on an agent framework.
- **Fine-tuning:** out of scope for the current roadmap until a separate hardware, data, licensing and product case is accepted.
