# Architecture documentation

The product-level reference is [Distributed AI Engine — Product Vision & Architecture RFC 001](architecture/product-vision.md). It defines the engine as the core product, preserves the existing chatbot as its first client, and places RAG and AI agents in optional application layers.

Use the following documents for implementation work:

- [Current state and verified baseline](architecture/current-state.md): what exists in this repository, which distributed-inference claims have log evidence, open questions, and the proposed smallest next increment.
- [Target architecture](architecture/target-state.md): intended boundaries, runtime and execution planes, workload modes, progressive platform support, and security/observability requirements.
- [Architecture decisions](adr/): decisions and trade-offs recorded as they are accepted.

## Current stack, summarized

- Angular + TypeScript web application.
- ASP.NET Core / C# local platform and control-plane APIs.
- Python local AI/chat runtime and provider adapter.
- llama.cpp coordinator on macOS and Android ARM64 RPC workers.
- USB ADB forwarding for the current inference experiment.
- Dedicated PostgreSQL container for chatbot conversation history and explicit memory notes.

This list describes present code, not universal platform support. The repository contains multiple local launch paths; see the current-state audit before assuming the dashboard process starts inference or that online nodes are executing a request.

## Core architectural constraints

1. Prove single-model distributed inference with coordinator and per-worker execution evidence.
2. Distinguish model partitioning from independent model replicas serving separate requests.
3. Treat node connectivity, liveness, process health, inference readiness and per-request participation as different facts.
4. Keep the engine usable without RAG or an AI agent; keep optional application dependencies outside core.
5. Preserve working chat, history and explicit-memory behavior through regression tests.
6. Do not expose unauthenticated llama.cpp RPC beyond USB/loopback or a trusted isolated network.
7. Add hardware platforms only after a real or faithfully simulated build/run acceptance test.
