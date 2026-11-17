import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock


ROOT_DIR = Path(__file__).resolve().parents[2]
PHASE3_PYTHON = ROOT_DIR / "experiments" / "phase-3" / "python"
sys.path.insert(0, str(PHASE3_PYTHON))

from android_telemetry import AndroidTelemetrySampler, load_rpc_devices  # noqa: E402


class AndroidTelemetryTests(unittest.TestCase):
    def test_load_rpc_devices_maps_workers_without_including_serial_in_public_fields(self):
        with tempfile.TemporaryDirectory() as directory:
            devices_file = Path(directory) / "devices.tsv"
            devices_file.write_text(
                "private-serial-1|SM_A075M|16|50052\n"
                "private-serial-2|SM_A075M|16|50053\n"
                "private-serial-3|SM_A065M|16|50054\n",
                encoding="utf-8",
            )

            devices = load_rpc_devices(devices_file, 2)

        self.assertEqual([device["rpc_worker"] for device in devices], ["RPC0", "RPC1"])
        self.assertEqual([device["hardware_model"] for device in devices], ["Galaxy A07", "Galaxy A07"])
        self.assertNotIn("private-serial-1", repr([{key: value for key, value in device.items() if key != "serial"} for device in devices]))

    def test_capture_reports_ram_temperature_sensor_and_collection_window(self):
        wall_times = iter([
            datetime(2026, 11, 16, 10, 0, 0, tzinfo=timezone.utc),
            datetime(2026, 11, 16, 10, 0, 0, 500000, tzinfo=timezone.utc),
        ])
        runner = Mock(return_value=SimpleNamespace(
            returncode=0,
            stdout=(
                "MemAvailable:     1474560 kB\n"
                "Temperature{mValue=40.1, mType=3, mName=SKIN}\n"
            ),
        ))
        sampler = AndroidTelemetrySampler(
            "adb",
            [{"serial": "private-serial", "rpc_worker": "RPC0", "hardware_model": "Galaxy A07", "android_version": "16"}],
            runner=runner,
            wall_clock=lambda: next(wall_times),
        )

        result = sampler.capture("during_request")

        self.assertEqual(result[0]["available_ram_mib"], 1440)
        self.assertEqual(result[0]["surface_temperature_celsius"], 40.1)
        self.assertEqual(result[0]["temperature_sensor"], "SKIN")
        self.assertEqual(result[0]["collection_status"], "ok")
        self.assertEqual(result[0]["sample_started_at_utc"], "2026-11-16T10:00:00+00:00")
        self.assertEqual(result[0]["sample_completed_at_utc"], "2026-11-16T10:00:00.500000+00:00")
        self.assertNotIn("private-serial", repr(result))
        self.assertEqual(runner.call_args.args[0], ["adb", "-s", "private-serial", "shell", "cat /proc/meminfo; dumpsys thermalservice"])

    def test_missing_skin_sensor_is_reported_as_partial_not_estimated(self):
        runner = Mock(return_value=SimpleNamespace(returncode=0, stdout="MemAvailable: 1024000 kB\n"))
        sampler = AndroidTelemetrySampler(
            "adb",
            [{"serial": "serial", "rpc_worker": "RPC0", "hardware_model": "Android device", "android_version": "16"}],
            runner=runner,
        )

        sample = sampler.capture("after_request")[0]

        self.assertEqual(sample["available_ram_mib"], 1000)
        self.assertIsNone(sample["surface_temperature_celsius"])
        self.assertEqual(sample["collection_status"], "partial")
        self.assertEqual(sample["unavailable_metrics"], ["surface_temperature"])

    def test_adb_failure_is_recorded_without_failing_inference_sampler(self):
        runner = Mock(return_value=SimpleNamespace(returncode=1, stdout="", stderr="private device detail"))
        sampler = AndroidTelemetrySampler(
            "adb",
            [{"serial": "serial", "rpc_worker": "RPC0", "hardware_model": "Galaxy A06", "android_version": "16"}],
            runner=runner,
        )

        sample = sampler.capture("before_request")[0]

        self.assertEqual(sample["collection_status"], "unavailable")
        self.assertEqual(sample["collection_error"], "adb_exit_1")
        self.assertNotIn("private device detail", repr(sample))

    def test_report_summarizes_during_request_values_by_worker(self):
        runner = Mock(return_value=SimpleNamespace(
            returncode=0,
            stdout="MemAvailable: 1024000 kB\nTemperature{mValue=39.0, mType=3, mName=SKIN}\n",
        ))
        sampler = AndroidTelemetrySampler(
            "adb",
            [{"serial": "serial", "rpc_worker": "RPC0", "hardware_model": "Galaxy A06", "android_version": "16"}],
            runner=runner,
        )
        sampler.capture("before_request")
        sampler.capture("during_request")
        sampler.capture("during_request")
        sampler.capture("after_request")
        start = datetime(2026, 11, 16, 10, 0, 0, tzinfo=timezone.utc)

        report = sampler.report(start, datetime(2026, 11, 16, 10, 0, 1, tzinfo=timezone.utc))

        self.assertEqual(report["devices"][0]["rpc_worker"], "RPC0")
        self.assertEqual(report["devices"][0]["during_request_sample_count"], 2)
        self.assertEqual(report["devices"][0]["available_ram_mib"], {"min": 1000, "max": 1000, "mean": 1000.0})
        self.assertEqual(report["devices"][0]["surface_temperature_celsius"], {"min": 39.0, "max": 39.0, "mean": 39.0})
        self.assertEqual([sample["phase"] for sample in report["samples"]], ["before_request", "during_request", "during_request", "after_request"])


if __name__ == "__main__":
    unittest.main()
