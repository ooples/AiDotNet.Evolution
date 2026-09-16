import os
from pathlib import Path
import tempfile
import unittest

from program_controls import candidate_hash
from run_program_comparison import run_campaign


class ProgramProfileTests(unittest.TestCase):
    def test_invalid_profile_refused_before_output_or_model_work(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)/"must-not-exist"
            with self.assertRaises(ValueError):
                run_campaign(root,"missing.dll","missing","x=1","task","fixture",None,None,
                             evolution_profile="unknown",evidence_class="contract-only",evaluator_manifest={"identity":"fixture"})
            self.assertFalse(root.exists())

    def test_invalid_openevolve_profile_and_partial_track_declarations_fail_before_work(self):
        for kwargs in (dict(openevolve_profile="unknown"), dict(tracks=[]),
                       dict(tracks=[("controlled","aidotnet")]*2), dict(tracks=[("unknown","aidotnet")])):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)/"must-not-exist"
                with self.assertRaises(ValueError):
                    run_campaign(root,"missing.dll","missing","x=1","task","fixture",None,None,
                                 evidence_class="contract-only",evaluator_manifest={"identity":"fixture"},**kwargs)
                self.assertFalse(root.exists())

    @unittest.skipUnless(os.environ.get("EVOLUTION_PROFILE_DLL") and os.environ.get("EVOLUTION_PROFILE_UPSTREAM"), "Requires built host and pinned OpenEvolve")
    def test_best_profile_actual_controllers_exploit_after_first_improvement(self):
        with tempfile.TemporaryDirectory() as directory:
            initial="def solve(x):\n    return x\n" + "# original padding\n"*100
            calls=0
            def generate(system,messages):
                nonlocal calls
                calls+=1
                return dict(text=f"```python\n# score:{calls+2}\ndef solve(x):\n    return x\n```",cost_units=10,
                            cost_metric="reported_input_plus_output_tokens")
            def evaluate(code):
                score=float(code.splitlines()[0].split(":")[1]) if code.startswith("# score:") else 1.0
                return dict(candidate_hash=candidate_hash(code),status="valid",quality=score,work_units=1,unknown_work=False)
            value=run_campaign(Path(directory)/"campaign",os.environ["EVOLUTION_PROFILE_DLL"],os.environ["EVOLUTION_PROFILE_UPSTREAM"],
                               initial,"nonexecuting score fixture","fixture",generate,evaluate,iterations=4,evolution_profile="best",
                               openevolve_profile="best",evidence_class="contract-only",evaluator_manifest={"identity":"nonexecuting-fixture"})
            self.assertEqual("completed",value["status"])
            self.assertEqual(21,calls)
            self.assertEqual("best",value["openevolve_profile"])
            self.assertTrue(all(r["search_wall_seconds"] > 0 for r in value["runs"]))
            for row in value["runs"]:
                if row["method"] == "aidotnet":
                    models=[r for r in row["receipts"] if r["operation"] == "model"]
                    self.assertEqual(4,len(models))
                    for receipt in models[1:]:
                        prompt=receipt["request"]["messages"][0]["content"]
                        self.assertTrue(prompt.startswith("Parent program:\n```python\n# score:"))
                    self.assertEqual(4,row["independent_counters"]["attempted"]["model"])
