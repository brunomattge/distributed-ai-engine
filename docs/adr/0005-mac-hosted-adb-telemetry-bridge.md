# ADR 0005: Use a Mac-hosted ADB bridge for the first physical telemetry slice

**Status:** Accepted for Phase 1 development and hardware validation

## Context

The platform API and Angular dashboard need real device health data before an Android-resident agent is justified. The project already uses USB ADB to start and monitor the llama.cpp RPC workers. Android exposes aggregate CPU counters in `/proc/stat`, available RAM in `/proc/meminfo`, and a surface temperature sensor through `dumpsys thermalservice` on the tested Galaxy A07/A06 devices.

## Decision

Add a small Python process on the Mac that discovers only supported Galaxy A07/A06 models through ADB, reads those metrics, and sends heartbeats to the loopback ASP.NET Core API. Derive stable local node IDs by hashing ADB serials. Keep the original serial only in process memory; never print or persist it. Do not modify or wrap llama.cpp RPC workers.

## Consequences

- The dashboard can show live physical device data without a new Android app or changes to the inference worker.
- The bridge scans ADB every two seconds, seeds its prior-device set from existing `adb-` API registrations at startup, and immediately notifies the API when a successful scan omits a known device. The API's six-second heartbeat policy remains a fallback. Reconnecting the same device produces the same ID and its next heartbeat restores Online.
- A transient USB renegotiation can briefly produce Offline until the next heartbeat. The five-second UI poll bounds how quickly the dashboard reflects state changes; API and UI tests cover both Offline and reconnect transitions.
- `ADB collection` is the combined duration of the two telemetry shell commands. It is not Wi-Fi RTT or inference RPC latency.
- This adapter depends on the Mac and an authorized ADB connection. It is not a production Android agent, a remote cluster protocol, or evidence that a phone participated in model inference.
- Unsupported models are ignored. Current validation uses two Galaxy A07s plus one Galaxy A06, so acceptance for exactly three A07s remains open.
