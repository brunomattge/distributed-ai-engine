# Security notes

- `llama-server` and the chat API bind to `127.0.0.1` only.
- The one agent tool is allowlisted and read-only: it invokes the fixed Phase 1 cluster status command without a shell.
- The llama.cpp RPC protocol is experimental and unauthenticated. Use USB ADB forwarding or a trusted isolated LAN; never port-forward RPC to the internet.
- Do not put secrets in chat prompts. This MVP sends prompts to a local model server but does not persist chat history; event logs record counts and timestamps, not message text.
- Device serials and raw worker logs are excluded from source control.

Report security issues privately to the project owner until a contact address is added.
