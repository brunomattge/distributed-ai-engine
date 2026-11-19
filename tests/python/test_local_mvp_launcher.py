import os
import signal
import subprocess
import tempfile
import time
import unittest
from pathlib import Path


ROOT_DIR = Path(__file__).resolve().parents[2]
LAUNCHER = ROOT_DIR / "tools" / "run-local-mvp.sh"


class LocalMvpLauncherTests(unittest.TestCase):
    def test_starts_both_services_and_stops_them_on_sigterm(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary = Path(temporary_directory)
            environment = self._create_fake_environment(temporary, device_count=3)
            stdout_path = temporary / "launcher.stdout"
            with stdout_path.open("w", encoding="utf-8") as stdout:
                process = subprocess.Popen(
                    [str(LAUNCHER), "fast"],
                    cwd=ROOT_DIR,
                    env=environment,
                    stdout=stdout,
                    stderr=subprocess.STDOUT,
                    text=True,
                )
                try:
                    self._wait_until(
                        lambda: "Local MVP ready:" in stdout_path.read_text(encoding="utf-8"),
                        process,
                        stdout_path,
                    )
                finally:
                    if process.poll() is None:
                        process.send_signal(signal.SIGTERM)

                self.assertEqual(process.wait(timeout=10), 143)

            inference_stopped = temporary / "inference.stopped"
            dashboard_stopped = temporary / "dashboard.stopped"
            self.assertTrue(inference_stopped.is_file())
            self.assertTrue(dashboard_stopped.is_file())
            for stop_file in (inference_stopped, dashboard_stopped):
                service_pid, helper_pid = map(int, stop_file.read_text(encoding="utf-8").split())
                for pid in (service_pid, helper_pid):
                    with self.assertRaises(ProcessLookupError):
                        os.kill(pid, 0)

    def test_rejects_wrong_device_count_before_starting_any_service(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary = Path(temporary_directory)
            environment = self._create_fake_environment(temporary, device_count=0)

            result = subprocess.run(
                [str(LAUNCHER), "fast"],
                cwd=ROOT_DIR,
                env=environment,
                capture_output=True,
                text=True,
                timeout=5,
                check=False,
            )

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("found 0. No services were started", result.stderr)
            self.assertFalse((temporary / "inference.started").exists())
            self.assertFalse((temporary / "dashboard.started").exists())

    def test_stops_inference_launcher_if_dashboard_exits_unexpectedly(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary = Path(temporary_directory)
            environment = self._create_fake_environment(temporary, device_count=3, dashboard_exits=True)

            result = subprocess.run(
                [str(LAUNCHER), "fast"],
                cwd=ROOT_DIR,
                env=environment,
                capture_output=True,
                text=True,
                timeout=10,
                check=False,
            )

            self.assertNotEqual(result.returncode, 0)
            self.assertIn("dashboard launcher exited before distributed chat became ready", result.stderr)
            inference_stopped = temporary / "inference.stopped"
            self.assertTrue(inference_stopped.is_file())
            service_pid, helper_pid = map(int, inference_stopped.read_text(encoding="utf-8").split())
            for pid in (service_pid, helper_pid):
                with self.assertRaises(ProcessLookupError):
                    os.kill(pid, 0)

    def _create_fake_environment(
        self,
        temporary: Path,
        device_count: int,
        dashboard_exits: bool = False,
    ) -> dict[str, str]:
        bin_directory = temporary / "bin"
        bin_directory.mkdir()
        adb_path = bin_directory / "adb"
        device_lines = "".join(f"device-{index}\tdevice\n" for index in range(device_count))
        adb_path.write_text(
            "#!/bin/sh\nprintf 'List of devices attached\\n" + device_lines.replace("\n", "\\n") + "'\n",
            encoding="utf-8",
        )
        adb_path.chmod(0o755)

        (bin_directory / "lsof").write_text("#!/bin/sh\nexit 1\n", encoding="utf-8")
        (bin_directory / "lsof").chmod(0o755)

        curl_path = bin_directory / "curl"
        curl_path.write_text(
            "#!/bin/sh\n"
            "for argument in \"$@\"; do url=\"$argument\"; done\n"
            "case \"$url\" in\n"
            "  */api/health)\n"
            "    [ -f \"$INFERENCE_STARTED_FILE\" ] || exit 7\n"
            "    printf '{\"state\":\"ready\",\"supervisor_state\":\"ready\"}'\n;;\n"
            "  */api/inference-status)\n"
            "    [ -f \"$DASHBOARD_STARTED_FILE\" ] || exit 7\n"
            "    printf '{\"available\":true}'\n;;\n"
            "  *) exit 7;;\n"
            "esac\n",
            encoding="utf-8",
        )
        curl_path.chmod(0o755)

        inference_launcher = temporary / "fake-inference-launcher.sh"
        inference_launcher.write_text(self._fake_service_script("inference"), encoding="utf-8")
        inference_launcher.chmod(0o755)
        dashboard_launcher = temporary / "fake-dashboard-launcher.sh"
        dashboard_script = (
            "#!/usr/bin/env bash\n"
            "printf '%s\\n' \"$$\" > \"$DASHBOARD_STARTED_FILE\"\n"
            "exit 17\n"
            if dashboard_exits
            else self._fake_service_script("dashboard")
        )
        dashboard_launcher.write_text(dashboard_script, encoding="utf-8")
        dashboard_launcher.chmod(0o755)

        environment = os.environ.copy()
        environment.update({
            "PATH": str(bin_directory) + os.pathsep + environment.get("PATH", ""),
            "ANDROID_ADB": str(adb_path),
            "INFERENCE_LAUNCHER": str(inference_launcher),
            "DASHBOARD_LAUNCHER": str(dashboard_launcher),
            "INFERENCE_STARTED_FILE": str(temporary / "inference.started"),
            "DASHBOARD_STARTED_FILE": str(temporary / "dashboard.started"),
            "INFERENCE_STOPPED_FILE": str(temporary / "inference.stopped"),
            "DASHBOARD_STOPPED_FILE": str(temporary / "dashboard.stopped"),
            "MODEL_HEALTH_URL": "http://example.invalid/api/health",
            "INFERENCE_STATUS_URL": "http://example.invalid/api/inference-status",
            "MODEL_READY_TIMEOUT_SECONDS": "5",
            "CLUSTER_READY_TIMEOUT_SECONDS": "5",
        })
        return environment

    @staticmethod
    def _fake_service_script(service_name: str) -> str:
        return (
            "#!/usr/bin/env bash\n"
            "set -u\n"
            f"printf '%s\\n' \"$$\" > \"${service_name.upper()}_STARTED_FILE\"\n"
            "sleep 60 &\n"
            "child_pid=$!\n"
            "stop() {\n"
            "  kill \"$child_pid\" 2>/dev/null || true\n"
            "  wait \"$child_pid\" 2>/dev/null || true\n"
            f"  printf '%s %s\\n' \"$$\" \"$child_pid\" > \"${service_name.upper()}_STOPPED_FILE\"\n"
            "  exit 0\n"
            "}\n"
            "trap stop TERM INT\n"
            "wait \"$child_pid\"\n"
        )

    @staticmethod
    def _wait_until(predicate, process: subprocess.Popen, stdout_path: Path) -> None:
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            if predicate():
                return
            if process.poll() is not None:
                raise AssertionError(stdout_path.read_text(encoding="utf-8"))
            time.sleep(0.05)
        raise AssertionError(f"Timed out waiting for launcher readiness:\n{stdout_path.read_text(encoding='utf-8')}")


if __name__ == "__main__":
    unittest.main()
