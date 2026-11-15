# ADR-0001: One model distributed across workers

- Status: Accepted
- Date: 2026-11-14

## Context

The central requirement is to use multiple devices for one model inference. Spreading independent requests across independent models would not prove that capability.

## Decision

Use llama.cpp RPC for the first inference plane. Select only RPC devices for model execution and require logs that show graph work on each worker and model weights on each RPC device, with zero model weights on the Mac.

## Evidence and consequences

Qwen2.5 7B Q3_K_M produced a non-empty response over USB on two Galaxy A07s and one Galaxy A06. The workers recorded 30 graph recomputations each; allocations were 1,068/1,060/1,274 MiB; Mac model weights were 0 MiB. The strict three-A07 target, Wi-Fi behavior, and comparative one/two/three-node benchmarks remain open. RPC is experimental and unauthenticated.
