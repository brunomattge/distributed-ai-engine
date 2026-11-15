#!/usr/bin/env python3
"""Local chat API and one allowlisted, read-only agent tool."""

from __future__ import annotations

import json
import os
import subprocess
import time
from urllib.error import HTTPError, URLError
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from urllib.request import urlopen

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


_AGENT_RUNTIME = create_runtime()


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
