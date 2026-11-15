# ADR 0011: Report coordinator model readiness separately

- Status: Accepted
- Date: 2026-11-14

## Context

The dashboard reported phone heartbeat and the ADB-observed RPC process, but it did not show whether the active `llama-server` had completed loading its model. Repeated TCP connect probes against worker RPC ports correlated with ADB shell timeouts during a prior experiment, so the dashboard must not add recurring worker socket probes.

## Decision

The Python runtime polls the host-local `llama-server /health` endpoint with a short timeout and maps its response to `ready`, `loading`, or `unavailable`. ASP.NET Core proxies that result at `/api/model-status`; Angular displays it separately from node state and refreshes it on the existing five-second cadence. The runtime never probes individual RPC workers for this display.

## Consequences

- `ready` means only that the coordinator reports its model loaded. It does not mean every phone is online, prove per-request phone participation, or establish that the coordinator is serving a distributed model.
- Phone participation remains demonstrated by the existing inference evidence: coordinator allocation plus worker logs for the same run.
- Model readiness can be observed without disrupting the USB ADB telemetry path.
- The physical transition from loading to ready after unloading/reloading the active model still needs a hardware validation run.

## Validation

Python tests cover ready, HTTP 503/loading, and unreachable/unavailable. The .NET suite verifies compilation and existing gateway behavior; Angular tests verify the status is presented as coordinator state. No worker RPC port is opened by the new polling code.
