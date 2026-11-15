# ADR-0002: Angular/.NET platform with a Python AI runtime

- Status: Accepted for stack selection; product-boundary details superseded by [ADR 0008](0008-engine-core-and-optional-applications.md)
- Date: 2026-11-14

## Context

The project needs a conventional, maintainable platform and direct access to the Python AI ecosystem. The owner already has experience with .NET and Angular and wants to build experience that also applies to AI engineering.

## Decision

- Angular + TypeScript is the target web frontend.
- ASP.NET Core / C# is the primary platform and control plane. It owns public application APIs, node identity/registry, health policy, authorization, and durable platform state.
- Python is the AI-specific integration/runtime choice. The existing chat prototype uses it for provider integration and tool orchestration. Future agent and RAG behavior belongs to optional applications and must not become a dependency of the inference engine.
- llama.cpp remains the inference engine and distributes one model across Android RPC workers.

The Python runtime is one deliberate process boundary for AI-specific integration, not a general microservices architecture. It communicates with the .NET platform through a versioned local contract. PostgreSQL currently supports chatbot history and explicit memory; pgvector is only an option for a future RAG application.

## Trade-offs

This adds a language boundary and requires contract tests, process health checks, and trace correlation. It preserves Python's AI integration ecosystem and uses .NET for the main platform. The current standard-library Python API is only a feasibility prototype; it is not yet the final service contract.

## Career evidence

Python is a useful addition for AI/data work, but should not be described as universally having more backend vacancies than C#. O*NET/Lightcast 2025 U.S. programmer postings list Python at 13% and C# at 12%; Stack Overflow's 2025 survey shows Python usage accelerated by 7 percentage points, which measures developer use rather than job vacancies. Links: [O*NET demand data](https://www.onetonline.org/link/demand/15-1251.00) and [Stack Overflow technology survey](https://survey.stackoverflow.co/2025/technology).
