# Contributing

Keep development phase-based and focused on the current MVP. Before starting a phase, write down its objective, scope, risks, and acceptance checks. Do not claim completion until those checks pass on the documented hardware.

## Local checks

```sh
python3 -m unittest discover -s tests -v
bash -n experiments/phase-0/*.sh experiments/phase-1/*.sh experiments/phase-2/*.sh experiments/phase-3/*.sh
```

Keep raw llama.cpp and worker output unchanged. Write human-readable session summaries in a separate `summary-en-us.log`. Do not commit model weights, build directories, device IDs, credentials, or run logs.

Changes that expose RPC workers beyond USB forwarding or loopback require an explicit security review. The RPC protocol is unauthenticated.
