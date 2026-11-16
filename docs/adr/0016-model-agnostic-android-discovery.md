# ADR 0016: Model-agnostic Android device discovery

- Status: Accepted
- Date: 2026-11-16

## Context

The MVP targets compatible Android devices, not a specific Samsung SKU. The initial ADB telemetry bridge used a Galaxy A07/A06 allowlist even though the physical inference evidence already used a mixed cohort of two A07s and one A06. That allowlist blocked discovery of other authorized Android devices before their runtime compatibility could be assessed.

## Decision

The Mac-side ADB bridge discovers every authorized device reported by `adb devices -l` and does not filter by manufacturer or model. It preserves the reported model when available, falls back to the product identifier, and labels missing identifiers generically. Unauthorized and offline entries remain excluded. Stable node IDs continue to be derived from a hash of the ADB serial.

This is discovery and telemetry support. Distributed inference still requires a compatible Android ABI, worker binary, memory budget, and llama.cpp RPC backend. The current validated inference cohort is two Galaxy A07s and one Galaxy A06 over USB. GPU hosts are planned for a later platform milestone and are not part of the MVP.

## Consequences

- The dashboard can show authorized Android models without a per-SKU code change.
- Discovery does not claim every Android model can run the current worker or a given LLM.
- Unsupported telemetry commands and worker/runtime compatibility require separate validation as new Android device classes are tested.
- LAN enrollment, additional CPU platforms, and GPU adapters remain separate increments.

## Verification

- Python unit tests verify discovery of known Samsung and generic Android model identifiers and exclusion of an unauthorized device.
- Physical inference evidence remains limited to the recorded A07/A06 USB cohort.
