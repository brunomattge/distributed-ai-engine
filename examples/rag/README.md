# Optional document Q&A example

This standalone CLI demonstrates a small retrieval-augmented generation flow on top of the local inference API. It is an optional application, not an engine dependency.

## Run

Start the local engine so the .NET gateway is ready, then ask a question over a directory of Markdown or UTF-8 text documents:

```sh
python3 examples/rag/ask_documents.py examples/rag/sample_documents \
  "What is the refund request period after purchase?"
```

The CLI discovers the loaded model through `GET /v1/models`, retrieves up to three matching excerpts, and sends one completion request to `POST /v1/chat/completions`. It prints the answer, the retrieved source paths, and model citations that match the retrieved source IDs. Use `--api-base-url` (platform root URL without `/v1`), `--model`, `--top-k` (1–10), or `--max-completion-tokens` to override defaults.

## Retrieval and trust boundaries

- The example uses dependency-free BM25 lexical retrieval. It does not use embeddings, PostgreSQL, pgvector, or a persistent index.
- It reads `.md` and `.txt` files recursively, skips hidden paths and symlinks, requires UTF-8, and rejects files larger than 5 MiB. Chunks contain up to 220 words with 30 words of overlap.
- A question with no matching excerpts fails before it calls the inference API.
- The assembled model input is limited to 12,000 characters; reduce `--top-k` or document size if it exceeds the limit.
- Retrieved text is serialized as JSON and labeled untrusted in the system instruction. The runtime exposes no document tools or side-effecting actions. Prompt instructions reduce confusion but cannot guarantee that a language model ignores malicious text.
- The source list reports retrieval provenance. It does not prove that the generated answer is factually grounded. The model is asked to cite `[S1]`-style IDs; the client only recognizes citations belonging to retrieved excerpts.

## Evaluation

The checked-in [evaluation cases](evaluation_cases.json) cover refund timing, access-log retention, and support response time. The tests verify top-ranked source and expected fact retrieval, untrusted-context framing, allowed citation IDs, input-size limits, malformed model discovery, HTTP error handling, missing-result behavior, and the request contract without requiring phones or a running model:

```sh
python3 -m unittest tests.python.test_rag_example -v
```

This is a lexical retrieval smoke suite, not a semantic-search or answer-quality benchmark. The broader engine API remains the experimental subset described in the [OpenAPI contract](../../docs/api/openapi.json).
