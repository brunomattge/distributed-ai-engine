"""Sample Android RAM and surface temperature around a single inference request."""

from __future__ import annotations

import subprocess
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]

import sys

sys.path.insert(0, str(REPOSITORY_ROOT / "tools"))
from adb_node_bridge import parse_available_ram_mib, parse_skin_temperature  # noqa: E402


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _iso(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat()


def display_hardware_model(value: str) -> str:
    normalized = value.replace("_", "-")
    return {
        "SM-A075M": "Galaxy A07",
        "SM-A065M": "Galaxy A06",
    }.get(normalized, normalized or "Android device")


def load_rpc_devices(path: Path, workers: int) -> list[dict[str, str]]:
    """Read the local active-device map; serials stay in memory and never enter reports."""
    devices = []
    for line in path.read_text(encoding="utf-8").splitlines():
        fields = line.split("|")
        if len(fields) < 3 or not fields[0]:
            continue
        devices.append({
            "serial": fields[0],
            "hardware_model": display_hardware_model(fields[1]),
            "android_version": fields[2],
        })
    if len(devices) < workers:
        raise ValueError(f"The device map contains {len(devices)} entries for {workers} selected RPC workers.")
    for index, device in enumerate(devices[:workers]):
        device["rpc_worker"] = f"RPC{index}"
    return devices[:workers]


class AndroidTelemetrySampler:
    """Capture before/during/after samples concurrently without recording device serials."""

    def __init__(
        self,
        adb: str,
        devices: list[dict[str, str]],
        interval_seconds: float = 0.5,
        runner: Callable[..., Any] = subprocess.run,
        wall_clock: Callable[[], datetime] = utc_now,
    ):
        if not devices:
            raise ValueError("At least one selected Android RPC device is required for telemetry.")
        if interval_seconds <= 0:
            raise ValueError("Telemetry interval must be positive.")
        self.adb = adb
        self.devices = devices
        self.interval_seconds = interval_seconds
        self.runner = runner
        self.wall_clock = wall_clock
        self._samples: list[dict[str, Any]] = []
        self._samples_lock = threading.Lock()
        self._stop_event = threading.Event()
        self._thread: threading.Thread | None = None

    def _sample_device(self, device: dict[str, str], phase: str) -> dict[str, Any]:
        started_at = self.wall_clock()
        started = time.perf_counter()
        sample: dict[str, Any] = {
            "rpc_worker": device["rpc_worker"],
            "hardware_model": device["hardware_model"],
            "android_version": device["android_version"],
            "phase": phase,
            "sample_started_at_utc": _iso(started_at),
            "available_ram_mib": None,
            "surface_temperature_celsius": None,
            "temperature_sensor": None,
            "collection_duration_ms": None,
            "collection_status": "unavailable",
            "unavailable_metrics": [],
        }
        try:
            result = self.runner(
                [self.adb, "-s", device["serial"], "shell", "cat /proc/meminfo; dumpsys thermalservice"],
                capture_output=True,
                text=True,
                timeout=5,
                check=False,
            )
            if result.returncode != 0:
                sample["unavailable_metrics"] = ["ram", "surface_temperature"]
                sample["collection_error"] = f"adb_exit_{result.returncode}"
            else:
                sample["available_ram_mib"] = parse_available_ram_mib(result.stdout)
                sample["surface_temperature_celsius"] = parse_skin_temperature(result.stdout)
                if sample["surface_temperature_celsius"] is not None:
                    sample["temperature_sensor"] = "SKIN"
                if sample["available_ram_mib"] is None:
                    sample["unavailable_metrics"].append("ram")
                if sample["surface_temperature_celsius"] is None:
                    sample["unavailable_metrics"].append("surface_temperature")
                sample["collection_status"] = "ok" if not sample["unavailable_metrics"] else "partial"
        except subprocess.TimeoutExpired:
            sample["unavailable_metrics"] = ["ram", "surface_temperature"]
            sample["collection_error"] = "adb_timeout"
        except OSError:
            sample["unavailable_metrics"] = ["ram", "surface_temperature"]
            sample["collection_error"] = "adb_unavailable"
        completed_at = self.wall_clock()
        sample["sample_completed_at_utc"] = _iso(completed_at)
        sample["collection_duration_ms"] = round((time.perf_counter() - started) * 1000, 1)
        return sample

    def capture(self, phase: str) -> list[dict[str, Any]]:
        with ThreadPoolExecutor(max_workers=len(self.devices)) as executor:
            samples = list(executor.map(lambda device: self._sample_device(device, phase), self.devices))
        with self._samples_lock:
            self._samples.extend(samples)
        return samples

    def start_during_request(self) -> None:
        self._stop_event.clear()

        def sample_loop() -> None:
            while not self._stop_event.is_set():
                self.capture("during_request")
                self._stop_event.wait(self.interval_seconds)

        self._thread = threading.Thread(target=sample_loop, name="android-inference-telemetry", daemon=True)
        self._thread.start()

    def stop_during_request(self) -> None:
        self._stop_event.set()
        if self._thread is not None:
            self._thread.join(timeout=10)
            self._thread = None

    def report(self, request_started_at: datetime, request_completed_at: datetime) -> dict[str, Any]:
        with self._samples_lock:
            samples = list(self._samples)
        per_worker = []
        for device in self.devices:
            worker = device["rpc_worker"]
            during = [sample for sample in samples if sample["rpc_worker"] == worker and sample["phase"] == "during_request"]
            per_worker.append({
                "rpc_worker": worker,
                "hardware_model": device["hardware_model"],
                "android_version": device["android_version"],
                "during_request_sample_count": len(during),
                "available_ram_mib": _range_summary(during, "available_ram_mib"),
                "surface_temperature_celsius": _range_summary(during, "surface_temperature_celsius"),
            })
        return {
            "sampling_interval_ms": round(self.interval_seconds * 1000),
            "request_started_at_utc": _iso(request_started_at),
            "request_completed_at_utc": _iso(request_completed_at),
            "devices": per_worker,
            "samples": samples,
            "limitations": [
                "Android telemetry is sampled through ADB over USB; sampling adds small USB/ADB traffic during inference.",
                "RAM is MemAvailable and temperature is the Android SKIN thermal sensor when exposed; neither is process-only or model-only telemetry.",
                "A missing sensor or failed ADB sample is recorded as unavailable rather than estimated.",
            ],
        }


def _range_summary(samples: list[dict[str, Any]], field: str) -> dict[str, Any]:
    values = [sample[field] for sample in samples if isinstance(sample.get(field), (int, float))]
    if not values:
        return {"min": None, "max": None, "mean": None}
    return {
        "min": min(values),
        "max": max(values),
        "mean": round(sum(values) / len(values), 2),
    }
