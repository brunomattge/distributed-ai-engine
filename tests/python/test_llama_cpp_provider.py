import io
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import patch
from urllib.error import HTTPError, URLError
from urllib.request import Request


PHASE_DIR = Path(__file__).resolve().parents[2] / "experiments" / "phase-3" / "python"
sys.path.insert(0, str(PHASE_DIR))
from inference.contracts_v1 import ChatCompletionRequestV1  # noqa: E402
from inference.llama_cpp_provider import LlamaCppOpenAIProvider  # noqa: E402
from agent_runtime.contracts_v1 import ChatCompletionRequestV1 as LegacyChatCompletionRequestV1  # noqa: E402
from agent_runtime.llama_cpp_provider import LlamaCppOpenAIProvider as LegacyLlamaCppOpenAIProvider  # noqa: E402


class LlamaCppProviderTests(unittest.TestCase):
    def setUp(self):
        self.provider = LlamaCppOpenAIProvider("http://127.0.0.1:8080/v1/chat/completions", timeout_seconds=12)
        self.contract = ChatCompletionRequestV1(
            model_id="distributed-test",
            messages=[{"role": "user", "content": "Hello"}],
            tools=[],
            tool_choice="auto",
            max_tokens=17,
            temperature=0.4,
        )

    def test_agent_runtime_compatibility_imports_reexport_inference_types(self):
        self.assertIs(LegacyChatCompletionRequestV1, ChatCompletionRequestV1)
        self.assertIs(LegacyLlamaCppOpenAIProvider, LlamaCppOpenAIProvider)

    def test_sends_openai_compatible_body_and_returns_selected_message(self):
        response = io.BytesIO(
            json.dumps({"choices": [{"message": {"role": "assistant", "content": "Hi."}}]}).encode("utf-8")
        )
        with patch("inference.llama_cpp_provider.urllib.request.urlopen", return_value=response) as urlopen_mock:
            result = self.provider.complete(self.contract)

        request = urlopen_mock.call_args.args[0]
        self.assertIsInstance(request, Request)
        self.assertEqual(request.full_url, "http://127.0.0.1:8080/v1/chat/completions")
        self.assertEqual(request.get_method(), "POST")
        self.assertEqual(request.get_header("Content-type"), "application/json")
        self.assertEqual(urlopen_mock.call_args.kwargs["timeout"], 12)
        body = json.loads(request.data.decode("utf-8"))
        self.assertEqual(body["model"], "distributed-test")
        self.assertEqual(body["messages"], self.contract.messages)
        self.assertEqual(body["max_tokens"], 17)
        self.assertEqual(body["temperature"], 0.4)
        self.assertFalse(body["stream"])
        self.assertEqual(result.message, {"role": "assistant", "content": "Hi."})
        self.assertEqual(result.contract_version, "v1")

    def test_reports_http_error_detail(self):
        error = HTTPError("http://localhost", 503, "Unavailable", {}, io.BytesIO(b"busy"))
        with patch("inference.llama_cpp_provider.urllib.request.urlopen", side_effect=error):
            with self.assertRaisesRegex(RuntimeError, "llama-server HTTP 503: busy"):
                self.provider.complete(self.contract)

    def test_reports_connection_error(self):
        with patch(
            "inference.llama_cpp_provider.urllib.request.urlopen",
            side_effect=URLError("connection refused"),
        ):
            with self.assertRaisesRegex(RuntimeError, "llama-server is unavailable"):
                self.provider.complete(self.contract)

    def test_rejects_response_without_message(self):
        response = io.BytesIO(b'{"choices": []}')
        with patch("inference.llama_cpp_provider.urllib.request.urlopen", return_value=response):
            with self.assertRaisesRegex(RuntimeError, "returned no choices"):
                self.provider.complete(self.contract)

    def test_stream_decodes_content_deltas_in_order_and_requires_done(self):
        events = [
            b": keepalive\n\n",
            b'data: {"choices":[{"delta":{"role":"assistant"}}]}\n\n',
            'data: {"choices":[{"delta":{"content":"Hello "}}]}\n\n'.encode("utf-8"),
            'data: {"choices":[{"delta":{"content":"world"}}]}\n\n'.encode("utf-8"),
            b"data: [DONE]\n\n",
        ]
        response = io.BytesIO(b"".join(events))
        with patch("inference.llama_cpp_provider.urllib.request.urlopen", return_value=response) as urlopen_mock:
            chunks = list(self.provider.stream(self.contract))

        self.assertEqual(chunks, ["Hello ", "world"])
        request = urlopen_mock.call_args.args[0]
        self.assertTrue(json.loads(request.data.decode("utf-8"))["stream"])
        self.assertEqual(urlopen_mock.call_args.kwargs["timeout"], 12)

    def test_stream_rejects_invalid_sse_json(self):
        response = io.BytesIO(b"data: {broken}\n\n")
        with patch("inference.llama_cpp_provider.urllib.request.urlopen", return_value=response):
            with self.assertRaisesRegex(RuntimeError, "invalid SSE event"):
                list(self.provider.stream(self.contract))

    def test_stream_rejects_eof_without_done(self):
        response = io.BytesIO(b'data: {"choices":[{"delta":{"content":"partial"}}]}\n\n')
        with patch("inference.llama_cpp_provider.urllib.request.urlopen", return_value=response):
            with self.assertRaisesRegex(RuntimeError, "without the \\[DONE\\] event"):
                list(self.provider.stream(self.contract))


if __name__ == "__main__":
    unittest.main()
