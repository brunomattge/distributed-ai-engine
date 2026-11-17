#!/usr/bin/env python3
"""Repeat a fixed streamed inference request and save a local JSON report."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import statistics
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from android_telemetry import AndroidTelemetrySampler, load_rpc_devices


DEFAULT_ENDPOINT = "http://127.0.0.1:8080/v1"
DEFAULT_PROMPT = "Reply with only the word OK."
DEFAULT_MODEL_FILE = Path(__file__).resolve().parents[2] / "phase-0" / "models" / "qwen2.5-0.5b-instruct-q4_k_m.gguf"
DEFAULT_OUTPUT_DIR = Path(__file__).resolve().parents[2] / "phase-1" / "results" / "benchmarks"
REQUEST_TIMEOUT_SECONDS = 1800


def _read_json(response) -> dict:
    payload = json.loads(response.read().decode("utf-8"))
    if not isinstance(payload, dict):
        raise RuntimeError("The inference API returned an invalid JSON object.")
    return payload


def list_models(base_url: str, opener=urlopen) -> list[dict]:
    request = Request(f"{base_url.rstrip('/')}/models", method="GET")
    try:
        with opener(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            payload = _read_json(response)
    except HTTPError as exception:
        detail = exception.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"Model discovery failed with HTTP {exception.code}: {detail}") from exception
    except (OSError, URLError, TimeoutError) as exception:
        raise RuntimeError(f"The model server is unavailable: {exception}") from exception

    models = payload.get("data")
    if not isinstance(models, list) or not models:
        raise RuntimeError("The inference API returned no models.")
    valid_models = [model for model in models if isinstance(model, dict) and isinstance(model.get("id"), str) and model["id"]]
    if len(valid_models) != len(models):
        raise RuntimeError("The inference API returned an invalid model entry.")
    return valid_models


def run_sample(
    base_url: str,
    model_id: str,
    prompt: str,
    max_tokens: int,
    temperature: float,
    opener=urlopen,
    clock=time.perf_counter,
    telemetry_sampler: AndroidTelemetrySampler | None = None,
    wall_clock=None,
) -> dict:
    payload = {
        "model": model_id,
        "messages": [{"role": "user", "content": prompt}],
        "max_tokens": max_tokens,
        "temperature": temperature,
        "stream": True,
        "cache_prompt": False,
    }
    request = Request(
        f"{base_url.rstrip('/')}/chat/completions",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json", "Accept": "text/event-stream"},
        method="POST",
    )
    wall_clock = wall_clock or (lambda: datetime.now(timezone.utc))
    if telemetry_sampler is not None:
        telemetry_sampler.capture("before_request")
    request_started_at = wall_clock()
    started_at = clock()
    if telemetry_sampler is not None:
        telemetry_sampler.start_during_request()
    first_content_ms = None
    text_chunks = []
    received_done = False
    request_error: RuntimeError | None = None
    try:
        with opener(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            for raw_line in response:
                line = raw_line.decode("utf-8").strip()
                if not line.startswith("data:"):
                    continue
                data = line[5:].strip()
                if data == "[DONE]":
                    received_done = True
                    break
                try:
                    event = json.loads(data)
                except json.JSONDecodeError as exception:
                    raise RuntimeError("The inference stream contained invalid JSON.") from exception
                if isinstance(event, dict) and "error" in event:
                    error = event["error"]
                    message = error.get("message", "The inference server returned an error.") if isinstance(error, dict) else str(error)
                    raise RuntimeError(message)
                choices = event.get("choices") if isinstance(event, dict) else None
                if not isinstance(choices, list):
                    raise RuntimeError("The inference stream returned an invalid choices collection.")
                for choice in choices:
                    delta = choice.get("delta") if isinstance(choice, dict) else None
                    content = delta.get("content") if isinstance(delta, dict) else None
                    if isinstance(content, str) and content:
                        if first_content_ms is None:
                            first_content_ms = (clock() - started_at) * 1000
                        text_chunks.append(content)
    except HTTPError as exception:
        detail = exception.read().decode("utf-8", errors="replace")
        request_error = RuntimeError(f"Completion failed with HTTP {exception.code}: {detail}")
    except (OSError, URLError, TimeoutError) as exception:
        request_error = RuntimeError(f"The inference stream was interrupted: {exception}")
    except RuntimeError as exception:
        request_error = exception

    if request_error is None and not received_done:
        request_error = RuntimeError("The inference stream ended without the [DONE] marker.")
    if request_error is None and not text_chunks:
        request_error = RuntimeError("The inference stream completed without returning text.")
    elapsed_seconds = clock() - started_at
    request_completed_at = wall_clock()
    telemetry_report = None
    if telemetry_sampler is not None:
        telemetry_sampler.stop_during_request()
        telemetry_sampler.capture("after_request")
        telemetry_report = telemetry_sampler.report(request_started_at, request_completed_at)
    if request_error is not None:
        if telemetry_report is not None:
            request_error.device_telemetry = telemetry_report
        raise request_error

    result = {
        "ok": True,
        "first_content_ms": round(first_content_ms, 3) if first_content_ms is not None else None,
        "elapsed_seconds": round(elapsed_seconds, 6),
        "content": "".join(text_chunks),
        "stream_chunks": len(text_chunks),
        "request_started_at_utc": request_started_at.isoformat(),
        "request_completed_at_utc": request_completed_at.isoformat(),
    }
    if telemetry_report is not None:
        result["device_telemetry"] = telemetry_report
    return result


def summarize_samples(samples: list[dict]) -> dict:
    successful = [sample for sample in samples if sample.get("ok")]
    ttfc = [sample["first_content_ms"] for sample in successful if sample.get("first_content_ms") is not None]
    elapsed = [sample["elapsed_seconds"] for sample in successful if isinstance(sample.get("elapsed_seconds"), (int, float))]

    def median_or_none(values: list[float]) -> float | None:
        return round(statistics.median(values), 3) if values else None

    return {
        "successful_requests": len(successful),
        "failed_requests": len(samples) - len(successful),
        "median_first_content_ms": median_or_none(ttfc),
        "median_total_seconds": median_or_none(elapsed),
        "min_total_seconds": round(min(elapsed), 6) if elapsed else None,
        "max_total_seconds": round(max(elapsed), 6) if elapsed else None,
    }


def _format_range(summary: dict, unit: str, digits: int = 1) -> str:
    minimum = summary.get("min")
    maximum = summary.get("max")
    mean = summary.get("mean")
    if minimum is None or maximum is None:
        return "not available"
    if minimum == maximum:
        return f"{minimum:.{digits}f} {unit}"
    return f"{minimum:.{digits}f}–{maximum:.{digits}f} {unit} (mean {mean:.{digits}f})"


def write_english_summary(report: dict, report_path: Path) -> Path:
    summary_path = report_path.with_name(f"{report_path.stem}-summary-en-us.log")
    summary = report["summary"]
    lines = [
        "Distributed inference benchmark summary",
        f"Result: {'COMPLETED' if summary['failed_requests'] == 0 else 'PARTIAL/FAILED'}",
        f"Created at (UTC): {report['created_at_utc']}",
        f"Model: {report['model']['id']} ({report['model']['file_name']})",
        f"Selected RPC workers: {report['selected_rpc_workers']}",
        f"Transport: {report['transport']}",
        f"Requests: {summary['successful_requests']} successful; {summary['failed_requests']} failed",
        f"Median first content: {summary['median_first_content_ms'] if summary['median_first_content_ms'] is not None else 'not recorded'} ms",
        f"Median total time: {summary['median_total_seconds'] if summary['median_total_seconds'] is not None else 'not recorded'} s",
        "Device-wide telemetry by request (MemAvailable and Android SKIN sensor):",
    ]
    for sample in report["samples"]:
        request_number = sample.get("sample", "unknown")
        if sample.get("ok"):
            lines.append(
                f"Request {request_number}: first content {sample['first_content_ms'] if sample['first_content_ms'] is not None else 'not recorded'} ms; "
                f"total {sample['elapsed_seconds']:.3f} s"
            )
        else:
            lines.append(f"Request {request_number}: FAILED; telemetry retained when available")
        telemetry = sample.get("device_telemetry")
        if not isinstance(telemetry, dict) or not telemetry.get("devices"):
            lines.append("  No per-device telemetry was recorded.")
            continue
        for device in telemetry["devices"]:
            ram = _format_range(device["available_ram_mib"], "MiB")
            temperature = _format_range(device["surface_temperature_celsius"], "°C")
            lines.append(
                f"  {device['rpc_worker']} ({device['hardware_model']}, Android {device['android_version']}): "
                f"RAM {ram}; surface temperature {temperature}; "
                f"during-request samples {device['during_request_sample_count']}"
            )
    lines.extend([
        "RAM and temperature are device-wide readings, not process/model-only metrics.",
        "ADB USB sampling adds a small amount of transport traffic and may affect timings slightly.",
        f"Detailed timestamped report: {report_path}",
    ])
    summary_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return summary_path


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as model_file:
        for chunk in iter(lambda: model_file.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_arguments(arguments: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Repeat uncached streamed inference requests and write a local JSON report.")
    parser.add_argument("--workers", type=int, choices=(1, 2, 3), required=True, help="Selected RPC count for the already-running session.")
    parser.add_argument("--model-file", type=Path, default=DEFAULT_MODEL_FILE, help="Local GGUF file used by the running session.")
    parser.add_argument("--model-license", required=True, help="License identifier for the selected model weights.")
    parser.add_argument("--runtime-revision", required=True, help="llama.cpp source revision used to build the running server.")
    parser.add_argument("--hardware-cohort", required=True, help="Human-readable device cohort and transport; never include serial numbers.")
    parser.add_argument("--context", type=int, default=512, help="Context size configured for the running model server.")
    parser.add_argument("--endpoint", default=DEFAULT_ENDPOINT, help="llama.cpp OpenAI-compatible v1 endpoint.")
    parser.add_argument("--prompt", default=DEFAULT_PROMPT, help="Fixed non-sensitive benchmark prompt; it is stored in the report.")
    parser.add_argument("--max-tokens", type=int, default=2, help="Generation limit for each request.")
    parser.add_argument("--temperature", type=float, default=0.2)
    parser.add_argument("--repetitions", type=int, default=3)
    parser.add_argument("--devices-file", type=Path, required=True, help="Local active-device map for this RPC session; serials are omitted from the report.")
    parser.add_argument("--adb", default=os.environ.get("ANDROID_ADB") or shutil.which("adb") or "adb", help="ADB executable used for device telemetry.")
    parser.add_argument("--telemetry-interval-seconds", type=float, default=0.5, help="Sampling period during each request.")
    parser.add_argument("--output", type=Path, help="JSON report path (default: ignored local results folder).")
    options = parser.parse_args(arguments)
    if not options.model_license.strip():
        parser.error("--model-license must not be empty.")
    if not options.runtime_revision.strip():
        parser.error("--runtime-revision must not be empty.")
    if not options.hardware_cohort.strip():
        parser.error("--hardware-cohort must not be empty.")
    if not options.prompt.strip():
        parser.error("--prompt must not be empty.")
    if not 1 <= options.context <= 131072:
        parser.error("--context must be between 1 and 131072.")
    if not 1 <= options.max_tokens <= 2048:
        parser.error("--max-tokens must be between 1 and 2048.")
    if not 1 <= options.repetitions <= 100:
        parser.error("--repetitions must be between 1 and 100.")
    if not 0 <= options.temperature <= 2:
        parser.error("--temperature must be between 0 and 2.")
    if not options.devices_file.is_file():
        parser.error(f"Device map does not exist: {options.devices_file}")
    if not 0 < options.telemetry_interval_seconds <= 60:
        parser.error("--telemetry-interval-seconds must be greater than 0 and no more than 60.")
    if not options.model_file.is_file():
        parser.error(f"Model file does not exist: {options.model_file}")
    return options


def main(arguments: list[str] | None = None) -> int:
    options = parse_arguments(arguments)
    output_path = options.output or DEFAULT_OUTPUT_DIR / f"worker-count-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')}-{options.workers}-rpc.json"
    output_path.parent.mkdir(parents=True, exist_ok=True)

    try:
        rpc_devices = load_rpc_devices(options.devices_file, options.workers)
        models = list_models(options.endpoint)
        model_id = models[0]["id"]
        samples = []
        print(f"Benchmarking model={model_id}; selected_rpc_workers={options.workers}; repetitions={options.repetitions}")
        for index in range(1, options.repetitions + 1):
            try:
                telemetry_sampler = AndroidTelemetrySampler(
                    options.adb,
                    rpc_devices,
                    interval_seconds=options.telemetry_interval_seconds,
                )
                sample = run_sample(
                    options.endpoint,
                    model_id,
                    options.prompt,
                    options.max_tokens,
                    options.temperature,
                    telemetry_sampler=telemetry_sampler,
                )
                sample["sample"] = index
                samples.append(sample)
                print(
                    f"Request {index}/{options.repetitions}: HTTP success; "
                    f"first_content={sample['first_content_ms'] if sample['first_content_ms'] is not None else 'not recorded'} ms; "
                    f"total={sample['elapsed_seconds']:.3f} s; "
                    f"device_samples={sum(device['during_request_sample_count'] for device in sample['device_telemetry']['devices'])}; "
                    f"response={sample['content']!r}"
                )
            except RuntimeError as exception:
                sample = {"sample": index, "ok": False, "error": str(exception)}
                telemetry = getattr(exception, "device_telemetry", None)
                if telemetry is not None:
                    sample["request_started_at_utc"] = telemetry["request_started_at_utc"]
                    sample["request_completed_at_utc"] = telemetry["request_completed_at_utc"]
                    sample["device_telemetry"] = telemetry
                samples.append(sample)
                print(f"Request {index}/{options.repetitions}: FAILED: {exception}", file=sys.stderr)

        report = {
            "schema_version": 2,
            "created_at_utc": datetime.now(timezone.utc).isoformat(),
            "endpoint": options.endpoint,
            "model": {
                "id": model_id,
                "file_name": options.model_file.name,
                "sha256": sha256_file(options.model_file),
                "license": options.model_license,
            },
            "runtime_revision": options.runtime_revision,
            "hardware_cohort": options.hardware_cohort,
            "transport": "USB",
            "selected_rpc_workers": options.workers,
            "telemetry": {
                "source": "Android /proc/meminfo MemAvailable and dumpsys thermalservice SKIN over ADB USB",
                "sampling_interval_ms": round(options.telemetry_interval_seconds * 1000),
                "selected_devices": [
                    {"rpc_worker": device["rpc_worker"], "hardware_model": device["hardware_model"], "android_version": device["android_version"]}
                    for device in rpc_devices
                ],
                "device_serials_included": False,
            },
            "context_tokens": options.context,
            "prompt": options.prompt,
            "max_tokens": options.max_tokens,
            "temperature": options.temperature,
            "prompt_cache": "disabled per request",
            "samples": samples,
            "summary": summarize_samples(samples),
            "limitations": [
                "Run startup separately and retain its English session summary and coordinator/worker logs.",
                "This client measures HTTP time to first streamed content and end-to-end elapsed time; it does not derive model token throughput from chunk counts.",
                "RAM and surface temperature are device-wide Android readings sampled before, during, and after each request; they are not process-only or model-only measurements.",
                "ADB sampling adds a small amount of USB/ADB traffic and may affect request timings slightly.",
                "Worker count is supplied by the operator and must match the running session summary.",
            ],
        }
        output_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        summary_path = write_english_summary(report, output_path)
        summary = report["summary"]
        print(
            f"Summary: successful={summary['successful_requests']}; failed={summary['failed_requests']}; "
            f"median_ttfc={summary['median_first_content_ms'] if summary['median_first_content_ms'] is not None else 'not recorded'} ms; "
            f"median_total={summary['median_total_seconds'] if summary['median_total_seconds'] is not None else 'not recorded'} s"
        )
        print(f"Report saved: {output_path}")
        print(f"English summary saved: {summary_path}")
        return 0 if summary["successful_requests"] == options.repetitions else 1
    except (OSError, RuntimeError, ValueError) as exception:
        print(f"Benchmark failed: {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
