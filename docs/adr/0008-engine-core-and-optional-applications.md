# ADR 0008: Treat inference as the core and chat, RAG, and agents as applications

- Status: Accepted as the current product direction
- Date: 2026-11-14
- Source: [Distributed AI Engine product vision and architecture RFC 001](../architecture/product-vision.md)

## Context

The repository began as Distributed AI Agent and now contains a chatbot/tool prototype, a cluster dashboard, a platform API, and experimental llama.cpp RPC scripts. The product vision establishes reusable distributed inference as the product core. It also requires preserving the working chatbot and distinguishes single-model partitioning from serving independent requests.

The roadmap previously placed RAG inside the initial agent MVP. That creates a product-boundary conflict: document ingestion, vector storage, and agent orchestration are not required to validate or release the inference engine. However, conversation history and user-managed memory already exist in the chatbot and should not be removed as part of this decision.

## Decision

1. Position **Distributed AI Engine** as the core platform for node orchestration and model inference.
2. Preserve the existing chatbot as the first application and reference client; evolve it through small compatibility-preserving changes.
3. Keep the current ASP.NET Core platform, Angular UI, Python AI integration, and llama.cpp RPC path. No technology replacement or repository rename is required by this decision.
4. Treat RAG and a tool-using AI agent as optional application milestones after the core inference API is stable. Keep their document stores, vector databases, and agent frameworks out of engine dependencies.
5. Treat embeddings as a separate optional inference workload, subject to a compatible model/backend and acceptance tests.
6. Keep explicit conversation history/memory behavior in the existing chatbot application; it is not a reason to couple the engine to agent runtime concerns.
7. Add platforms and multi-cluster behavior progressively, based on tested adapters and capability evidence.
8. Do not include fine-tuning in the MVP roadmap.

## Consequences

- The roadmap prioritizes verified distributed inference, API boundaries, node readiness, lifecycle, and trustworthy observability before optional RAG/agent examples.
- Existing user-facing chatbot functionality remains a regression requirement.
- RAG may still use PostgreSQL + pgvector as an optional app implementation, but the engine can run without either.
- The current USB/RPC proof remains experimental and validated only on the recorded Android cohort. It does not imply compatibility with every Android ABI/backend, Wi-Fi, other hardware platforms, multi-cluster, or fault-tolerant continuation.
- New API contracts must distinguish one model partitioned across nodes from independent replicas processing separate requests.
