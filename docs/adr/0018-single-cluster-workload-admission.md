# ADR 0018: Deterministic single-cluster workload admission

- Status: Accepted for the experimental Android RPC topology
- Date: 2026-11-17

## Context

The inference gateway needs to identify which workers it admitted for a request and distinguish a single model partitioned across workers from independent model replicas. The current coordinator loads one model into one llama.cpp RPC session, while the platform observes Android connectivity and worker-process state. It does not have trustworthy per-model memory budgets, a durable job system, or a runtime that launches independent replicas.

## Decision

- Admit only `single_model_distributed` completion requests. Reject `independent_replicas` until a backend implements that execution mode.
- Require the coordinator to be ready and select the requested count from online ADB nodes whose RPC process is observed as running and whose current cluster profile satisfies the loaded model and `llama.cpp-rpc` backend requirement. Sort candidates by node ID for deterministic selection; reject incomplete placement instead of silently running on fewer workers.
- Return job ID, workload mode, model, backend, and selected node IDs in response headers. Expose a query endpoint for the recent job state.
- Track accepted, running, completed, failed, cancelled, and timed-out states in a bounded process-local registry. Retain at most 256 requests, evict terminal jobs first, and reject admission if all retained entries are active.
- Do not retry or resume after a worker failure. Concurrent requests receive separate job records and do not imply throughput scaling or replica execution.
- Defer RAM-based placement until per-process/model resource measurements and a validated policy exist.

## Consequences

- Admission and workload semantics are inspectable without introducing a queue, database schema, scheduler service, or multi-cluster abstraction.
- A selected worker list records admission intent; it does not prove each device performed graph work. Worker logs remain the evidence for physical participation.
- Job status is diagnostic and disappears on process restart. It is not a durable job API or a recovery mechanism.
- The fixed-topology readiness gate still requires the three current Android RPC workers. Placement logic is tested against explicit capability candidates but is not a general heterogeneous scheduler.
