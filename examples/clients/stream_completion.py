#!/usr/bin/env python3
"""Small standard-library client for the local streaming inference API."""

from __future__ import annotations

import argparse
import json
import sys
import time
from collections.abc import Iterator
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


DEFAULT_BASE_URL = "http://127.0.0.1:5100/v1"
REQUEST_TIMEOUT_SECONDS = 1800


def list_models(base_url: str) -> list[dict]:
    request = Request(f"{base_url.rstrip('/')}/models", method="GET")
    try:
        with urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except HTTPError as exception:
        detail = exception.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Model listing failed with HTTP {exception.code}: {detail}") from exception
    except (URLError, TimeoutError) as exception:
        raise RuntimeError(f"The inference API is unavailable: {exception}") from exception

    models = payload.get("data") if isinstance(payload, dict) else None
    if not isinstance(models, list) or not models or any(not isinstance(model, dict) for model in models):
        raise RuntimeError("The inference API returned an invalid model list.")
    return models


def iter_streamed_text(response) -> Iterator[str]:
    received_done = False
    for raw_line in response:
        line = raw_line.decode("utf-8").strip()
        if not line or line.startswith(":") or not line.startswith("data:"):
            continue

        data = line[5:].strip()
        if data == "[DONE]":
            received_done = True
            break

        try:
            event = json.loads(data)
        except json.JSONDecodeError as exception:
            raise RuntimeError("The inference API sent an invalid SSE event.") from exception

        if isinstance(event, dict) and "error" in event:
            error = event["error"]
            message = error.get("message", "The inference API returned an error.") if isinstance(error, dict) else error
            raise RuntimeError(str(message))

        choices = event.get("choices") if isinstance(event, dict) else None
        if choices is None:
            choices = []
        if not isinstance(choices, list):
            raise RuntimeError("The inference API returned an invalid choices collection.")
        for choice in choices:
            if not isinstance(choice, dict):
                raise RuntimeError("The inference API returned an invalid completion choice.")
            delta = choice.get("delta") or {}
            if not isinstance(delta, dict):
                raise RuntimeError("The inference API returned an invalid text delta.")
            content = delta.get("content")
            if isinstance(content, str) and content:
                yield content

    if not received_done:
        raise RuntimeError("The inference API closed the stream without the [DONE] marker.")


def stream_completion(base_url: str, model_id: str, prompt: str, max_tokens: int) -> Iterator[str]:
    payload = {
        "model": model_id,
        "messages": [{"role": "user", "content": prompt}],
        "max_tokens": max_tokens,
        "stream": True,
    }
    request = Request(
        f"{base_url.rstrip('/')}/chat/completions",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json", "Accept": "text/event-stream"},
        method="POST",
    )
    try:
        with urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            yield from iter_streamed_text(response)
    except HTTPError as exception:
        detail = exception.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Inference failed with HTTP {exception.code}: {detail}") from exception
    except (URLError, TimeoutError) as exception:
        raise RuntimeError(f"The inference API is unavailable: {exception}") from exception


def main(arguments: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Stream a completion through the local Distributed AI Engine API.")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="Versioned API base URL (default: %(default)s).")
    parser.add_argument("--model", help="Model ID; defaults to the first model returned by /models.")
    parser.add_argument("--prompt", default="Explain distributed inference in one short sentence.")
    parser.add_argument("--max-tokens", type=int, default=80)
    options = parser.parse_args(arguments)
    if not 1 <= options.max_tokens <= 2048:
        parser.error("--max-tokens must be between 1 and 2048.")

    try:
        models = list_models(options.base_url)
        model_id = options.model or models[0].get("id")
        if not isinstance(model_id, str) or not model_id:
            raise RuntimeError("The selected model has no valid ID.")

        started_at = time.perf_counter()
        first_content_at: float | None = None
        chunk_count = 0
        for text in stream_completion(options.base_url, model_id, options.prompt, options.max_tokens):
            if first_content_at is None:
                first_content_at = time.perf_counter()
            chunk_count += 1
            print(text, end="", flush=True)
        if chunk_count == 0:
            raise RuntimeError("The inference API completed without returning any text.")
        print()
        elapsed_seconds = time.perf_counter() - started_at
        first_content_ms = "not recorded" if first_content_at is None else f"{(first_content_at - started_at) * 1000:.1f} ms"
        print(
            f"Completed model={model_id}; first content={first_content_ms}; "
            f"elapsed={elapsed_seconds:.3f} s; streamed chunks={chunk_count}.",
            file=sys.stderr,
        )
        return 0
    except (RuntimeError, OSError, ValueError) as exception:
        print(f"Inference client failed: {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
