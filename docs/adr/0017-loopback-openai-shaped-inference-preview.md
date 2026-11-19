# ADR 0017: Loopback OpenAI-shaped inference preview

- Status: Accepted for local integration experiments
- Date: 2026-11-16

## Context

The engine inference contract and llama.cpp adapter are separated from the agent runtime, but an independent client has no HTTP entry point that invokes this provider directly. The existing `/api/chat` routes belong to the chatbot and apply agent behavior, including the read-only cluster-status tool and saved memories. External client experiments need a clean path that does not imply those application features are part of the inference engine.

## Decision

Expose a narrow, OpenAI-shaped API from the ASP.NET Core platform at `/v1/models` and `/v1/chat/completions`, backed by internal Python inference routes. It invokes the inference provider directly, supports text completion and SSE text streaming, and is restricted to the configured model. Completion requests pass through the existing .NET inference-readiness gate; structured upstream stream errors latch the session as degraded. The Python server stays on loopback, and direct Python inference routes are internal only. Accept `max_completion_tokens` and the legacy `max_tokens` as mutually exclusive aliases for the provider token limit.

This is a compatibility preview, not a stable or complete OpenAI API. Tool calling, authentication, usage accounting, model negotiation, cancellation guarantees, and network exposure are excluded. The existing Angular chat continues through the .NET gateway and agent runtime.

## Consequences

- An independent local client can exercise distributed inference without invoking chatbot orchestration.
- Agent tools and explicit memories cannot affect these completion requests.
- Independent clients use `http://127.0.0.1:5100/v1` by default and inherit the platform's readiness and degraded-session behavior.
- A dependency-free terminal reference client demonstrates model discovery, streaming, and client-side latency reporting.
- A checked-in OpenAPI 3.1 JSON document describes the supported preview surface; a dependency-free regression test validates its structure, operations, limits, and local references.
- The official OpenAI Python SDK 2.48.0 was exercised against model listing, SSE chat completion, and structured unsupported-tool errors; this validates those operations only and does not claim full API compatibility.
- The modern `max_completion_tokens` field and legacy `max_tokens` alias are mapped to the same provider limit and validated by unit tests plus a physical SDK request.
- The internal Python routes on port 8000 still bypass the .NET failure-generation latch; clients must not use them directly.
- Contract tests use fake providers and HTTP clients. A physical three-phone request through the .NET platform route returned `OK`; the recorded local run is described in the Phase 3 guide. This demonstrates the tested request path, not general API compatibility.
