# ADR 0009: Keep inference contracts outside the chat/agent runtime

- Status: Accepted for the Phase 3 prototype
- Date: 2026-11-14
- Related: [ADR 0008](0008-engine-core-and-optional-applications.md)

## Context

The existing Python `ChatProviderV1` protocol and llama.cpp adapter were stored inside `agent_runtime`, even though inference is the engine capability and chat/tool orchestration is an application concern. This naming and package boundary would make future non-agent clients depend on an agent-oriented module.

The current supported execution target is the owner's Android phones over USB ADB. Other CPU platforms and GPU hosts are future targets; a future-facing boundary must not claim that their adapters already exist.

## Decision

- Place the provider-neutral v1 request/result/protocol and the experimental llama.cpp adapter in `experiments/phase-3/python/inference/`.
- Make `agent_runtime` depend on the inference contract; keep prompts, tool selection/execution, memory composition and chat policy in `agent_runtime`.
- Preserve the previous Python import paths as re-exports so current local callers do not break during this migration.
- Keep llama.cpp RPC as the only current inference adapter and retain the existing phone-only USB launch configuration.
- Add a future adapter only for a named hardware/runtime target after it can be built, run and tested. Priority: currently available phones first, other CPU classes when validation hardware is available, GPU hosts last.

## Consequences

- The boundary is ready for additional providers without implementing or claiming them now.
- The existing chat behavior and runtime remain the same; this change only moves ownership of shared provider types and preserves compatibility imports.
- This internal Python package is not yet a public, stable engine API. Public versioning and independent client acceptance remain roadmap work.
- Contract tests run without phones; a hardware run is still required to validate the actual distributed backend and current devices.
