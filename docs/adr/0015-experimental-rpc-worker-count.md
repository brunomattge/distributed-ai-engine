# ADR 0015: Select RPC count for controlled experiments

- Status: Accepted for experimentation only
- Date: 2026-11-15

## Context

The performance roadmap needs comparable runs of one GGUF with one, two and three Android RPC devices. The Phase 3 launcher previously hard-coded all three RPC endpoints, so changing the count required editing the launcher. A count control must not imply a scheduler, independent replicas, or per-device model readiness.

## Decision

`INFERENCE_WORKER_COUNT` accepts only `1`, `2` or `3` and defaults to `3`. It limits the `--rpc` and `--device` arguments passed to llama.cpp to the leading endpoints (`RPC0...`). The fixed cluster launcher still starts and monitors all three USB workers. The English session summary records the selected RPC count, worker graph counts and model-weight allocations.

## Consequences and limits

- This is a repeatable experiment switch, not production placement logic or a public API.
- The selected prefix follows the current ADB worker order. It is not capability-aware; the mixed 2×A07 + 1×A06 cohort must be reported.
- A three-request cold sweep with per-request Android RAM and `SKIN` temperature sampling has been recorded for each worker count. It does not establish a general performance trend; thermal changes across longer runs, other days, or other hardware remain unmeasured.
- A single selected RPC is a baseline configuration, not evidence that all cluster devices participated.

## Verification

- Shell syntax check passed; invalid values are rejected before cluster startup.
- Hardware runs with 1, 2 and 3 selected RPCs each returned `OK`; a three-request cold sweep disabled prompt caching and confirmed graph work on every selected worker, with 0 MiB of Mac model weights.
- Raw coordinator, worker, and chat logs are local artifacts excluded from Git. The repeated measurements and limitations are summarized in the Phase 3 guide.
