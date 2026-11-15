import sys
import unittest
from unittest.mock import patch
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))

from adb_node_bridge import (  # noqa: E402
    AndroidDevice,
    RPC_PROCESS_CHECK_COMMAND,
    _read_rpc_worker_process_state,
    cpu_percent,
    newly_disconnected_devices,
    parse_adb_devices,
    parse_available_ram_mib,
    parse_cpu_counters,
    parse_registered_adb_nodes,
    parse_rpc_worker_process_state,
    parse_skin_temperature,
    stable_node_id,
)


class AdbNodeBridgeTests(unittest.TestCase):
    def test_device_discovery_selects_supported_authorized_models_only(self):
        devices = parse_adb_devices(
            """List of devices attached
serial-a device product:device model:SM_A075M transport_id:1
serial-b device product:device model:SM_A075M transport_id:2
serial-c device product:device model:SM_A065M transport_id:3
serial-other device product:device model:SM_G998B transport_id:4
serial-locked unauthorized usb:1-5
"""
        )

        self.assertEqual(3, len(devices))
        self.assertCountEqual(["SM-A075M", "SM-A075M", "SM-A065M"], [device.model for device in devices])
        self.assertTrue(all(device.node_id.startswith("adb-") for device in devices))
        self.assertTrue(all("serial-" not in device.node_id for device in devices))

    def test_node_id_is_stable_for_same_serial(self):
        self.assertEqual(stable_node_id("sample-device"), stable_node_id("sample-device"))
        self.assertNotEqual(stable_node_id("sample-device"), stable_node_id("other-device"))

    def test_discovery_reports_devices_missing_from_a_successful_scan(self):
        previous = parse_adb_devices(
            """List of devices attached
serial-a device model:SM_A075M
serial-b device model:SM_A065M
"""
        )
        current = parse_adb_devices("""List of devices attached
serial-a device model:SM_A075M
""")

        disconnected = newly_disconnected_devices({device.node_id: device for device in previous}, current)

        self.assertEqual(["SM-A065M"], [device.model for device in disconnected])

    def test_startup_reconciliation_seeds_only_previously_registered_adb_nodes(self):
        nodes = parse_registered_adb_nodes(
            [
                {
                    "nodeId": "adb-existing123456",
                    "name": "Galaxy A07 · ADB 123456",
                    "hardwareModel": "Samsung Galaxy A07 (SM-A075M)",
                },
                {"nodeId": "sim-a07-1", "name": "Simulated node", "hardwareModel": "sim"},
            ]
        )

        self.assertEqual(["adb-existing123456"], list(nodes))
        self.assertEqual("Galaxy A07 · ADB 123456", nodes["adb-existing123456"].display_name)
        self.assertEqual("", nodes["adb-existing123456"].serial)

    def test_cpu_percent_uses_busy_delta_and_ignores_guest_counters(self):
        before = parse_cpu_counters("cpu  100 0 50 800 50 0 0 0 200 10\n")
        after = parse_cpu_counters("cpu  150 0 75 975 50 0 0 0 210 11\n")

        self.assertEqual(30.0, cpu_percent(before, after))

    def test_memory_available_is_converted_from_kibibytes_to_mibibytes(self):
        self.assertEqual(1346, parse_available_ram_mib("MemTotal: 4096000 kB\nMemAvailable: 1378056 kB\n"))
        self.assertIsNone(parse_available_ram_mib("MemTotal: 4096000 kB\n"))

    def test_skin_temperature_ignores_cpu_and_unavailable_sensor_values(self):
        output = """Temperature{mValue=63.401, mType=0, mName=CPU, mStatus=0}
Temperature{mValue=45.937, mType=3, mName=SKIN, mStatus=2}
Temperature{mValue=39.1, mType=2, mName=BATTERY, mStatus=0}
"""
        self.assertEqual(45.9, parse_skin_temperature(output))
        self.assertIsNone(parse_skin_temperature("Temperature{mValue=-127.0, mType=3, mName=SKIN, mStatus=0}"))

    def test_rpc_worker_process_state_accepts_only_known_observations(self):
        self.assertEqual("Running", parse_rpc_worker_process_state("Running\r\n"))
        self.assertEqual("Stopped", parse_rpc_worker_process_state("Stopped\n"))
        self.assertEqual("Unknown", parse_rpc_worker_process_state(""))
        self.assertEqual("Unknown", parse_rpc_worker_process_state("unexpected output"))

    def test_rpc_process_probe_is_sent_as_one_android_shell_expression(self):
        device = AndroidDevice("serial-a", "SM-A075M", "Galaxy A07", "adb-a", "A07", "SM-A075M")
        with patch("adb_node_bridge._run_adb", return_value=("Running\n", 12.0)) as run_adb:
            state, elapsed = _read_rpc_worker_process_state("adb", device)

        self.assertEqual("Running", state)
        self.assertEqual(12.0, elapsed)
        run_adb.assert_called_once_with("adb", "serial-a", ["shell", RPC_PROCESS_CHECK_COMMAND])

if __name__ == "__main__":
    unittest.main()
