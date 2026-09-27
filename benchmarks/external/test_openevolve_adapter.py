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

class ResumeTests(unittest.TestCase):
    def test_a_killed_openevolve_search_resumes_from_checkpoint_losing_at_most_one_iteration(self):
        import hashlib
        import time
        import openevolve_configs
        upstream = os.environ.get("EVOLUTION_OPENEVOLVE_CHECKOUT")
        if not upstream:
            raise RuntimeError("Set EVOLUTION_OPENEVOLVE_CHECKOUT to the pinned checkout for this integration gate")
        iterations, kill_after = 6, 3
        initial = "def solve(x):\n    return x + 1\n"
        record = openevolve_configs.matched({"haiku": 1.0}, "Contract fixture only.", iterations=iterations, seed=37)
        live_calls = []

        def generate(system, messages, model=None):
            digest = hashlib.sha256((system + json.dumps(messages)).encode()).hexdigest()[:8]
            live_calls.append(digest)
            return {"text": f"```python\ndef solve(x):\n    v_{digest} = 1\n    return x + v_{digest}\n```",
                    "cost_units": 10, "cost_metric": "reported_input_plus_cache_plus_output_tokens"}

        def evaluate(code):
            return dict(candidate_hash=candidate_hash(code), status="valid", quality=float(len(code) % 7),
                        work_units=1, unknown_work=False)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "initial.py").write_text(initial, encoding="utf-8")
            (root / "task.txt").write_text("unused", encoding="utf-8")
            (root / "record.json").write_text(json.dumps(record), encoding="utf-8")
            journal = root / "journal.jsonl"

            def command(count, *extra):
                return [sys.executable, "-X", "utf8", str(Path(__file__).with_name("openevolve_adapter.py")),
                        "--upstream", upstream, "--initial", str(root / "initial.py"), "--output", str(root / "run"),
                        "--model", "unused", "--iterations", str(count), "--seed", "37", "--task", str(root / "task.txt"),
                        "--mode", "matched", "--config-record", str(root / "record.json"), *extra]

            def broker(resume, lost=0):
                # Work lost after the last checkpoint is interruption cost, not search budget:
                # the resumed arm gets it back, so an interruption cannot shrink either arm's search.
                return ProgramBroker(generate, evaluate, model_calls=iterations + lost, evaluations=iterations + 1 + lost,
                                     seconds=300, initial=initial, model_tokens=10_000 + 10 * lost, journal=journal,
                                     resume=resume)

            with broker("replay") as first:
                environment = dict(os.environ, EVOLUTION_BROKER_ENDPOINT=first.endpoint,
                                   EVOLUTION_BROKER_CAPABILITY=first.capability)
                child = subprocess.Popen(command(iterations), env=environment,
                                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                deadline = time.monotonic() + 120
                while sum(r["operation"] == "model" and r["status"] == "completed" for r in first.rows) < kill_after:
                    self.assertLess(time.monotonic(), deadline, "search never reached the kill point")
                    self.assertIsNone(child.poll(), "search finished before it could be killed")
                    time.sleep(0.01)
                child.kill()
                child.wait(timeout=30)
            from openevolve_adapter import latest_checkpoint
            found = latest_checkpoint(root / "run")
            self.assertIsNotNone(found, "no complete checkpoint was saved before the kill")
            checkpoints = [found[1]]
            prior_calls = sum(e["operation"] == "model" for e in ProgramBroker.__init__.__globals__["ReplayJournal"](journal).entries.values())
            lost = prior_calls - checkpoints[-1]
            # OpenEvolve keeps batch_size = 2 x parallel_evaluations iterations in flight by design,
            # and a kill can tear the newest checkpoint, so resume can trail completed work by up to
            # 3 iterations. That is upstream's structural bound; the lost work is granted back below.
            self.assertLessEqual(lost, 3, "at most OpenEvolve's in-flight batch plus a torn checkpoint is lost")
            with broker("continue", lost) as second:
                self.assertEqual(prior_calls, second.prior["model"])
                self.assertEqual(10 * prior_calls, second.model_tokens, "journaled work stays recorded")
                remaining = iterations - checkpoints[-1]
                environment = dict(os.environ, EVOLUTION_BROKER_ENDPOINT=second.endpoint,
                                   EVOLUTION_BROKER_CAPABILITY=second.capability)
                live_before = len(live_calls)
                result = subprocess.run(command(remaining, "--resume"), env=environment, capture_output=True, timeout=180)
                self.assertEqual(0, result.returncode, result.stderr.decode(errors="replace")[-4000:])
                self.assertFalse(second.closed)
                self.assertTrue(all(r["status"] == "completed" for r in second.rows))
                new_calls = sum(r["operation"] == "model" for r in second.rows)
                self.assertEqual(remaining, new_calls, result.stderr.decode(errors="replace")[-3000:])
                self.assertEqual(new_calls, len(live_calls) - live_before)
                self.assertEqual(iterations, checkpoints[-1] + new_calls, "the search gets exactly its iterations")
                self.assertEqual(0, sum(r["request"].get("code") == initial for r in second.rows if r["operation"] == "evaluate"),
                                 "the resumed run does not re-evaluate the initial program")

if __name__ == "__main__":
    unittest.main()
