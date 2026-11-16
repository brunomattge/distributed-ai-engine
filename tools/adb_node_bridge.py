#!/usr/bin/env python3
"""Forward read-only Android telemetry from authorized ADB devices to the local platform API."""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


KNOWN_MODEL_NAMES = {
    "SM-A075M": "Galaxy A07",
    "SM-A065M": "Galaxy A06",
}
POLL_SECONDS = 2.0
CPU_SAMPLE_SECONDS = 0.35
ADB_TIMEOUT_SECONDS = 2
RPC_PROCESS_CHECK_COMMAND = (
    "if pidof ggml-rpc-server >/dev/null 2>&1; then echo Running; else echo Stopped; fi"
)


@dataclass(frozen=True)
class AndroidDevice:
    serial: str = field(repr=False)
    model: str
    display_name: str
    node_id: str
    name: str
    hardware_model: str


def stable_node_id(serial: str) -> str:
    """Create a repeatable local ID without displaying or storing the ADB serial."""
    digest = hashlib.sha256(serial.encode("utf-8")).hexdigest()[:16]
    return f"adb-{digest}"


def parse_adb_devices(output: str) -> list[AndroidDevice]:
    android_devices: list[tuple[str, str, str]] = []
    for line in output.splitlines()[1:]:
        parts = line.split()
        if len(parts) < 3 or parts[1] != "device":
            continue

        model_token = next((part for part in parts[2:] if part.startswith("model:")), "")
        product_token = next((part for part in parts[2:] if part.startswith("product:")), "")
        model = (model_token or product_token).split(":", maxsplit=1)[-1].replace("_", "-")
        model = model or "unknown"

        serial = parts[0]
        android_devices.append((stable_node_id(serial), serial, model))

    android_devices.sort(key=lambda item: item[0])
    devices: list[AndroidDevice] = []
    for node_id, serial, model in android_devices:
        display_name = KNOWN_MODEL_NAMES.get(model)
        if display_name is None:
            display_name = "Android device" if model == "unknown" else model.replace("-", " ")
        hardware_model = (
            f"Samsung {display_name} ({model})"
            if model in KNOWN_MODEL_NAMES
            else f"Android device ({display_name})"
        )
        devices.append(
            AndroidDevice(
                serial=serial,
                model=model,
                display_name=display_name,
                node_id=node_id,
                name=f"{display_name} · ADB {node_id[-6:].upper()}",
                hardware_model=hardware_model,
            )
        )
    return devices


def parse_registered_adb_nodes(nodes: list[dict[str, Any]]) -> dict[str, AndroidDevice]:
    """Seed bridge state from API nodes so stale ADB nodes can be reconciled on startup."""
    devices: dict[str, AndroidDevice] = {}
    for node in nodes:
        node_id = node.get("nodeId")
        if not isinstance(node_id, str) or not node_id.startswith("adb-"):
            continue
        name = node.get("name") if isinstance(node.get("name"), str) else node_id
        hardware_model = (
            node.get("hardwareModel")
            if isinstance(node.get("hardwareModel"), str)
            else "Unknown Android device"
        )
        devices[node_id] = AndroidDevice(
            serial="",
            model=hardware_model,
            display_name=name,
            node_id=node_id,
            name=name,
            hardware_model=hardware_model,
        )
    return devices


def parse_cpu_counters(output: str) -> tuple[int, int]:
    line = next((line for line in output.splitlines() if line.startswith("cpu ")), "")
    fields = line.split()[1:9]
    if len(fields) < 5:
        raise ValueError("Android /proc/stat did not contain aggregate CPU counters")

    counters = [int(value) for value in fields]
    total = sum(counters)
    idle = counters[3] + counters[4]
    return total, idle


def cpu_percent(before: tuple[int, int], after: tuple[int, int]) -> float | None:
    total_delta = after[0] - before[0]
    idle_delta = after[1] - before[1]
    if total_delta <= 0 or idle_delta < 0:
        return None
    busy_delta = max(0, total_delta - idle_delta)
    return round(100.0 * busy_delta / total_delta, 1)


def parse_available_ram_mib(meminfo: str) -> int | None:
    match = re.search(r"^MemAvailable:\s+(\d+)\s+kB", meminfo, re.MULTILINE)
    return round(int(match.group(1)) / 1024) if match else None


def parse_skin_temperature(thermal_output: str) -> float | None:
    pattern = r"Temperature\{mValue=(-?\d+(?:\.\d+)?),[^}]*mName=SKIN(?:,|})"
    for match in re.finditer(pattern, thermal_output):
        value = float(match.group(1))
        if 0 <= value <= 120:
            return round(value, 1)
    return None


def parse_rpc_worker_process_state(output: str) -> str:
    state = output.strip()
    return state if state in {"Running", "Stopped"} else "Unknown"


def _read_rpc_worker_process_state(adb: str, device: AndroidDevice) -> tuple[str, float]:
    output, elapsed_ms = _run_adb(
        adb,
        device.serial,
        ["shell", RPC_PROCESS_CHECK_COMMAND],
    )
    return parse_rpc_worker_process_state(output), elapsed_ms


def _run_adb(adb: str, serial: str | None, args: list[str]) -> tuple[str, float]:
    command = [adb]
    if serial is not None:
        command.extend(["-s", serial])
    command.extend(args)
    started = time.perf_counter()
    result = subprocess.run(command, capture_output=True, text=True, timeout=ADB_TIMEOUT_SECONDS, check=False)
    elapsed_ms = (time.perf_counter() - started) * 1000
    if result.returncode != 0:
        raise RuntimeError("ADB command failed")
    return result.stdout.replace("\r", ""), elapsed_ms


def _post_json(url: str, payload: dict[str, Any]) -> int:
    request = Request(
        url,
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urlopen(request, timeout=5) as response:
            return response.status
    except HTTPError as error:
        return error.code
    except URLError as error:
        raise RuntimeError("Platform API request failed") from error


def _register(api_url: str, device: AndroidDevice) -> bool:
    status = _post_json(
        f"{api_url}/api/nodes",
        {
            "nodeId": device.node_id,
            "name": device.name,
            "hardwareModel": device.hardware_model,
        },
    )
    return status in (201, 409)


def _read_cpu(adb: str, device: AndroidDevice) -> tuple[int, int]:
    output, _ = _run_adb(adb, device.serial, ["shell", "cat", "/proc/stat"])
    return parse_cpu_counters(output)


def _read_metrics(adb: str, device: AndroidDevice, cpu: float) -> dict[str, Any]:
    meminfo, meminfo_ms = _run_adb(adb, device.serial, ["shell", "cat", "/proc/meminfo"])
    thermal, thermal_ms = _run_adb(adb, device.serial, ["shell", "dumpsys", "thermalservice"])
    process_state, process_ms = _read_rpc_worker_process_state(adb, device)
    ram_mib = parse_available_ram_mib(meminfo)
    if ram_mib is None:
        raise RuntimeError("Android did not report MemAvailable")

    return {
        "cpuPercent": cpu,
        "availableRamMiB": ram_mib,
        "temperatureCelsius": parse_skin_temperature(thermal),
        "rpcWorkerProcessState": process_state,
        # Sum of ADB collection command durations, not RPC inference latency.
        "latencyMs": round(meminfo_ms + thermal_ms + process_ms, 1),
    }


def _discover(adb: str) -> list[AndroidDevice]:
    output, _ = _run_adb(adb, None, ["devices", "-l"])
    return parse_adb_devices(output)


def newly_disconnected_devices(
    previously_present: dict[str, AndroidDevice],
    currently_present: list[AndroidDevice],
) -> list[AndroidDevice]:
    current_ids = {device.node_id for device in currently_present}
    return [device for node_id, device in previously_present.items() if node_id not in current_ids]


def _heartbeat(api_url: str, device: AndroidDevice, metrics: dict[str, Any]) -> bool:
    status = _post_json(f"{api_url}/api/nodes/{device.node_id}/heartbeat", metrics)
    if status == 404:
        if not _register(api_url, device):
            return False
        status = _post_json(f"{api_url}/api/nodes/{device.node_id}/heartbeat", metrics)
    return status == 200


def _collect_and_send(
    adb: str,
    api_url: str,
    device: AndroidDevice,
    registered: set[str],
) -> str:
    if device.node_id not in registered:
        if not _register(api_url, device):
            return "registration-failed"
        registered.add(device.node_id)

    before = _read_cpu(adb, device)
    time.sleep(CPU_SAMPLE_SECONDS)
    after = _read_cpu(adb, device)
    current_cpu = cpu_percent(before, after)
    if current_cpu is None:
        return "cpu-sample-unavailable"

    metrics = _read_metrics(adb, device, current_cpu)
    if not _heartbeat(api_url, device, metrics):
        return "heartbeat-rejected"
    return "sent"


def _summary(devices: list[AndroidDevice]) -> str:
    counts: dict[str, int] = {}
    for device in devices:
        counts[device.display_name] = counts.get(device.display_name, 0) + 1
    return ", ".join(f"{count} {model}" for model, count in counts.items()) or "no supported devices"


def run(api_url: str) -> int:
    adb = os.environ.get("ANDROID_ADB") or shutil.which("adb")
    if not adb:
        print("ADB not found. Install Android platform-tools or set ANDROID_ADB.", file=sys.stderr)
        return 2

    api_url = api_url.rstrip("/")
    try:
        with urlopen(f"{api_url}/healthz", timeout=5) as response:
            if response.status != 200:
                raise RuntimeError("Platform API is not healthy")
    except (HTTPError, URLError, TimeoutError, OSError):
        print("Platform API is unavailable; start it before the ADB bridge.", file=sys.stderr)
        return 2

    registered: set[str] = set()
    previously_present: dict[str, AndroidDevice] = {}
    try:
        with urlopen(f"{api_url}/api/nodes", timeout=5) as response:
            previously_present = parse_registered_adb_nodes(json.load(response))
        if previously_present:
            print(f"Reconciled {len(previously_present)} registered ADB node(s) from the API.")
    except (HTTPError, URLError, TimeoutError, OSError, ValueError):
        print("Could not seed prior ADB nodes from the API; heartbeat timeout remains the fallback.")

    previous_discovery: tuple[str, ...] | None = None
    print("ADB telemetry bridge ready. Reading supported Samsung nodes; serials are kept in memory only.")
    while True:
        cycle_started = time.monotonic()
        discovery_ok = True
        try:
            devices = _discover(adb)
        except (OSError, RuntimeError, subprocess.TimeoutExpired):
            devices = []
            discovery_ok = False
            print("ADB discovery failed; waiting for the next poll.")

        if discovery_ok:
            next_present = {device.node_id: device for device in devices}
            for device in newly_disconnected_devices(previously_present, devices):
                try:
                    status = _post_json(f"{api_url}/api/nodes/{device.node_id}/disconnect", {})
                except (OSError, RuntimeError):
                    print(f"Disconnect notification failed for {device.display_name}; will retry.")
                    next_present[device.node_id] = device
                    continue

                if status == 200:
                    print(f"ADB disconnected {device.display_name}; API marked it Offline.")
                elif status != 404:
                    print(f"Disconnect notification failed for {device.display_name}.")
                    next_present[device.node_id] = device
            previously_present = next_present

            discovery = tuple(device.node_id for device in devices)
            if discovery != previous_discovery:
                print(f"Supported Android devices: {len(devices)} ({_summary(devices)}).")
                previous_discovery = discovery

        accepted = 0
        with ThreadPoolExecutor(max_workers=max(1, len(devices))) as pool:
            pending = {
                pool.submit(_collect_and_send, adb, api_url, device, registered): device
                for device in devices
            }
            for future in as_completed(pending):
                device = pending[future]
                try:
                    outcome = future.result()
                except (OSError, RuntimeError, ValueError, subprocess.TimeoutExpired):
                    print(f"Telemetry read failed for {device.display_name}; no heartbeat was sent.")
                    registered.discard(device.node_id)
                    continue

                if outcome == "sent":
                    accepted += 1
                elif outcome == "registration-failed":
                    print(f"Registration failed for {device.display_name}; will retry.")
                elif outcome == "cpu-sample-unavailable":
                    print(f"CPU sample unavailable for {device.display_name}; heartbeat skipped.")
                else:
                    print(f"Heartbeat failed for {device.display_name}; it will age Offline if this continues.")

        if accepted:
            print(f"Android telemetry cycle complete: {accepted}/{len(devices)} heartbeats sent.")
        elif devices:
            print("Android telemetry cycle complete: no heartbeat was sent.")

        remaining = POLL_SECONDS - (time.monotonic() - cycle_started)
        time.sleep(max(0.0, remaining))


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: python3 tools/adb_node_bridge.py http://127.0.0.1:5100", file=sys.stderr)
        return 2
    return run(sys.argv[1])


if __name__ == "__main__":
    raise SystemExit(main())
