# Security notes

- `llama-server` and the chat API bind to `127.0.0.1` only.
- The one agent tool is allowlisted and read-only: it invokes the fixed Phase 1 cluster status command without a shell.
- The llama.cpp RPC protocol is experimental and unauthenticated. The supported MVP transport is USB ADB port forwarding. Direct network RPC is unsupported; never expose it to the internet.
- Do not put secrets in chat prompts. This MVP sends prompts to a local model server but does not persist chat history; event logs record counts and timestamps, not message text.
- Device serials and raw worker logs are excluded from source control.

## Reporting a vulnerability

Use [GitHub's private vulnerability reporting form](https://github.com/brunomattge/distributed-ai-engine/security/advisories/new) for this repository. The repository owner must enable private vulnerability reporting in GitHub settings before this route is available. Do not publish sensitive vulnerability details in public issues. If private reporting is unavailable, contact the maintainer through the [project owner's GitHub profile](https://github.com/brunomattge) and ask for a private reporting channel.
