import base64
import json
from pathlib import Path
import tempfile
import unittest

from docker_sandbox import encode, MAX_REQUEST, MAX_OUTPUT
from program_tasks import TASKS, expected
from run_program_audit import cases, run


class ProgramAuditTests(unittest.TestCase):
    def test_byte_boundaries_fit_actual_transport_limits(self):
        for task in ("base64_encoding", "sha256_hashing"):
            inputs = cases(task, 19)
            lengths = [len(base64.b64decode(row["plaintext"]["$bytes"])) for row in inputs]
            self.assertEqual(list(range(66)), lengths[:66])
            self.assertEqual([65535, 65536, 65537, 524287, 524288], lengths[66:])
            self.assertLess(len(encode({"class": TASKS[task]["class"], "problems": inputs})), MAX_REQUEST)
            self.assertLess(len(encode(expected(task, inputs))), MAX_OUTPUT)
            self.assertEqual(inputs, cases(task, 19))
            self.assertNotEqual(inputs, cases(task, 20))

    def test_graph_oracle_rejects_edge_count_shortcuts(self):
        answers = expected("count_connected_components", cases("count_connected_components", 19))
        self.assertEqual([0, 1, 512, 1, 1, 1, 1, 52], [row["number_connected_components"] for row in answers])

    def test_empty_or_running_panel_cannot_be_a_passing_audit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for status in ("running", "completed", "failed"):
                (root / "report.json").write_text(json.dumps({"status": status, "tasks": [], "planned_tasks": []}))
                with self.assertRaises(ValueError):
                    run(root, root / "audit", "unused")
                self.assertFalse((root / "audit").exists())
