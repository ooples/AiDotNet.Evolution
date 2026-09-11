import json
import os
import signal
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from aidotnet_evolution import DurableWorkClient, DurableWorkError, parse_evaluation_payload

ROOT = Path(__file__).resolve().parents[3]
DLL = os.environ.get("AIDOTNET_DURABLE_HOST_DLL")
HOST = os.environ.get("AIDOTNET_DURABLE_HOST_PATH")
if not DLL and not HOST:
    # Local source-tree default. CI supplies the binary built for its target explicitly.
    DLL = str(ROOT / "src/AiDotNet.Evolution.Host/bin/Release/net10.0/aidotnet-evolution-host.dll")
TRANSPORT = {"host_path": HOST} if HOST else {"host_path": "dotnet", "host_args": [DLL]}
JOB = {"evaluationId": "9223372036854775807", "attempt": 1, "canonicalGenomeId": "integer:7", "payload": "7",
    "estimated": {"cost": "1"}, "maximum": {"cost": "5"}}


def config(directory):
    return {"directory": directory, "runId": "run", "compatibilityHash": "compat", "limits": {"cost": "100"}}


def worker(name):
    return {"workerId": name, "compatibilityHash": "compat"}


def receipt(lease):
    return {"identity": lease["identity"], "workerId": lease["workerId"], "payload": "49", "provenance": "local-square-v1",
        "actual": {"cost": "0.1234567890123456789012345678"}, "outcome": "completed"}


class DurableTests(unittest.TestCase):
    def test_live_host_preserves_exact_identity_receipt_and_reopen(self):
        with tempfile.TemporaryDirectory(prefix="evolution-py-work-") as directory:
            with DurableWorkClient(config(directory), **TRANSPORT) as client:
                self.assertTrue(client.enqueue(JOB))
                self.assertFalse(client.enqueue(JOB))
                lease = client.claim(worker("one"))
                self.assertEqual(JOB["evaluationId"], lease["identity"]["evaluationId"])
                self.assertIsNone(client.claim(worker("two")))
                self.assertEqual("renewed", client.heartbeat(lease["identity"], "one"))
            with DurableWorkClient(config(directory), **TRANSPORT) as client:
                self.assertTrue(client.status()["wasRecovered"])
                page = client.unsettled("one")
                self.assertEqual(lease["identity"], page["lease"]["identity"])
                self.assertIsNone(client.unsettled("one", page["nextAfterLeaseId"])["lease"])
                self.assertEqual("accepted", client.commit(receipt(lease)))
                self.assertEqual("duplicate", client.commit(receipt(lease)))
                self.assertEqual(receipt(lease)["actual"], client.delivery(lease["identity"], "one")["actual"])
            with DurableWorkClient(config(directory), **TRANSPORT) as client:
                self.assertEqual("49", client.result(JOB["evaluationId"], 1)["payload"])
                self.assertEqual("1", client.status()["settled"])
                self.assertEqual("0", client.status()["reserved"]["cost"])

    def test_real_process_kill_does_not_forget_dispatch_reservation(self):
        with tempfile.TemporaryDirectory(prefix="evolution-py-kill-") as directory:
            client = DurableWorkClient(config(directory), **TRANSPORT)
            try:
                client.enqueue(JOB)
                lease = client.claim(worker("one"))
                os.kill(client.pid, signal.SIGTERM)
                with self.assertRaises(DurableWorkError):
                    client.status()
            finally:
                try:
                    client.close()
                except DurableWorkError:
                    pass
            with DurableWorkClient(config(directory), **TRANSPORT) as restored:
                self.assertEqual("5", restored.status()["reserved"]["cost"])
                self.assertEqual(lease["identity"], restored.unsettled("one")["lease"]["identity"])
                self.assertIsNone(restored.claim(worker("one")))
                self.assertEqual("accepted", restored.commit(receipt(lease)))

    def test_matching_cancel_and_stale_commit_keep_cost(self):
        with tempfile.TemporaryDirectory(prefix="evolution-py-cancel-") as directory:
            with DurableWorkClient(config(directory), **TRANSPORT) as client:
                client.enqueue({**JOB, "tags": ["gpu"], "minimumResources": {"gpu_slots": "1"}})
                self.assertIsNone(client.claim(worker("cpu")))
                lease = client.claim({**worker("gpu"), "tags": ["gpu"], "capacity": {"gpu_slots": "1"}})
                self.assertTrue(client.cancel(JOB["evaluationId"], 1))
                self.assertEqual("canceled", client.heartbeat(lease["identity"], "gpu"))
                self.assertEqual("5", client.status()["reserved"]["cost"])
                self.assertEqual("stale", client.commit(receipt(lease)))
                self.assertEqual("duplicate-stale", client.commit(receipt(lease)))
                self.assertIsNone(client.result(JOB["evaluationId"], 1))

    def test_envelope_preserves_all_bits_and_rejects_numeric_seed(self):
        payload = {"schema": 1, "genomePayload": "7", "canonicalGenomeId": "integer:7", "evaluationId": JOB["evaluationId"],
            "attempt": 2, "rootSeed": "18446744073709551615", "seedStream": "18446744073709551614"}
        self.assertEqual(payload, parse_evaluation_payload(json.dumps(payload)))
        for change in [{"rootSeed": 18446744073709551615}, {"rootSeed": "01"}, {"schema": True}, {"attempt": True},
                {"rootSeed": "18446744073709551616"}, {"evaluationId": 9223372036854775807}]:
            with self.subTest(change=change), self.assertRaises(DurableWorkError):
                parse_evaluation_payload(json.dumps({**payload, **change}))

    def test_bad_configuration_is_rejected_before_spawn(self):
        with self.assertRaises(DurableWorkError):
            DurableWorkClient({**config("unused"), "limits": {"cost": 1.25}}, host_path="must-not-run")
        for timeout in [0, -1, float("nan"), True]:
            with self.subTest(timeout=timeout), self.assertRaises(DurableWorkError):
                DurableWorkClient(config("unused"), host_path="must-not-run", request_timeout=timeout)

    def test_fake_protocol_downgrade_and_transport_failures(self):
        fake = str(Path(__file__).with_name("fake_host.py"))
        with self.assertRaisesRegex(DurableWorkError, "downgrade"):
            DurableWorkClient(config("unused"), host_path=sys.executable, host_args=[fake, "downgrade"], request_timeout=2)
        for mode, message in [("invalid-claim", "Invalid claim"), ("uncorrelated", "correlation"), ("oversized", "Oversized"), ("timeout", "timed out")]:
            with self.subTest(mode=mode):
                client = DurableWorkClient(config("unused"), host_path=sys.executable, host_args=[fake, mode], request_timeout=2)
                started = time.monotonic()
                try:
                    with self.assertRaisesRegex(DurableWorkError, message):
                        client.claim(worker("one"))
                    self.assertLess(time.monotonic() - started, 8)
                finally:
                    with self.assertRaises(DurableWorkError):
                        client.close()


if __name__ == "__main__":
    unittest.main()
