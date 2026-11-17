import io
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import Mock, patch
from urllib.request import Request


CLIENTS_DIR = Path(__file__).resolve().parents[2] / "examples" / "clients"
sys.path.insert(0, str(CLIENTS_DIR))
import stream_completion  # noqa: E402


class StreamParserTests(unittest.TestCase):
    def test_yields_text_deltas_until_done(self):
        stream = io.BytesIO(
            b'data: {"choices":[{"delta":{"content":"Hello "}}]}\n\n'
            b'data: {"choices":[{"delta":{"content":"engine."}}]}\n\n'
            b"data: [DONE]\n\n"
        )

        self.assertEqual(list(stream_completion.iter_streamed_text(stream)), ["Hello ", "engine."])

    def test_raises_on_structured_stream_error(self):
        stream = io.BytesIO(b'data: {"error":{"message":"Session degraded."}}\n\n')

        with self.assertRaisesRegex(RuntimeError, "Session degraded"):
            list(stream_completion.iter_streamed_text(stream))

    def test_raises_when_the_done_marker_is_missing(self):
        stream = io.BytesIO(b'data: {"choices":[{"delta":{"content":"partial"}}]}\n\n')

        with self.assertRaisesRegex(RuntimeError, "without the \\[DONE\\] marker"):
            list(stream_completion.iter_streamed_text(stream))


class ClientRequestTests(unittest.TestCase):
    def test_list_models_uses_the_versioned_endpoint(self):
        response = Mock()
        response.read.return_value = b'{"data":[{"id":"qwen-fast"}]}'
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)

        with patch.object(stream_completion, "urlopen", return_value=response) as open_url:
            models = stream_completion.list_models("http://127.0.0.1:5100/v1/")

        request = open_url.call_args.args[0]
        self.assertIsInstance(request, Request)
        self.assertEqual(request.full_url, "http://127.0.0.1:5100/v1/models")
        self.assertEqual(models[0]["id"], "qwen-fast")

    def test_stream_completion_sends_selected_model_and_prompt(self):
        response = io.BytesIO(b'data: {"choices":[{"delta":{"content":"OK"}}]}\n\ndata: [DONE]\n\n')
        response.__enter__ = lambda: response
        response.__exit__ = lambda *_: False

        with patch.object(stream_completion, "urlopen", return_value=response) as open_url:
            result = list(stream_completion.stream_completion(
                "http://127.0.0.1:5100/v1", "qwen-fast", "Return OK.", 12))

        request = open_url.call_args.args[0]
        payload = json.loads(request.data.decode("utf-8"))
        self.assertEqual(request.full_url, "http://127.0.0.1:5100/v1/chat/completions")
        self.assertEqual(payload["model"], "qwen-fast")
        self.assertEqual(payload["messages"][0]["content"], "Return OK.")
        self.assertTrue(payload["stream"])
        self.assertEqual(result, ["OK"])


if __name__ == "__main__":
    unittest.main()
