"""Required gate: our engine resumes a killed search by broker replay, losing at most one request."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest

from program_broker import ProgramBroker
from program_controls import candidate_hash


@unittest.skipUnless(os.environ.get("EVOLUTION_PROFILE_DLL"), "Requires the built EvolutionComparison host")
class EngineReplayTests(unittest.TestCase):
    def test_killed_engine_search_replays_deterministically(self):
        iterations, kill_after = 8, 4
        initial = "def solve(x):\n    return x + 1\n"
        live = []

        def generate(system, messages):
            digest = hashlib.sha256((system + json.dumps(messages)).encode()).hexdigest()[:8]
            live.append(digest)
            return {"text": f"```python\ndef solve(x):\n    v_{digest} = 1\n    return x + v_{digest}\n```",
                    "cost_units": 10, "cost_metric": "reported_input_plus_output_tokens"}

        def evaluate(code):
            return dict(candidate_hash=candidate_hash(code), status="valid", quality=float(len(code) % 7),
                        work_units=1, unknown_work=False)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            # newline="": the host hashes the exact bytes; CRLF would read as a changed start.
            (root / "initial.py").write_text(initial, encoding="utf-8", newline="")
            (root / "task.txt").write_text("fixture", encoding="utf-8", newline="")
            journal = root / "journal.jsonl"

            def command(output):
                return ["dotnet", os.environ["EVOLUTION_PROFILE_DLL"], str(root / "initial.py"), str(root / output),
                        "fixture", str(iterations), "37", str(root / "task.txt"), "native-bounded", "uniform"]

            def broker():
                return ProgramBroker(generate, evaluate, model_calls=iterations, evaluations=iterations + 1,
                                     seconds=300, initial=initial, model_tokens=10_000, journal=journal)

            with broker() as first:
                environment = dict(os.environ, EVOLUTION_BROKER_ENDPOINT=first.endpoint,
                                   EVOLUTION_BROKER_CAPABILITY=first.capability)
                child = subprocess.Popen(command("killed.json"), env=environment,
                                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                deadline = time.monotonic() + 120
                while sum(r["operation"] == "model" and r["status"] == "completed" for r in first.rows) < kill_after:
                    self.assertLess(time.monotonic(), deadline, first.refusals)
                    self.assertIsNone(child.poll(), f"search ended before the kill point: {first.refusals}")
                    time.sleep(0.005)
                child.kill()
                child.wait(timeout=30)
            dispatched = len(first.rows)
            live_before = len(live)
            with broker() as second:
                environment = dict(os.environ, EVOLUTION_BROKER_ENDPOINT=second.endpoint,
                                   EVOLUTION_BROKER_CAPABILITY=second.capability)
                result = subprocess.run(command("resumed.json"), env=environment, capture_output=True, timeout=300)
                self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace")[-3000:])
                self.assertEqual([], second.refusals)
                self.assertFalse(second.closed)
                journaled = second.journal.replayed
                self.assertLessEqual(dispatched - journaled, 1, "at most the in-flight request is lost")
                models = [r for r in second.rows if r["operation"] == "model"]
                self.assertEqual(iterations, len(models))
                self.assertTrue(all(r["status"] == "completed" for r in second.rows))
                self.assertEqual(iterations - sum(r["replayed"] for r in models), len(live) - live_before,
                                 "replayed calls cost no new generation")
                self.assertEqual(10 * iterations, second.model_tokens, "replayed work stays in the budget")


if __name__ == "__main__":
    unittest.main()