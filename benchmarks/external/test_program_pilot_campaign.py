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
