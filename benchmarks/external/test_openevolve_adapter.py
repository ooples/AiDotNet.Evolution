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


class NativeConfigTests(unittest.TestCase):
    """Recommended and matched configs drive upstream's own OpenAI client through the broker shim."""

    def run_native(self, kind):
        import openevolve_configs
        upstream = os.environ.get("EVOLUTION_OPENEVOLVE_CHECKOUT")
        if not upstream:
            raise RuntimeError("Set EVOLUTION_OPENEVOLVE_CHECKOUT to the pinned checkout for this integration gate")
        initial = "def solve(x):\n    return x + 1\n"
        if kind == "recommended":
            record = openevolve_configs.recommended(
                Path(upstream) / "examples/algotune/fft_convolution/config.yaml", iterations=2, seed=37)
            # The shipped config is diff-based, so the fixture answers in upstream's diff format.
            reply = "<<<<<<< SEARCH\n    return x + 1\n=======\n    return 1 + x\n>>>>>>> REPLACE\n"
        else:
            record = openevolve_configs.matched({"haiku": 0.5, "sonnet": 0.3, "opus": 0.2}, "Contract fixture only.",
                                                iterations=2, seed=37)
            reply = "```python\ndef solve(x):\n    return 1 + x\n```"
        calls = []

        def generate(system, messages, model=None):
            calls.append(model)
            return {"text": reply, "cost_units": 0, "cost_metric": "reported_input_plus_cache_plus_output_tokens"}

        def evaluate(code):
            return dict(candidate_hash=candidate_hash(code), status="valid", quality=1.0, work_units=1, unknown_work=False)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "initial.py").write_text(initial, encoding="utf-8")
            (root / "task.txt").write_text("Contract fixture only.", encoding="utf-8")
            (root / "record.json").write_text(json.dumps(record), encoding="utf-8")
            with ProgramBroker(generate, evaluate, model_calls=2, evaluations=3, seconds=120, initial=initial) as broker:
                environment = dict(os.environ)
                environment.update(EVOLUTION_BROKER_ENDPOINT=broker.endpoint, EVOLUTION_BROKER_CAPABILITY=broker.capability)
                for key in ("OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL"):
                    environment.pop(key, None)
                result = subprocess.run([sys.executable, "-X", "utf8", str(Path(__file__).with_name("openevolve_adapter.py")),
                                         "--upstream", upstream, "--initial", str(root / "initial.py"),
                                         "--output", str(root / "result"), "--model", "unused", "--iterations", "2",
                                         "--seed", "37", "--task", str(root / "task.txt"), "--mode", kind,
                                         "--config-record", str(root / "record.json")],
                                        env=environment, capture_output=True, timeout=180)
                self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace")[-5000:])
                models = [row for row in broker.rows if row["operation"] == "model"]
                self.assertFalse(broker.closed)
                self.assertTrue(all(row["status"] == "completed" for row in broker.rows))
                self.assertEqual(2, len(models))
                self.assertEqual(broker.chat_received, len(models), "unrecorded prompts")
                self.assertTrue(set(calls) <= set(record["models"]))
                self.assertTrue(all(row["request"]["model"] in record["models"] and row["request"]["system"]
                                    for row in models))
                self.assertIn("temperature", models[0]["request"]["ignored_sampling"])
                report = json.loads((root / "result/adapter-result.json").read_text(encoding="utf-8"))
                self.assertEqual((kind, record["changes"]), (report["mode"], report["changes"]))
                return models

    def test_recommended_config_runs_verbatim_through_the_shim(self):
        models = self.run_native("recommended")
        self.assertIn("autonomous programmer", models[0]["request"]["system"])

    def test_matched_config_runs_through_the_shim(self):
        models = self.run_native("matched")
        self.assertTrue(models[0]["request"]["system"].startswith("Contract fixture only."))

if __name__ == "__main__":
    unittest.main()
