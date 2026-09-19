"""Contract tests against a required, real NativeAOT shared library (no skip fallback)."""
import concurrent.futures
import ctypes
import json
from pathlib import Path
import sys
import tempfile
import unittest

LIBRARY = Path(sys.argv.pop(1)).resolve()
native = ctypes.CDLL(str(LIBRARY))
native.aiev_work_abi_version.argtypes = []
native.aiev_work_abi_version.restype = ctypes.c_int32
native.aiev_work_create.argtypes = []
native.aiev_work_create.restype = ctypes.c_uint64
native.aiev_work_submit.argtypes = [ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int32]
native.aiev_work_submit.restype = ctypes.c_int32
native.aiev_work_read.argtypes = [ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int32]
native.aiev_work_read.restype = ctypes.c_int32
native.aiev_work_close.argtypes = [ctypes.c_uint64]
native.aiev_work_close.restype = ctypes.c_int32
MAXIMUM = 16 * 1024 * 1024
JOB = {"evaluationId": "9223372036854775807", "attempt": 1, "canonicalGenomeId": "integer:7", "payload": "7",
       "estimated": {"cost": "1"}, "maximum": {"cost": "5"}}


def submit(handle, value):
    data = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return native.aiev_work_submit(handle, data, len(data))


def read(handle):
    size = native.aiev_work_read(handle, None, 0)
    if not 0 < size <= MAXIMUM:
        raise AssertionError(f"Invalid response size/status: {size}")
    output = ctypes.create_string_buffer(size)
    if native.aiev_work_read(handle, output, size) != size:
        raise AssertionError("Response size changed")
    return json.loads(output.raw)


class NativeTests(unittest.TestCase):
    def setUp(self):
        self.handle = native.aiev_work_create()
        self.assertNotEqual(0, self.handle)
        self.sequence = 0
        self.directory = tempfile.TemporaryDirectory(prefix="evolution-c-work-")
        self.addCleanup(self.directory.cleanup)
        self.addCleanup(lambda: native.aiev_work_close(self.handle))

    def call(self, op, **fields):
        self.sequence += 1
        self.assertEqual(0, submit(self.handle, {**fields, "protocol": 1, "id": self.sequence, "op": op}))
        response = read(self.handle)
        self.assertEqual(self.sequence, response["id"])
        self.assertEqual(1, response["protocol"])
        self.assertTrue(response["ok"], response)
        return response

    def open(self):
        return self.call("open", config={"directory": self.directory.name, "runId": "run", "compatibilityHash": "compat",
                                         "limits": {"cost": "100"}})

    def reopen(self):
        previous = self.handle
        self.assertEqual(0, native.aiev_work_close(previous))
        self.assertEqual(-1, native.aiev_work_read(previous, None, 0))
        self.handle = native.aiev_work_create()
        self.assertGreater(self.handle, previous)
        self.assertTrue(self.open()["wasRecovered"])

    def test_actual_protocol_recovery_and_exact_receipt(self):
        self.assertEqual(1, native.aiev_work_abi_version())
        self.assertFalse(self.open()["wasRecovered"])
        self.assertTrue(self.call("enqueue", job=JOB)["enqueued"])
        lease = self.call("claim", worker={"workerId": "one", "compatibilityHash": "compat"})["lease"]
        self.assertEqual(JOB["evaluationId"], lease["identity"]["evaluationId"])
        self.reopen()
        status = self.call("status")
        self.assertEqual("5", status["reserved"]["cost"])
        self.assertFalse(status["supportsExactSearchContinuation"])
        receipt = {"identity": lease["identity"], "workerId": "one", "payload": "49", "provenance": "ctypes-square-v1",
                   "actual": {"cost": "0.1234567890123456789012345678"}, "outcome": "completed"}
        # Execute an actual authored evaluator, not a fabricated engine quality result.
        receipt["payload"] = str(int(lease["payload"]) ** 2)
        self.assertEqual("accepted", self.call("commit", **receipt)["disposition"])
        self.assertEqual("duplicate", self.call("commit", **receipt)["disposition"])
        self.reopen()
        self.assertEqual("49", self.call("result", evaluationId=JOB["evaluationId"], attempt=1)["result"]["payload"])
        status = self.call("status")
        self.assertEqual(receipt["actual"], status["spent"])
        self.assertEqual("0", status["reserved"]["cost"])
        self.assertEqual("1", status["settled"])

    def test_size_queries_and_short_buffers_never_repeat_mutation(self):
        self.open()
        request = {"protocol": 1, "id": 25, "op": "enqueue", "job": JOB}
        self.assertEqual(0, submit(self.handle, request))
        self.assertEqual(-3, submit(self.handle, request))
        size = native.aiev_work_read(self.handle, None, 0)
        sentinel = ctypes.create_string_buffer(b"untouched")
        for _ in range(3):
            self.assertEqual(size, native.aiev_work_read(self.handle, sentinel, 1))
            self.assertEqual(b"untouched", sentinel.value)
            self.assertEqual(size, native.aiev_work_read(self.handle, None, 0))
        self.assertTrue(read(self.handle)["enqueued"])
        self.assertEqual(-4, native.aiev_work_read(self.handle, None, 0))
        self.assertFalse(self.call("enqueue", job=JOB)["enqueued"])

    def test_invalid_buffers_utf8_and_handles_fail_without_execution(self):
        self.assertEqual(-2, native.aiev_work_submit(self.handle, None, 2))
        self.assertEqual(-2, native.aiev_work_submit(self.handle, b"x", MAXIMUM + 1))
        self.assertEqual(-2, native.aiev_work_submit(self.handle, b"x", -1))
        self.assertEqual(-2, native.aiev_work_submit(self.handle, b"\xff", 1))
        self.assertEqual(-2, native.aiev_work_read(self.handle, None, 1))
        self.assertEqual(-2, native.aiev_work_read(self.handle, None, -1))
        self.assertEqual(-1, native.aiev_work_read(0, None, 0))
        self.assertEqual(-1, native.aiev_work_submit(0, b"{}", 2))
        self.assertFalse(self.open()["wasRecovered"])

    def test_handle_capacity_and_stale_handle_fencing(self):
        extra = []
        try:
            for _ in range(7):
                handle = native.aiev_work_create()
                self.assertNotEqual(0, handle)
                extra.append(handle)
            self.assertEqual(0, native.aiev_work_create())
        finally:
            for handle in extra:
                self.assertEqual(0, native.aiev_work_close(handle))
                self.assertEqual(-1, native.aiev_work_close(handle))
        self.open()
        self.reopen()

    def test_concurrent_submit_keeps_one_reply_and_one_mutation(self):
        self.open()
        request = {"protocol": 1, "id": 50, "op": "enqueue", "job": JOB}
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
            statuses = list(pool.map(lambda _: submit(self.handle, request), range(16)))
        self.assertEqual(1, statuses.count(0))
        self.assertEqual(15, statuses.count(-3))
        self.assertTrue(read(self.handle)["enqueued"])
        self.assertFalse(self.call("enqueue", job=JOB)["enqueued"])

    def test_protocol_errors_and_close_reply_are_not_abi_failures(self):
        self.assertEqual(0, submit(self.handle, {"protocol": 0, "id": 1, "op": "status"}))
        self.assertFalse(read(self.handle)["ok"])
        self.open()
        self.call("close")
        self.assertEqual(0, submit(self.handle, {"protocol": 1, "id": 99, "op": "status"}))
        self.assertFalse(read(self.handle)["ok"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
