# ADR 0013: Gate chat on distributed inference readiness

- Status: Accepted
- Date: 2026-11-15

## Context

The local llama.cpp coordinator can report its model as `ready` even when one or more Android RPC workers have disconnected. That signal describes coordinator/model health only. It does not mean the fixed three-worker experiment can currently serve chat. A physical USB disconnect also caused the existing RPC session to fail after the phones were reconnected; a clean worker and coordinator restart restored inference.

## Decision

The platform exposes `/api/inference-status`, combining the Python coordinator health with the node registry. For this fixed MVP topology, distributed chat is available only when the coordinator reports `ready` and exactly three registered `adb-` nodes are `Online` with their ADB-observed RPC processes `Running`. Simulator nodes cannot satisfy this hardware readiness check.

The .NET chat and streaming endpoints check this status before invoking the Python runtime. If unavailable, they return HTTP 503 with a user-facing reason. Angular displays chat availability, coordinator health, and the count of ADB-observed RPC processes separately. The count is explicitly named active RPC processes; it does not imply per-device model readiness. Sending stays disabled until the fixed-cluster admission check passes.

## Consequences and limits

- This is a guard for the current three-worker setup, not a general scheduler or multi-cluster readiness model.
- The `activeRpcWorkers` count means registered Android nodes are online and ADB reports their RPC processes as running. It is not per-device model readiness, proof that the RPC port accepts work, or proof that a worker participates in a particular request.
- Readiness can change immediately after the check. The guard reduces predictable failures but cannot make an RPC request atomic with worker availability.
- The bounded supervisor can restart workers and coordinator after an RPC session failure, but starts a new session: it does not replay the failed request or continue generation from a checkpoint. The hardware recovery cycle is validated in [ADR 0014](0014-supervised-rpc-session-recovery.md).
- The standalone Python runtime remains available on its own local port and does not use the .NET readiness gate. The gated product path is Angular through ASP.NET Core.

## Verification

- .NET tests cover ready, loading, unavailable and incomplete-cluster status, plus HTTP 503 before the Python agent is called.
- Angular tests cover the chat-availability label, active RPC process count, coordinator state and the warning that process state is not per-device model readiness. A .NET serialization test locks the `activeRpcWorkers` field name.
- Hardware smoke verification is recorded in `docs/architecture/current-state.md`; this ADR does not claim fault-tolerant continuation.
