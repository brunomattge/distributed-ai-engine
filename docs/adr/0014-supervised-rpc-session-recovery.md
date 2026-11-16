# ADR 0014: Supervise RPC session recovery

- Status: Accepted
- Date: 2026-11-15

## Context

A USB disconnect can leave the Android RPC processes visible again after reconnection while the existing llama-server RPC session is unusable. A subsequent request then aborts the coordinator. Readiness must expose this failure, stop additional requests, and distinguish a new model-server generation from the stale one.

## Decision

The Phase 3 launcher supervises the llama-server process. When it exits, it publishes a degraded state, waits for exactly three authorized USB devices, stops and starts the RPC workers, and loads a fresh llama-server session. It permits two recovery attempts per launcher run and waits up to ten minutes for the devices. These bounds are configurable through `INFERENCE_MAX_RECOVERIES` and `INFERENCE_PHONE_WAIT_SECONDS`.

The launcher atomically publishes its state and monotonically increasing generation in the local runtime status file. The Python health endpoint exposes these fields. The .NET readiness service latches session failures and keeps chat unavailable until it observes a newer generation report `ready` with all three workers online. Chat and SSE failures mark the session degraded; Angular immediately disables sending and explains the recovery state.

The user must resend a failed request after recovery. The application does not replay requests or resume generated tokens.

## Consequences and limits

- This supervises the current fixed three-phone USB launcher; it is not a generic service manager or cross-platform scheduler.
- If a phone is disconnected, the supervisor waits for all three before replacing workers, avoiding a partial cluster restart.
- After the configured retry or wait limit is exceeded, the chat remains degraded and the Terminal reports that manual intervention is needed.
- This is process restart and request retry by the user, not checkpoint-based continuation, transparent failover, or fault-tolerant inference.
- A different launcher or direct Python runtime without the status file is not supervised by this mechanism.

## Verification

- Tests cover degraded-session latching, release only on a newer ready generation, HTTP/SSE failure reporting, chat gating, and status-file parsing.
- On 2026-11-16, stopping one A07 RPC process through ADB while keeping USB connected caused the gateway to reject chat at 2/3 processes. A subsequent internal runtime request failed the coordinator; supervision restarted all three workers and model server, advanced the generation from 8 to 9, and a fresh chat succeeded. This validates process-failure recovery only.
- Hardware acceptance must disconnect a phone, observe the clear error/block, reconnect it, verify supervised worker/coordinator restart, and complete a fresh user-submitted chat.
