import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import Mock


ROOT_DIR = Path(__file__).resolve().parents[2]
PHASE3_PYTHON = ROOT_DIR / "experiments" / "phase-3" / "python"
sys.path.insert(0, str(PHASE3_PYTHON))

import benchmark_client  # noqa: E402


class BenchmarkClientTests(unittest.TestCase):
    def test_run_sample_records_time_to_first_content_and_completion(self):
        response = Mock()
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        response.__iter__ = Mock(return_value=iter([
            b'data: {"choices":[{"delta":{"content":"O"}}]}\n',
            b'data: {"choices":[{"delta":{"content":"K"}}]}\n',
            b"data: [DONE]\n",
        ]))
        times = iter([10.0, 10.25, 11.0])
        wall_times = iter([
            datetime(2026, 11, 16, 10, 0, 0, tzinfo=timezone.utc),
            datetime(2026, 11, 16, 10, 0, 1, tzinfo=timezone.utc),
        ])
        opener = Mock(return_value=response)

        result = benchmark_client.run_sample(
            "http://127.0.0.1:8080/v1",
            "qwen-test",
            "Reply OK",
            2,
            0.2,
            opener=opener,
            clock=lambda: next(times),
            wall_clock=lambda: next(wall_times),
        )

        self.assertEqual(result, {
            "ok": True,
            "first_content_ms": 250.0,
            "elapsed_seconds": 1.0,
            "content": "OK",
            "stream_chunks": 2,
            "request_started_at_utc": "2026-11-16T10:00:00+00:00",
            "request_completed_at_utc": "2026-11-16T10:00:01+00:00",
        })
        request = opener.call_args.args[0]
        self.assertEqual(request.full_url, "http://127.0.0.1:8080/v1/chat/completions")
        self.assertEqual(request.data, b'{"model": "qwen-test", "messages": [{"role": "user", "content": "Reply OK"}], "max_tokens": 2, "temperature": 0.2, "stream": true, "cache_prompt": false}')

    def test_run_sample_correlates_device_telemetry_to_request_window(self):
        response = Mock()
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        response.__iter__ = Mock(return_value=iter([
            b'data: {"choices":[{"delta":{"content":"OK"}}]}\n',
            b"data: [DONE]\n",
        ]))
        sampler = Mock()
        sampler.report.return_value = {"devices": [], "samples": []}
        wall_times = iter([
            datetime(2026, 11, 16, 10, 0, 0, tzinfo=timezone.utc),
            datetime(2026, 11, 16, 10, 0, 1, tzinfo=timezone.utc),
        ])

        result = benchmark_client.run_sample(
            "http://127.0.0.1:8080/v1",
            "qwen-test",
            "Reply OK",
            2,
            0.2,
            opener=Mock(return_value=response),
            telemetry_sampler=sampler,
            wall_clock=lambda: next(wall_times),
        )

        self.assertEqual(result["device_telemetry"], {"devices": [], "samples": []})
        sampler.capture.assert_any_call("before_request")
        sampler.start_during_request.assert_called_once_with()
        sampler.stop_during_request.assert_called_once_with()
        sampler.capture.assert_any_call("after_request")
        sampler.report.assert_called_once_with(
            datetime(2026, 11, 16, 10, 0, 0, tzinfo=timezone.utc),
            datetime(2026, 11, 16, 10, 0, 1, tzinfo=timezone.utc),
        )

    def test_run_sample_rejects_stream_without_done(self):
        response = Mock()
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        response.__iter__ = Mock(return_value=iter([b'data: {"choices":[{"delta":{"content":"partial"}}]}\n']))

        with self.assertRaisesRegex(RuntimeError, r"without the \[DONE\] marker"):
            benchmark_client.run_sample(
                "http://127.0.0.1:8080/v1",
                "qwen-test",
                "Reply OK",
                2,
                0.2,
                opener=Mock(return_value=response),
            )

    def test_run_sample_rejects_empty_completion(self):
        response = Mock()
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        response.__iter__ = Mock(return_value=iter([b"data: [DONE]\n"]))

        with self.assertRaisesRegex(RuntimeError, "without returning text"):
            benchmark_client.run_sample(
                "http://127.0.0.1:8080/v1",
                "qwen-test",
                "Reply OK",
                2,
                0.2,
                opener=Mock(return_value=response),
            )

    def test_summary_uses_successes_only_and_reports_failures(self):
        summary = benchmark_client.summarize_samples([
            {"ok": True, "first_content_ms": 100.0, "elapsed_seconds": 1.0},
            {"ok": True, "first_content_ms": 300.0, "elapsed_seconds": 3.0},
            {"ok": False, "error": "worker failed"},
        ])

        self.assertEqual(summary, {
            "successful_requests": 2,
            "failed_requests": 1,
            "median_first_content_ms": 200.0,
            "median_total_seconds": 2.0,
            "min_total_seconds": 1.0,
            "max_total_seconds": 3.0,
        })

    def test_english_summary_includes_per_request_device_ranges_without_serials(self):
        report = {
            "created_at_utc": "2026-11-17T00:33:31+00:00",
            "model": {"id": "qwen-test", "file_name": "qwen-test.gguf"},
            "selected_rpc_workers": 1,
            "transport": "USB",
            "summary": {
                "successful_requests": 1,
                "failed_requests": 0,
                "median_first_content_ms": 1000.0,
                "median_total_seconds": 1.2,
            },
            "samples": [{
                "sample": 1,
                "ok": True,
                "first_content_ms": 1000.0,
                "elapsed_seconds": 1.2,
                "device_telemetry": {"devices": [{
                    "rpc_worker": "RPC0",
                    "hardware_model": "Galaxy A07",
                    "android_version": "16",
                    "during_request_sample_count": 3,
                    "available_ram_mib": {"min": 1000, "max": 1010, "mean": 1005.0},
                    "surface_temperature_celsius": {"min": 39.0, "max": 40.0, "mean": 39.5},
                }]},
            }],
        }
        with tempfile.TemporaryDirectory() as directory:
            report_path = Path(directory) / "benchmark.json"
            summary_path = benchmark_client.write_english_summary(report, report_path)
            content = summary_path.read_text(encoding="utf-8")

        self.assertIn("Distributed inference benchmark summary", content)
        self.assertIn("Request 1: first content 1000.0 ms; total 1.200 s", content)
        self.assertIn("RAM 1000.0–1010.0 MiB (mean 1005.0)", content)
        self.assertIn("surface temperature 39.0–40.0 °C (mean 39.5)", content)
        self.assertNotIn("serial", content.lower())


if __name__ == "__main__":
    unittest.main()
