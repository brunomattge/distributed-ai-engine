"""Core inference contracts for provider-independent chat completion."""

from __future__ import annotations

from dataclasses import dataclass
from collections.abc import Iterator
from typing import Any, Protocol


@dataclass(frozen=True)
class ChatCompletionRequestV1:
    model_id: str
    messages: list[dict[str, Any]]
    tools: list[dict[str, Any]]
    tool_choice: str
    max_tokens: int = 80
    temperature: float = 0.2
    contract_version: str = "v1"


@dataclass(frozen=True)
class ChatCompletionResultV1:
    message: dict[str, Any]
    contract_version: str = "v1"


class ChatProviderV1(Protocol):
    """Synchronous provider contract; callers do not depend on an LLM vendor."""

    def complete(self, request: ChatCompletionRequestV1) -> ChatCompletionResultV1:
        ...


class StreamingChatProviderV1(ChatProviderV1, Protocol):
    """Optional v1 provider capability for text deltas from one completion."""

    def stream(self, request: ChatCompletionRequestV1) -> Iterator[str]:
        ...
