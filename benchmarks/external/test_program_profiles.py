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

    def test_a_declared_track_panel_is_the_panel_that_runs(self):
        """The arm that was missing: the old tests only checked that a bad panel is refused.

        `tracks` was validated and then overwritten by the full six-track list, so a caller asking
        for one track ran six - six times the model calls it budgeted. Measured before the fix:
        `runs` was 6 for this exact call. `run_warm_study` passes `tracks=row["tracks"]` and was
        affected in production.

        No host and no model: an empty file satisfies the binary hash, and the generate/evaluate
        callbacks are stubs, so this asserts the panel without paying for a campaign.
        """
        for panel in ([("controlled", "aidotnet")],
                      [("native-bounded", "openevolve"), ("controlled", "one-shot")]):
            with self.subTest(panel=panel), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                dll = root / "fake.dll"
                dll.write_bytes(b"")
                report = run_campaign(root / "run", str(dll), str(root), "x = 1\n", "task", "fixture",
                                      lambda *args, **kwargs: ("", {}),
                                      lambda *args, **kwargs: {"combined_score": 1.0},
                                      iterations=1, tracks=panel, evidence_class="contract-only",
                                      evaluator_manifest={"identity": "fixture"})
                self.assertEqual([tuple(track) for track in panel], [tuple(t) for t in report["schedule"]])
                self.assertEqual(len(panel), len(report["runs"]),
                                 "a declared panel of %d ran %d tracks" % (len(panel), len(report["runs"])))

    def test_no_declaration_still_runs_the_whole_panel(self):
        """The other direction, so the fix cannot be read as "tracks shrinks the default"."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            dll = root / "fake.dll"
            dll.write_bytes(b"")
            report = run_campaign(root / "run", str(dll), str(root), "x = 1\n", "task", "fixture",
                                  lambda *args, **kwargs: ("", {}),
                                  lambda *args, **kwargs: {"combined_score": 1.0},
                                  iterations=1, evidence_class="contract-only",
                                  evaluator_manifest={"identity": "fixture"})
            self.assertEqual(6, len(report["schedule"]))
            self.assertEqual(6, len(report["runs"]))

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
