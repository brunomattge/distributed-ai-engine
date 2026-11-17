#!/usr/bin/env python3
"""Local chat API and one allowlisted, read-only agent tool."""

from __future__ import annotations

import json
import os
import subprocess
import time
import uuid
from urllib.error import HTTPError, URLError
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from urllib.request import urlopen

from inference.contracts_v1 import ChatCompletionRequestV1
from inference.llama_cpp_provider import LlamaCppOpenAIProvider
from agent_runtime.runtime_v1 import AgentRuntimeV1


PHASE_DIR = Path(__file__).resolve().parents[2]
PHASE1_DIR = PHASE_DIR.parent / "phase-1"
CLUSTER_CTL = PHASE1_DIR / "clusterctl.sh"
LLAMA_API = os.environ.get("LLAMA_API_URL", "http://127.0.0.1:8080/v1/chat/completions")
MODEL_ID = os.environ.get("LLAMA_MODEL_ID", "distributed-qwen")
MODEL_NAME = os.environ.get("LLAMA_MODEL_NAME", MODEL_ID)
EVENT_LOG = Path(os.environ.get("CHAT_EVENT_LOG", str(PHASE_DIR / "chat-events.jsonl")))
PORT = int(os.environ.get("CHAT_PORT", "8000"))
LLAMA_HEALTH = LLAMA_API.split("/v1/", 1)[0] + "/health"
SUPERVISOR_STATUS_FILE = Path(
    os.environ.get(
        "INFERENCE_SUPERVISOR_STATUS_FILE",
        str(PHASE1_DIR / ".runtime" / "supervisor-status.json"),
    )
)


def query_model_status() -> dict:
    """Read llama-server readiness without opening connections to RPC workers."""
    try:
        with urlopen(LLAMA_HEALTH, timeout=2) as response:
            payload = json.loads(response.read().decode("utf-8"))
        ready = payload.get("status") == "ok"
        return {"state": "ready" if ready else "loading", "model_id": MODEL_ID, "model_name": MODEL_NAME}
    except HTTPError as exc:
        if exc.code == 503:
            return {"state": "loading", "model_id": MODEL_ID, "model_name": MODEL_NAME}
        return {"state": "unavailable", "model_id": MODEL_ID, "model_name": MODEL_NAME}
    except (OSError, URLError, TimeoutError, json.JSONDecodeError):
        return {"state": "unavailable", "model_id": MODEL_ID, "model_name": MODEL_NAME}


def query_supervisor_status() -> dict:
    """Expose the local launcher's restart state to the platform API."""
    try:
        payload = json.loads(SUPERVISOR_STATUS_FILE.read_text(encoding="utf-8"))
        state = payload.get("state")
        generation = payload.get("generation")
        message = payload.get("message")
        if state not in {"starting", "ready", "degraded", "waiting_for_devices", "restarting", "failed", "stopped"}:
            raise ValueError("invalid supervisor state")
        if not isinstance(generation, int) or generation < 0:
            raise ValueError("invalid supervisor generation")
        return {
            "supervisor_state": state,
            "supervisor_generation": generation,
            "supervisor_message": message if isinstance(message, str) else None,
        }
    except (OSError, json.JSONDecodeError, ValueError):
        return {"supervisor_state": "unmanaged", "supervisor_generation": 0, "supervisor_message": None}


def query_cluster_status() -> dict:
    try:
        result = subprocess.run(
            [str(CLUSTER_CTL), "status"],
            cwd=PHASE1_DIR,
            capture_output=True,
            text=True,
            timeout=30,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        return {"healthy": False, "status": f"Failed to query the cluster: {exc}"}

    lines = [
        line.strip()
        for line in result.stdout.splitlines()
        if line.startswith("Worker ") or line.startswith("Cluster:")
    ]
    return {
        "healthy": result.returncode == 0,
        "status": "\n".join(lines) if lines else "No cluster status is available.",
    }


def append_event(event: dict) -> None:
    try:
        EVENT_LOG.parent.mkdir(parents=True, exist_ok=True)
        with EVENT_LOG.open("a", encoding="utf-8") as stream:
            entry = {"timestamp": int(time.time()), **event}
            stream.write(json.dumps(entry, ensure_ascii=False) + "\n")
    except OSError:
        print("[chat-api] event_log_write_failed", flush=True)
    if event.get("event") == "chat_first_content":
        print(
            "[chat-api] first_content "
            f"model={event.get('model', 'unknown')} "
            f"time_to_first_content_ms={event.get('time_to_first_content_ms', 'not recorded')}",
            flush=True,
        )


def create_runtime(provider=None) -> AgentRuntimeV1:
    return AgentRuntimeV1(
        provider=provider or LlamaCppOpenAIProvider(LLAMA_API),
        status_query=query_cluster_status,
        event_sink=append_event,
        model_id=MODEL_ID,
    )


_INFERENCE_PROVIDER = LlamaCppOpenAIProvider(LLAMA_API)
_AGENT_RUNTIME = create_runtime(provider=_INFERENCE_PROVIDER)


def chat(
    messages: list[dict],
    runtime: AgentRuntimeV1 | None = None,
    memories: list[str] | None = None,
) -> dict:
    return (runtime or _AGENT_RUNTIME).chat(messages, memories=memories)


class Handler(BaseHTTPRequestHandler):
    server_version = "DistributedAIMVP/0.1"

    def _json(self, status: int, payload: dict) -> None:
        encoded = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(encoded)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(encoded)

    def do_GET(self) -> None:
        if self.path == "/v1/models":
            self._json(200, {
                "object": "list",
                "data": [{
                    "id": MODEL_ID,
                    "object": "model",
                    "created": 0,
                    "owned_by": "distributed-ai-engine",
                }],
            })
            return
        if self.path == "/api/health":
            self._json(200, {"api": "online", **query_model_status(), **query_supervisor_status()})
            return
        if self.path in {"/", "/index.html"}:
            page = (Path(__file__).resolve().parent / "index.html").read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(page)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(page)
            return
        self._json(404, {"error": "Not found."})

    def do_POST(self) -> None:
        if self.path == "/v1/chat/completions":
            self._handle_engine_completion()
            return
        if self.path == "/api/chat/stream":
            self._handle_chat_stream()
            return
        if self.path != "/api/chat":
            self._json(404, {"error": "Not found."})
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length < 1 or length > 256 * 1024:
                raise ValueError("The message size is invalid.")
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
            self._json(200, chat(payload.get("messages"), memories=payload.get("memories")))
        except (ValueError, json.JSONDecodeError) as exc:
            print(f"[chat-api] request rejected: {exc}", flush=True)
            self._json(400, {"error": str(exc)})
        except Exception as exc:  # Return a useful local diagnostic to the UI.
            self._json(502, {"error": str(exc)})

    def _handle_engine_completion(self) -> None:
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length < 1 or length > 256 * 1024:
                raise ValueError("The request size is invalid.")
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
            if not isinstance(payload, dict):
                raise ValueError("The request body must be a JSON object.")
            if payload.get("model") != MODEL_ID:
                self._json(404, {"error": {"message": "The requested model is not available.", "type": "model_not_found", "code": "model_not_found"}})
                return
            messages = payload.get("messages")
            if not isinstance(messages, list) or not messages or any(
                not isinstance(message, dict)
                or message.get("role") not in {"system", "user", "assistant"}
                or not isinstance(message.get("content"), str)
                or not message["content"].strip()
                or len(message["content"]) > 12000
                for message in messages
            ):
                raise ValueError("messages must be a non-empty array of supported text entries.")
            if payload.get("tools") or payload.get("tool_choice") not in (None, "none"):
                self._json(422, {"error": {"message": "Tools are not supported by the engine inference API.", "type": "unsupported_feature", "code": "tools_not_supported"}})
                return
            if "max_tokens" in payload and "max_completion_tokens" in payload:
                raise ValueError("Use max_completion_tokens or max_tokens, not both.")
            token_limit_field = "max_completion_tokens" if "max_completion_tokens" in payload else "max_tokens"
            max_tokens = payload.get(token_limit_field, 80)
            temperature = payload.get("temperature", 0.2)
            if isinstance(max_tokens, bool) or not isinstance(max_tokens, int) or not 1 <= max_tokens <= 2048:
                raise ValueError(f"{token_limit_field} must be between 1 and 2048.")
            if isinstance(temperature, bool) or not isinstance(temperature, (int, float)) or not 0 <= temperature <= 2:
                raise ValueError("temperature must be between 0 and 2.")
            request = ChatCompletionRequestV1(
                model_id=MODEL_ID,
                messages=messages,
                tools=[],
                tool_choice="none",
                max_tokens=max_tokens,
                temperature=float(temperature),
            )
            model_status = query_model_status()
            cluster_status = query_cluster_status()
            supervisor_status = query_supervisor_status()
            supervised_state = supervisor_status.get("supervisor_state")
            if (
                model_status.get("state") != "ready"
                or not cluster_status.get("healthy")
                or supervised_state not in {"ready", "unmanaged"}
            ):
                self._json(503, {"error": {
                    "message": "Distributed inference is not ready. Check model and Android worker status.",
                    "type": "service_unavailable",
                    "code": "inference_unavailable",
                }})
                return
            if payload.get("stream", False):
                self._stream_engine_completion(request)
            else:
                result = _INFERENCE_PROVIDER.complete(request).message
                content = result.get("content")
                if not isinstance(content, str) or not content.strip():
                    raise RuntimeError("The model returned no text.")
                created = int(time.time())
                self._json(200, {
                    "id": f"chatcmpl-{uuid.uuid4().hex}",
                    "object": "chat.completion",
                    "created": created,
                    "model": MODEL_ID,
                    "choices": [{
                        "index": 0,
                        "message": {"role": "assistant", "content": content},
                        "finish_reason": "stop",
                    }],
                })
        except (ValueError, json.JSONDecodeError) as exc:
            self._json(400, {"error": {"message": str(exc), "type": "invalid_request_error", "code": "invalid_request"}})
        except Exception as exc:
            self._json(502, {"error": {"message": str(exc), "type": "engine_error", "code": "inference_failed"}})

    def _stream_engine_completion(self, request: ChatCompletionRequestV1) -> None:
        completion_id = f"chatcmpl-{uuid.uuid4().hex}"
        created = int(time.time())
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache, no-transform")
        self.send_header("X-Accel-Buffering", "no")
        self.end_headers()
        try:
            received_content = False
            for text in _INFERENCE_PROVIDER.stream(request):
                received_content = received_content or bool(text)
                chunk = {
                    "id": completion_id,
                    "object": "chat.completion.chunk",
                    "created": created,
                    "model": MODEL_ID,
                    "choices": [{"index": 0, "delta": {"content": text}, "finish_reason": None}],
                }
                self.wfile.write(f"data: {json.dumps(chunk, ensure_ascii=False)}\n\n".encode("utf-8"))
                self.wfile.flush()
            if not received_content:
                raise RuntimeError("The model returned no text.")
            final_chunk = {
                "id": completion_id,
                "object": "chat.completion.chunk",
                "created": created,
                "model": MODEL_ID,
                "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
            }
            self.wfile.write(f"data: {json.dumps(final_chunk)}\n\ndata: [DONE]\n\n".encode("utf-8"))
            self.wfile.flush()
        except Exception as exc:
            error = {"error": {"message": str(exc), "type": "engine_error", "code": "inference_failed"}}
            self.wfile.write(f"data: {json.dumps(error, ensure_ascii=False)}\n\ndata: [DONE]\n\n".encode("utf-8"))
            self.wfile.flush()

    def _handle_chat_stream(self) -> None:
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length < 1 or length > 256 * 1024:
                raise ValueError("The message size is invalid.")
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
            if not isinstance(payload, dict):
                raise ValueError("The request body must be a JSON object.")
            events = _AGENT_RUNTIME.chat_stream(payload.get("messages"), memories=payload.get("memories"))
        except (ValueError, json.JSONDecodeError) as exc:
            print(f"[chat-api] streaming request rejected: {exc}", flush=True)
            self._json(400, {"error": str(exc)})
            return

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream; charset=utf-8")
        self.send_header("Cache-Control", "no-cache, no-transform")
        self.send_header("X-Accel-Buffering", "no")
        self.send_header("Connection", "close")
        self.end_headers()

        try:
            for event in events:
                self._write_sse(event["event"], {key: value for key, value in event.items() if key != "event"})
            self._write_sse("done", {"done": True})
        except Exception as exc:
            self._write_sse("error", {"error": str(exc)})

    def _write_sse(self, event_name: str, payload: dict) -> None:
        data = json.dumps(payload, ensure_ascii=False)
        self.wfile.write(f"event: {event_name}\ndata: {data}\n\n".encode("utf-8"))
        self.wfile.flush()

    def log_message(self, format_string: str, *args) -> None:
        print("[chat-api] " + (format_string % args), flush=True)


if __name__ == "__main__":
    server = HTTPServer(("127.0.0.1", PORT), Handler)
    print(f"Local chat available at http://127.0.0.1:{PORT}", flush=True)
    server.serve_forever()
