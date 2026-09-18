"""Integration fixture: real pinned OpenEvolve, synthetic model/evaluator, zero API calls.

Kept separate from numeric-only discovery; the final program gate supplies the checkout.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from program_broker import ProgramBroker
from program_controls import candidate_hash


class OpenEvolveAdapterTests(unittest.TestCase):
    def test_real_controller_uses_shared_start_and_broker_without_api_access(self):
        upstream = os.environ.get("EVOLUTION_OPENEVOLVE_CHECKOUT")
        if not upstream:
            raise RuntimeError("Set EVOLUTION_OPENEVOLVE_CHECKOUT to the pinned checkout for this integration gate")
        initial = "# Erdős–Rényi “reference”\ndef solve(x):\n    return x + 1\n"
        evolved = "# Erdős–Rényi “candidate”\ndef solve(x):\n    return 1 + x\n"

        def evaluate(code):
            # Contract fixture does not execute or claim optimization of candidate code.
            return dict(candidate_hash=candidate_hash(code), status="valid", quality=1.0,
                        work_units=1, unknown_work=False)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "initial.py"
            source.write_text(initial, encoding="utf-8")
            task = root / "task.txt"
            task.write_text("Contract fixture only.", encoding="utf-8")
            with ProgramBroker(lambda system, messages: {"text": "```python\n" + evolved + "```", "cost_units": 0,
                               "cost_metric": "reported_input_plus_output_tokens"}, evaluate,
                               model_calls=2, evaluations=3, seconds=120, initial=initial) as broker:
                environment = dict(os.environ)
                environment.update(EVOLUTION_BROKER_ENDPOINT=broker.endpoint,
                                   EVOLUTION_BROKER_CAPABILITY=broker.capability)
                for key in ("OPENAI_API_KEY", "CODEX_API_KEY"):
                    environment.pop(key, None)
                result = subprocess.run([sys.executable, "-X", "utf8", str(Path(__file__).with_name("openevolve_adapter.py")),
                                         "--upstream", upstream, "--initial", str(source), "--output", str(root / "result"),
                                         "--model", "contract-fixture-no-provider", "--iterations", "2", "--seed", "37",
                                         "--task", str(task), "--mode", "native-bounded"],
                                        env=environment, capture_output=True, timeout=120)
                self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace")[-5000:])
                rows = broker.rows
                self.assertFalse(broker.closed)
                self.assertTrue(all(row["status"] == "completed" for row in rows))
                self.assertEqual(2, sum(row["operation"] == "model" for row in rows))
                self.assertEqual(3, sum(row["operation"] == "evaluate" for row in rows))
                self.assertEqual(initial, rows[0]["request"]["code"])
                report = json.loads((root / "result/adapter-result.json").read_text(encoding="utf-8"))
                self.assertEqual("native-bounded", report["mode"])


if __name__ == "__main__":
    unittest.main()
