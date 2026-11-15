"""Compatibility imports for the inference contract used by the chat runtime."""

from inference.contracts_v1 import (
    ChatCompletionRequestV1,
    ChatCompletionResultV1,
    ChatProviderV1,
    StreamingChatProviderV1,
)

__all__ = [
    "ChatCompletionRequestV1",
    "ChatCompletionResultV1",
    "ChatProviderV1",
    "StreamingChatProviderV1",
]
