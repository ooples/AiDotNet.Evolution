"""The cell executor's contract: the endpoint is held-out, and a searcher cannot report its own score.

The pilot's whole credibility rests on the arms not scoring themselves, so these tests inject
searchers that try to - a stub that returns the ground truth, one that returns a broken program, and
one that hands back a tampered split - and require the executor's number to come from the sealed
held-out split in every case.
"""
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

import pilot_cells  # noqa: E402
import symbolic_regression_family as family  # noqa: E402

TASK = "rational_gain"
SEED = family.DEV_SEEDS[0]


def program(directory, body):
    path = Path(directory) / "candidate.py"
    path.write_text(body, encoding="utf-8")
    return str(path)


def cell(arm, seed=SEED):
    return {"Cell": f"{TASK}/{arm}/{seed}", "Family": TASK, "Arm": arm, "Seed": seed, "Block": 0, "Position": 0}


class PilotCellTests(unittest.TestCase):
    def test_the_endpoint_is_the_held_out_score_not_the_searchers_claim(self):
        # The stub reports an absurd score of its own and returns the ground-truth program. The
        # executor must ignore the claim and report the held-out NMSE, which for the truth is ~0.
        def searcher(_cell, directory, _resume):
            body = "import math\n\n\ndef predict(x):\n    return (3.0 * x + 1.0) / (x * x + 2.0)\n"
            return {"program": program(directory, body), "combined_score": 999.0,
                    "model_calls": 4, "model_tokens": 1234}

        with tempfile.TemporaryDirectory() as directory:
            outcome = pilot_cells.executor(TASK, {"aidotnet": searcher})(cell("aidotnet"), directory, False)
        self.assertEqual("completed", outcome["Status"])
        self.assertLess(outcome["Value"], 1e-24, "the truth must score ~0 held-out NMSE")
        self.assertNotIn(999.0, outcome.values(), "the searcher's own claim must not reach the row")
        self.assertEqual((4, 1234), (outcome["ModelCalls"], outcome["ModelTokens"]))

    def test_a_constant_predictor_scores_about_one_and_the_truth_beats_it(self):
        # Two arms, one cell each, so the executor is shown to discriminate rather than to return a
        # constant. Without this, "the truth scores 0" is also what a broken scorer reports.
        def constant(_cell, directory, _resume):
            return {"program": program(directory, "def predict(x):\n    return 0.0\n"), "model_calls": 1}

        def truth(_cell, directory, _resume):
            return {"program": program(directory, "def predict(x):\n    return (3.0*x + 1.0)/(x*x + 2.0)\n"),
                    "model_calls": 1}

        run = pilot_cells.executor(TASK, {"aidotnet": truth, "openevolve-recommended": constant})
        with tempfile.TemporaryDirectory() as directory:
            ours = run(cell("aidotnet"), directory, False)
            theirs = run(cell("openevolve-recommended"), directory, False)
        self.assertLess(ours["Value"], theirs["Value"], "minimizing endpoint: the truth must be lower")
        self.assertGreater(theirs["Value"], 0.5, "a constant cannot beat the mean by much")

    def test_a_broken_program_is_a_failed_cell_not_a_quiet_value(self):
        for body in ("def predict(x):\n    raise ZeroDivisionError('boom')\n",
                     "def predict(x):\n    return float('nan')\n",
                     "x = 1\n"):
            with self.subTest(body=body.splitlines()[0]), tempfile.TemporaryDirectory() as directory:
                def searcher(_cell, where, _resume, body=body):
                    return {"program": program(where, body), "model_calls": 2, "model_tokens": 7}

                outcome = pilot_cells.executor(TASK, {"aidotnet": searcher})(cell("aidotnet"), directory, False)
                self.assertEqual("failed", outcome["Status"])
                self.assertIsNone(outcome.get("Value"), "a failed cell must carry no value")
                # The accounting survives a failure: a cell that spent calls still reports them.
                self.assertEqual((2, 7), (outcome["ModelCalls"], outcome["ModelTokens"]))

    def test_a_searcher_that_raises_is_a_failed_cell_and_names_the_exception(self):
        def searcher(_cell, _directory, _resume):
            raise TimeoutError("provider timed out")

        with tempfile.TemporaryDirectory() as directory:
            outcome = pilot_cells.executor(TASK, {"aidotnet": searcher})(cell("aidotnet"), directory, False)
        self.assertEqual("failed", outcome["Status"])
        self.assertEqual("TimeoutError", outcome["Reason"])

    def test_a_tampered_held_out_split_fails_the_cell_rather_than_scoring_it(self):
        # The seal is the endpoint's integrity. If a run could hand back a split whose test rows had
        # moved, the pilot's number would be whatever that run chose.
        def searcher(_cell, directory, _resume):
            return {"program": program(directory, "def predict(x):\n    return 0.0\n"), "model_calls": 1}

        genuine = pilot_cells.family.build
        tampered = genuine(TASK, SEED)
        tampered["test"] = [(xs, y + 1.0) for xs, y in tampered["test"]]
        try:
            pilot_cells.family.build = lambda *args, **kwargs: tampered
            with tempfile.TemporaryDirectory() as directory:
                outcome = pilot_cells.executor(TASK, {"aidotnet": searcher})(cell("aidotnet"), directory, False)
        finally:
            pilot_cells.family.build = genuine
        self.assertEqual("failed", outcome["Status"])
        self.assertEqual("ValueError", outcome["Reason"], "family.score must refuse an unsealed split")

    def test_an_undeclared_arm_is_refused_rather_than_failed(self):
        # A missing arm is a campaign that was not configured. Returning "failed" would let the
        # analysis substitute a failure value and report a result for an arm that never ran.
        def searcher(_cell, directory, _resume):
            return {"program": program(directory, "def predict(x):\n    return 0.0\n")}

        run = pilot_cells.executor(TASK, {"aidotnet": searcher})
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(KeyError):
                run(cell("openevolve-matched"), directory, False)

    def test_campaign_runner_drives_the_executor_end_to_end(self):
        """The wiring that has never existed: a registration, every cell run, rows `analyze` accepts.

        Rigged stubs, so this asserts the **plumbing** and not a finding - which is why the arms are
        swapped below. No model and no spend: each searcher writes a fixed program.
        """
        sys.path.insert(0, str(ROOT.parent / "analysis"))
        import campaign_registration as cr
        import campaign_runner as runner

        def searchers(ours_body, theirs_body):
            def make(body):
                def search(_cell, directory, _resume):
                    return {"program": program(directory, body), "model_calls": 2, "model_tokens": 100}
                return search
            return {"aidotnet": make(ours_body),
                    "openevolve-recommended": make(theirs_body),
                    "openevolve-matched": make(theirs_body)}

        truth = "def predict(x):\n    return (3.0*x + 1.0)/(x*x + 2.0)\n"
        constant = "def predict(x):\n    return 0.0\n"
        verdicts = {}
        for label, (ours, theirs) in {"ours-better": (truth, constant), "ours-worse": (constant, truth)}.items():
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                registration = cr.register(dict(
                    Campaign="pilot-plumbing", Budget=dict(ModelTokens=1000, ModelCalls=10, WallSeconds=60),
                    Models=["sonnet"], SeedCount=6, BootstrapSamples=1000, BootstrapSeed=11,
                    FamilywiseAlpha=0.05,
                    Families=[dict(Family=TASK, Configs=["recommended", "matched"],
                                   Endpoint=dict(Metric="nmse", Direction="minimize", FailureValue=1e6))],
                    TestPartitionHashes={TASK: "a" * 64}, Published=None), root / "registration")
                projection = runner.project(registration)
                rows = runner.run(root / "registration" / "registration.json", root / "state",
                                  pilot_cells.executor(TASK, searchers(ours, theirs)),
                                  confirm=projection["ProjectionSha256"], log=lambda *args: None)
                self.assertEqual(18, len(rows), "3 arms x 6 seeds")
                self.assertEqual({"completed"}, {row["Status"] for row in rows})
                report = cr.analyze(registration, rows)
                verdicts[label] = [endpoint["Superior"] for endpoint in report["Endpoints"]]

        self.assertEqual([True, True], verdicts["ours-better"])
        # The control. Without it, "superior" could be hardcoded to our arm and this would still pass.
        self.assertEqual([False, False], verdicts["ours-worse"])

    def test_an_unknown_task_or_no_searcher_is_refused_at_construction(self):
        with self.assertRaises(KeyError):
            pilot_cells.executor("no_such_task", {"aidotnet": lambda *a: None})
        with self.assertRaises(ValueError):
            pilot_cells.executor(TASK, {})


if __name__ == "__main__":
    unittest.main()
