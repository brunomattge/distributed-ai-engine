"""Experimental llama.cpp adapter for the engine inference contract."""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from collections.abc import Iterator

from .contracts_v1 import ChatCompletionRequestV1, ChatCompletionResultV1


class LlamaCppOpenAIProvider:
    def __init__(self, endpoint: str, timeout_seconds: int = 1800) -> None:
        self._endpoint = endpoint
        self._timeout_seconds = timeout_seconds

    def complete(self, request: ChatCompletionRequestV1) -> ChatCompletionResultV1:
        http_request = self._build_request(request, stream=False)
        try:
            with urllib.request.urlopen(http_request, timeout=self._timeout_seconds) as response:
                result = json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"llama-server HTTP {exc.code}: {detail[:1000]}") from exc
        except (urllib.error.URLError, TimeoutError) as exc:
            raise RuntimeError(f"llama-server is unavailable: {exc}") from exc

        choices = result.get("choices") or []
        if not choices:
            raise RuntimeError("llama-server returned no choices.")
        message = choices[0].get("message")
        if not isinstance(message, dict):
            raise RuntimeError("llama-server returned an invalid message.")
        return ChatCompletionResultV1(message=message)

    def stream(self, request: ChatCompletionRequestV1) -> Iterator[str]:
        http_request = self._build_request(request, stream=True)
        try:
            response = urllib.request.urlopen(http_request, timeout=self._timeout_seconds)
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"llama-server HTTP {exc.code}: {detail[:1000]}") from exc
        except (urllib.error.URLError, TimeoutError) as exc:
            raise RuntimeError(f"llama-server is unavailable: {exc}") from exc

        received_done = False
        try:
            with response:
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
                    except json.JSONDecodeError as exc:
                        raise RuntimeError("llama-server sent an invalid SSE event.") from exc

                    if event.get("error"):
                        raise RuntimeError(f"llama-server streaming error: {event['error']}")
                    for choice in event.get("choices") or []:
                        delta = choice.get("delta") or {}
                        content = delta.get("content")
                        if isinstance(content, str) and content:
                            yield content
        except (urllib.error.URLError, TimeoutError) as exc:
            raise RuntimeError(f"llama-server interrupted the stream: {exc}") from exc

        if not received_done:
            raise RuntimeError("llama-server ended the stream without the [DONE] event.")

    def _build_request(self, request: ChatCompletionRequestV1, stream: bool) -> urllib.request.Request:
        body = {
            "model": request.model_id,
            "messages": request.messages,
            "tools": request.tools,
            "tool_choice": request.tool_choice,
            "max_tokens": request.max_tokens,
            "temperature": request.temperature,
            "stream": stream,
        }
        return urllib.request.Request(
            self._endpoint,
            data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
