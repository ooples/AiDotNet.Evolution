"""The driver's refusals, and that a campaign cannot start without someone reading its budget.

The statistics belong to campaign_registration; what is asserted here is the order and the guards:
seeds drawn by the registration, a projection that must be confirmed, and a report that shows losses.
"""
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

import pilot_campaign as campaign  # noqa: E402

TASK = "rational_gain"
TRUTH = "def predict(x):\n    return (3.0*x + 1.0)/(x*x + 2.0)\n"
CONSTANT = "def predict(x):\n    return 0.0\n"


def searcher(body):
    def search(cell, directory, resume):
        directory = Path(directory)
        directory.mkdir(parents=True, exist_ok=True)
        path = directory / "candidate.py"
        path.write_text(body, encoding="utf-8")
        return {"program": str(path), "model_calls": 2, "model_tokens": 100}
    return search


def arms(ours, theirs):
    return {"aidotnet": searcher(ours), "openevolve-recommended": searcher(theirs),
            "openevolve-matched": searcher(theirs)}


def declared():
    return campaign.spec(TASK, seed_count=6, model="sonnet", model_calls=10, model_tokens=1000,
                         wall_seconds=60)


def frozen_for(seeds, direction="minimize"):
    """A registration-shaped dict: `ranked_losses` reads only the spec, the seeds and the digest."""
    return {"RegistrationSha256": "r" * 64, "Seeds": list(seeds),
            "Spec": {"Families": [{"Family": TASK, "Configs": ["recommended", "matched"],
                                   "Endpoint": {"Metric": "nmse", "Direction": direction}}]}}


def cell(arm, seed, value, status="completed"):
    return {"Family": TASK, "Arm": arm, "Seed": seed, "Status": status, "Value": value}


class RankedLossTests(unittest.TestCase):
    """V1-07's third criterion: the report says whether we win, this says where we lose."""

    def test_the_lost_cells_are_ranked_worst_margin_first(self):
        # A mean difference cannot carry this: an arm can win most cells and still be beaten badly
        # in one, and it is the beaten one that sets the next priority.
        seeds = [11, 22, 33]
        rows = [cell("aidotnet", 11, 0.90), cell("aidotnet", 22, 0.20), cell("aidotnet", 33, 0.40),
                cell("openevolve-recommended", 11, 0.10), cell("openevolve-recommended", 22, 0.50),
                cell("openevolve-recommended", 33, 0.25),
                cell("openevolve-matched", 11, 0.95), cell("openevolve-matched", 22, 0.95),
                cell("openevolve-matched", 33, 0.95)]
        ranking = campaign.ranked_losses(frozen_for(seeds), rows)
        first = ranking["Endpoints"][0]
        self.assertEqual("recommended", first["Config"], "the endpoint losing most cells leads")
        self.assertEqual([11, 33], [loss["Seed"] for loss in first["Losses"]], "worst margin first")
        self.assertAlmostEqual(0.80, first["WorstMargin"], places=10)
        self.assertEqual(0, ranking["Endpoints"][1]["LostCells"], "matched lost nothing")
        self.assertIn("lost 2/3 cells", chr(10).join(campaign.loss_lines(ranking)))

    def test_a_maximised_endpoint_is_not_read_as_a_minimised_one(self):
        # The direction is the endpoint's, not the reader's: inverting it would publish every win as
        # a loss and rank the strongest cells first.
        rows = [cell("aidotnet", 11, 0.90),
                cell("openevolve-recommended", 11, 0.10), cell("openevolve-matched", 11, 0.10)]
        minimised = campaign.ranked_losses(frozen_for([11], "minimize"), rows)["Endpoints"][0]
        maximised = campaign.ranked_losses(frozen_for([11], "maximize"), rows)["Endpoints"][0]
        self.assertEqual(1, minimised["LostCells"], "0.90 is worse than 0.10 when lower is better")
        self.assertEqual(0, maximised["LostCells"], "and better when higher is")
        self.assertIn("no cell was lost on any endpoint",
                      chr(10).join(campaign.loss_lines(campaign.ranked_losses(frozen_for([11], "maximize"), rows))))

    def test_an_unfinished_pair_is_counted_as_incomparable_not_as_a_win(self):
        # Dropping it would shorten the denominator the ranking is read against, so a campaign with
        # three failed cells would look like a clean sweep of the ones that ran.
        rows = [cell("aidotnet", 11, None, "failed"), cell("aidotnet", 22, 0.40),
                cell("openevolve-recommended", 11, 0.10), cell("openevolve-recommended", 22, 0.10),
                cell("openevolve-matched", 11, 0.10), cell("openevolve-matched", 22, 0.10)]
        ranking = campaign.ranked_losses(frozen_for([11, 22]), rows)
        # Our arm failed seed 11, so *both* configs lose that pair as a comparison: one `aidotnet`
        # cell per seed is what a campaign records, and it is shared by every config.
        for row in ranking["Endpoints"]:
            self.assertEqual(1, row["IncomparablePairs"], row["Config"])
            self.assertEqual(1, row["ComparablePairs"], "one pair of two can be compared")
            self.assertEqual(1, row["LostCells"], "the completed pair, and only it")
        self.assertIn("(1 incomparable)", chr(10).join(campaign.loss_lines(ranking)))

    def test_a_campaign_with_nothing_comparable_does_not_report_a_clean_sweep(self):
        # "No cell was lost" and "no pair could be compared" are different readings; printing the
        # second as the first would let a campaign whose cells all failed read as a clean sweep.
        rows = [cell("aidotnet", 11, None, "failed"),
                cell("openevolve-recommended", 11, 0.10), cell("openevolve-matched", 11, 0.10)]
        ranking = campaign.ranked_losses(frozen_for([11]), rows)
        text = chr(10).join(campaign.loss_lines(ranking))
        self.assertEqual([0, 0], [row["ComparablePairs"] for row in ranking["Endpoints"]])
        self.assertIn("no pair could be compared", text)
        self.assertNotIn("no cell was lost", text)

    def test_the_ranking_is_a_function_of_the_rows_not_of_their_order(self):
        seeds = [11, 22]
        rows = [cell("aidotnet", 11, 0.50), cell("aidotnet", 22, 0.50),
                cell("openevolve-recommended", 11, 0.20), cell("openevolve-recommended", 22, 0.20),
                cell("openevolve-matched", 11, 0.20), cell("openevolve-matched", 22, 0.20)]
        # Equal margins throughout, so only the tie-break can decide the order - and the frozen
        # seeds are reversed so that iterating them is not the same as sorting them. Without the
        # tie-break this test fails rather than passing by coincidence (review caught that).
        forward = campaign.ranked_losses(frozen_for(list(reversed(seeds))), rows)
        backward = campaign.ranked_losses(frozen_for(list(reversed(seeds))), list(reversed(rows)))
        self.assertEqual(forward, backward)
        self.assertEqual([11, 22], [loss["Seed"] for loss in forward["Endpoints"][0]["Losses"]])


class DriverTests(unittest.TestCase):
    def test_the_registered_budget_is_what_stops_a_cell(self):
        """`Budget.ModelCalls` has to be enforcement, not a number in a file.

        The broker is what can enforce it, so `cell_broker` builds each cell's broker from the
        frozen registration: the call after the last declared one is refused, whatever the arm's own
        stopping rule would have done. The evaluation ceiling is deliberately two above the call
        ceiling - a cell evaluates the seed before it proposes anything - because a tighter cap
        would leave a candidate unscored instead of refusing a call.
        """
        with tempfile.TemporaryDirectory() as directory:
            frozen, _, _ = campaign.freeze(
                campaign.spec(TASK, seed_count=6, model="sonnet", model_calls=2, model_tokens=500,
                              wall_seconds=60), Path(directory) / "registration")
            session = campaign.cell_broker(frozen, lambda s, m, model=None: ("answer", 1), TASK, CONSTANT)
            with session({"Family": TASK, "Seed": frozen["Seeds"][0]}) as broker:
                self.assertEqual({"model": 2, "evaluate": 4}, broker.limits)
                broker.dispatch("evaluate", {"code": CONSTANT})
                for _ in range(2):
                    broker.dispatch("model", {"system": "s", "messages": []})
                with self.assertRaises(ValueError):
                    broker.dispatch("model", {"system": "s", "messages": []})
            self.assertFalse(broker.closed, "spending the declared budget is not a failure")

    def test_the_spec_field_set_is_closed(self):
        # register refuses any extra key, which is how an undeclared knob cannot ride along into a
        # frozen registration. Found by trying to carry `Iterations` in the spec.
        with tempfile.TemporaryDirectory() as directory:
            extra = dict(declared(), Iterations=2)
            with self.assertRaises(ValueError):
                campaign.freeze(extra, Path(directory) / "registration")

    def test_a_campaign_cannot_launch_without_confirming_its_projection(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            frozen, path, projection = campaign.freeze(declared(), root / "registration")
            self.assertEqual(18, projection["Cells"], "3 arms x 6 seeds")
            self.assertEqual(180, projection["MaxModelCalls"], "the ceiling the operator must read")
            with self.assertRaises(ValueError):
                campaign.run(frozen, path, projection, root / "state", TASK, arms(TRUTH, CONSTANT),
                             holdout_seed=918273, confirm="not-the-digest", log=lambda *args: None)

    def test_the_registration_draws_its_own_seeds(self):
        # The operator cannot choose them after seeing anything, so two freezes differ.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first, _, _ = campaign.freeze(declared(), root / "a")
            second, _, _ = campaign.freeze(declared(), root / "b")
            self.assertEqual(6, len(first["Seeds"]))
            self.assertNotEqual(first["Seeds"], second["Seeds"])

    def test_every_cell_runs_and_the_report_shows_both_endpoints(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            frozen, path, projection = campaign.freeze(declared(), root / "registration")
            rows, report = campaign.run(frozen, path, projection, root / "state", TASK,
                                        arms(TRUTH, CONSTANT), holdout_seed=918273,
                                        confirm=projection["ProjectionSha256"], log=lambda *args: None)
        self.assertEqual(18, len(rows))
        self.assertEqual({"completed"}, {row["Status"] for row in rows})
        self.assertEqual(2, len(report["Endpoints"]))
        self.assertEqual(2, len(campaign.report_lines(report)))
        for line in campaign.report_lines(report):
            self.assertIn("failures=", line, "a report that hides failures is not a pilot report")

    def test_a_winning_arm_is_reported_as_winning(self):
        """The positive control, which this suite did not have.

        `test_a_losing_arm_is_reported_as_losing` asserts `[False, False]` and the shape test asserts
        only the shape, so `Superior` hardcoded to `False` - or a bootstrap oriented the wrong way -
        passed both. Then the pilot could only ever say "no", and a campaign that cannot say "yes"
        is not a measurement. Rigged in our favour: ours returns the ground truth, both OpenEvolve
        arms return the constant predictor, over six paired seeds.
        """
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            frozen, path, projection = campaign.freeze(declared(), root / "registration")
            _, report = campaign.run(frozen, path, projection, root / "state", TASK,
                                     arms(TRUTH, CONSTANT), holdout_seed=918273,
                                     confirm=projection["ProjectionSha256"], log=lambda *args: None)
        self.assertEqual([True, True], [endpoint["Superior"] for endpoint in report["Endpoints"]],
                         "a verdict machine that cannot report a win cannot report a result")
        for endpoint in report["Endpoints"]:
            with self.subTest(config=endpoint["Config"]):
                self.assertGreater(endpoint["MeanDifference"], 0.5,
                                   "the truth beats a constant predictor by most of the endpoint's range")
                self.assertLessEqual(endpoint["OneSidedP"], frozen["Spec"]["FamilywiseAlpha"],
                                     "six paired seeds of a large consistent difference must reject")

    def test_a_losing_arm_is_reported_as_losing(self):
        # Rigged the other way, so "superior" cannot be hardcoded to our arm.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            frozen, path, projection = campaign.freeze(declared(), root / "registration")
            _, report = campaign.run(frozen, path, projection, root / "state", TASK,
                                     arms(CONSTANT, TRUTH), holdout_seed=918273,
                                     confirm=projection["ProjectionSha256"], log=lambda *args: None)
        self.assertEqual([False, False], [endpoint["Superior"] for endpoint in report["Endpoints"]])


if __name__ == "__main__":
    unittest.main()
