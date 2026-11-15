# ADR 0010: Separate node liveness from RPC process health

- Status: Accepted for the local Android/USB experiment
- Date: 2026-11-14
- Related: [ADR 0003](0003-usb-first-worker-transport.md), [ADR 0005](0005-mac-hosted-adb-telemetry-bridge.md)

## Context

The platform's `Online` state is derived from a recent heartbeat. The Mac-hosted ADB bridge can additionally inspect whether `ggml-rpc-server` has a process ID on a connected phone. Neither observation proves that the forwarded RPC port is reachable or that a particular model/backend is ready to execute inference.

## Decision

- Keep heartbeat liveness (`AwaitingHeartbeat`, `Online`, `Offline`) independent from the observed RPC process (`Unknown`, `Running`, `Stopped`).
- Collect the process observation through the existing USB ADB bridge and display it separately in the dashboard.
- Clear the reported process state to `Unknown` when the heartbeat goes offline, because the last process observation may be stale.
- Label the observation as process state, not inference readiness. Do not use it for scheduling or claim model readiness.
- Add a model/backend-specific readiness check only when a real probe and its lifecycle are defined and validated.

## Consequences

- A phone may be `Online` while its RPC process is `Stopped` or `Unknown`.
- A `Running` process is useful diagnostic information, but acceptance still requires RPC reachability and model/backend readiness checks.
- The heartbeat reports a small additional ADB query; its duration is included in the existing ADB collection metric.
- The design remains limited to the local ADB/USB development path; it does not add an Android-resident agent or network enrollment.

## Probe limitation found during follow-up

The llama.cpp RPC worker accepts a client and serves it in a loop; the current coordinator keeps that connection open. A raw TCP connect to the forwarded port can complete while another connection is being served, without receiving an RPC HELLO response. Repeating that probe is therefore not a safe or meaningful dashboard health signal. The CLI's `nc -z` check is labeled as TCP reachability only. A protocol-aware HELLO probe would need a lifecycle that does not compete with the active coordinator connection before it can represent worker readiness.

During the repeated-probe experiment, subsequent ADB shell telemetry calls timed out. Removing the project forward for Worker 1 immediately restored `adb shell` on that phone; removing the other two project forwards restored it on all three. The launcher then cleanly stopped and restarted the workers, and distributed chat succeeded again. This does not prove the exact internal cause, but it is sufficient evidence to keep recurring forward probes out of the dashboard and to treat `nc -z` as a manual TCP-only observation.
