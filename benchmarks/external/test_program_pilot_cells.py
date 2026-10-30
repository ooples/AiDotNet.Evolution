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
            outcome = pilot_cells.executor(TASK, {"aidotnet": searcher}, holdout_seed=918273)(cell("aidotnet"), directory, False)
        self.assertEqual("completed", outcome["Status"])
        self.assertLess(outcome["Value"], 1e-24, "the truth must score ~0 held-out NMSE")
        self.assertNotIn(999.0, outcome.values(), "the searcher's own claim must not reach the row")
        self.assertEqual((4, 1234), (outcome["ModelCalls"], outcome["ModelTokens"]))

    def test_our_arms_champion_decision_reaches_the_row_and_no_other_arm_gets_one(self):
        champion = {"StandardErrors": 1.0, "BeatSeed": True}

        def searcher(with_champion):
            def search(_cell, directory, _resume):
                outcome = {"program": program(directory, "def predict(x):\n    return 0.0\n"), "model_calls": 1}
                if with_champion:
                    outcome["champion"] = champion
                return outcome
            return search

        run = pilot_cells.executor(TASK, {"aidotnet": searcher(True), "openevolve-matched": searcher(False)}, holdout_seed=918273)
        with tempfile.TemporaryDirectory() as directory:
            ours = run(cell("aidotnet"), directory, False)
        with tempfile.TemporaryDirectory() as directory:
            theirs = run(cell("openevolve-matched"), directory, False)
        self.assertEqual(champion, ours["Champion"])
        self.assertNotIn("Champion", theirs)

    def test_a_constant_predictor_scores_about_one_and_the_truth_beats_it(self):
        # Two arms, one cell each, so the executor is shown to discriminate rather than to return a
        # constant. Without this, "the truth scores 0" is also what a broken scorer reports.
        def constant(_cell, directory, _resume):
            return {"program": program(directory, "def predict(x):\n    return 0.0\n"), "model_calls": 1}

        def truth(_cell, directory, _resume):
            return {"program": program(directory, "def predict(x):\n    return (3.0*x + 1.0)/(x*x + 2.0)\n"),
                    "model_calls": 1}

        run = pilot_cells.executor(TASK, {"aidotnet": truth, "openevolve-recommended": constant}, holdout_seed=918273)
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

                outcome = pilot_cells.executor(TASK, {"aidotnet": searcher}, holdout_seed=918273)(cell("aidotnet"), directory, False)
                self.assertEqual("failed", outcome["Status"])
                self.assertIsNone(outcome.get("Value"), "a failed cell must carry no value")
                # The accounting survives a failure: a cell that spent calls still reports them.
                self.assertEqual((2, 7), (outcome["ModelCalls"], outcome["ModelTokens"]))

    def test_a_searcher_that_raises_is_a_failed_cell_and_names_the_exception(self):
        def searcher(_cell, _directory, _resume):
            raise TimeoutError("provider timed out")

        with tempfile.TemporaryDirectory() as directory:
            outcome = pilot_cells.executor(TASK, {"aidotnet": searcher}, holdout_seed=918273)(cell("aidotnet"), directory, False)
        self.assertEqual("failed", outcome["Status"])
        # Type AND message: eighteen cells reading "SearchFailed" name no cause at all, which is
        # what a re-pilot whose every cell failed actually printed.
        self.assertEqual("TimeoutError: provider timed out", outcome["Reason"])

    def test_a_long_failure_keeps_both_ends_of_its_message(self):
        """A subprocess failure carries a traceback, and its exception line is at the END.

        Bounding the message by its head alone - the obvious thing - cut off exactly the line that
        names the cause: every OpenEvolve cell of a rehearsal reported the same asyncio frame, and
        the `ModuleNotFoundError` that explained all of them was past the cut.
        """
        tail = "ModuleNotFoundError: No module named 'openevolve'"

        def searcher(_cell, _directory, _resume):
            raise RuntimeError("the adapter exited 1: " + "stack frame " * 200 + tail)

        with tempfile.TemporaryDirectory() as directory:
            outcome = pilot_cells.executor(TASK, {"aidotnet": searcher}, holdout_seed=918273)(
                cell("aidotnet"), directory, False)
        reason = outcome["Reason"]
        self.assertTrue(reason.startswith("RuntimeError: the adapter exited 1:"), reason)
        self.assertTrue(reason.endswith(tail), reason)
        self.assertLess(len(reason), 400, "a stack-sized string must not become the journal")

    def test_a_cell_that_spent_and_then_failed_still_reports_what_it_spent(self):
        """A failed cell used to report no calls at all, so the campaign's bill came out under.

        The searcher is the only thing that knows what the cell spent before it broke - its broker
        is already closed by the time the exception arrives - so it attaches the spend to the
        exception, and the row carries it. Without this the row said ModelCalls: None for a cell
        that really was billed for four calls.
        """
        class Failed(RuntimeError):
            accounting = {"model_calls": 4, "model_tokens": 1200, "throttle_seconds": 2.5, "lost_calls": 1}

        def searcher(_cell, _directory, _resume):
            raise Failed("the adapter exited 1")

        with tempfile.TemporaryDirectory() as directory:
            outcome = pilot_cells.executor(TASK, {"aidotnet": searcher}, holdout_seed=918273)(
                cell("aidotnet"), directory, False)
        self.assertEqual("failed", outcome["Status"])
        self.assertEqual("Failed: the adapter exited 1", outcome["Reason"])
        self.assertEqual(4, outcome["ModelCalls"])
        self.assertEqual(1200, outcome["ModelTokens"])
        self.assertEqual(2.5, outcome["ThrottleSeconds"])
        self.assertEqual(1, outcome["LostCalls"])

    def test_a_searcher_that_failed_without_spending_reports_nothing_rather_than_a_guess(self):
        def searcher(_cell, _directory, _resume):
            raise TimeoutError("provider timed out")

        with tempfile.TemporaryDirectory() as directory:
            outcome = pilot_cells.executor(TASK, {"aidotnet": searcher}, holdout_seed=918273)(
                cell("aidotnet"), directory, False)
        self.assertIsNone(outcome["ModelCalls"])
        self.assertIsNone(outcome["ModelTokens"])

    def test_a_tampered_held_out_split_fails_the_cell_rather_than_scoring_it(self):
        # The seal is the endpoint's integrity. If a run could hand back a split whose test rows had
        # moved, the pilot's number would be whatever that run chose.
        def searcher(_cell, directory, _resume):
            return {"program": program(directory, "def predict(x):\n    return 0.0\n"), "model_calls": 1}

        genuine = pilot_cells.family.holdout
        tampered = genuine(TASK, 918273)
        tampered["test"] = [(xs, y + 1.0) for xs, y in tampered["test"]]
        try:
            pilot_cells.family.holdout = lambda *args, **kwargs: tampered
            with tempfile.TemporaryDirectory() as directory:
                outcome = pilot_cells.executor(TASK, {"aidotnet": searcher}, holdout_seed=918273)(cell("aidotnet"), directory, False)
        finally:
            pilot_cells.family.holdout = genuine
        self.assertEqual("failed", outcome["Status"])
        self.assertEqual("ValueError: The held-out split does not match its seal, so its score would "
                         "not be held out.", outcome["Reason"],
                         "family.score must refuse an unsealed split, and say which refusal it was")

    def test_the_registered_partition_seal_is_the_one_the_endpoint_is_scored_against(self):
        """A pre-registered hash is only a commitment if it is the hash of the rows that get scored.

        Nothing downstream recomputes `TestPartitionHashes`, so a placeholder there would commit to
        nothing. `holdout_seal` is what a registration should carry, and it must match the seal
        `score_held_out` checks before it will report a number.
        """
        seal = pilot_cells.holdout_seal(TASK, 918273)
        self.assertEqual(family.holdout(TASK, 918273)["test_seal"], seal)
        self.assertNotEqual(seal, pilot_cells.holdout_seal(TASK, 918274), "a seed change must move the seal")
        self.assertNotEqual(seal, pilot_cells.holdout_seal("damped_oscillator", 918273))

    def test_an_undeclared_arm_is_refused_rather_than_failed(self):
        # A missing arm is a campaign that was not configured. Returning "failed" would let the
        # analysis substitute a failure value and report a result for an arm that never ran.
        def searcher(_cell, directory, _resume):
            return {"program": program(directory, "def predict(x):\n    return 0.0\n")}

        run = pilot_cells.executor(TASK, {"aidotnet": searcher}, holdout_seed=918273)
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

        champion = {"StandardErrors": 1.0, "BeatSeed": False}

        def searchers(ours_body, theirs_body):
            def make(body, decision=None):
                def search(_cell, directory, _resume):
                    outcome = {"program": program(directory, body), "model_calls": 2, "model_tokens": 100}
                    if decision is not None:
                        outcome["champion"] = decision
                    return outcome
                return search
            return {"aidotnet": make(ours_body, champion),
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
                                  pilot_cells.executor(TASK, searchers(ours, theirs), holdout_seed=918273),
                                  confirm=projection["ProjectionSha256"], log=lambda *args: None)
                self.assertEqual(18, len(rows), "3 arms x 6 seeds")
                self.assertEqual({"completed"}, {row["Status"] for row in rows})
                # Our arm's champion decision reaches the campaign's rows; the other arms carry none.
                self.assertEqual([champion] * 6, [row.get("Champion") for row in rows if row["Arm"] == "aidotnet"])
                self.assertEqual(set(), {row["Arm"] for row in rows if row["Arm"] != "aidotnet" and "Champion" in row})
                report = cr.analyze(registration, rows)
                verdicts[label] = [endpoint["Superior"] for endpoint in report["Endpoints"]]

        self.assertEqual([True, True], verdicts["ours-better"])
        # The control. Without it, "superior" could be hardcoded to our arm and this would still pass.
        self.assertEqual([False, False], verdicts["ours-worse"])

    def test_an_unknown_task_or_no_searcher_is_refused_at_construction(self):
        with self.assertRaises(KeyError):
            pilot_cells.executor("no_such_task", {"aidotnet": lambda *a: None}, holdout_seed=918273)
        with self.assertRaises(ValueError):
            pilot_cells.executor(TASK, {}, holdout_seed=918273)


if __name__ == "__main__":
    unittest.main()
