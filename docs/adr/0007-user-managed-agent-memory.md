# ADR 0007: Use explicit user-managed memory notes for the first memory slice

**Status:** Accepted for the local MVP

## Context

The MVP needs memory that can be inspected and corrected. Automatic extraction from chat would add model-driven writes and uncertain retention before the project has identity, authorization, or memory evaluation policies.

## Decision

Let the local user create and delete up to 10 plain-text memory notes, each up to 1,200 characters. Store them in PostgreSQL. The ASP.NET Core platform is authoritative and adds the saved notes to normal and streaming chat requests; browser-supplied memory is ignored. The Python runtime treats notes as untrusted, possibly stale context and bounds the notes it will accept.

## Consequences

- Notes are explicit, visible, editable only by deletion and re-creation, and removable from future model requests.
- The first slice has one local profile because the platform has no account system and binds to loopback. Remote multi-user access requires identity and authorization first.
- Notes stay on the local platform and are sent to its Python runtime and configured local inference provider. They are not automatically extracted from transcripts.
- A storage failure does not block chat; the platform sends no notes and reports that memory was unavailable.
- Deleting a note removes it from future requests; it does not redact copies that may already exist in persisted conversation messages.
- This is contextual memory, not semantic retrieval. RAG remains a separate step.
