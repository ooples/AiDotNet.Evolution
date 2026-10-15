"""The broker callbacks: the shapes the broker enforces, and invalid-as-a-status.

The broker closes admission on a malformed reply, which would lose the whole cell rather than one
candidate - so the wrapper's job is to turn a provider's or a candidate's bad behaviour into
something the broker can accept, and to refuse only what it genuinely cannot.
"""
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

import pilot_broker  # noqa: E402
import symbolic_regression_family as family  # noqa: E402

TASK = "rational_gain"
SEED = family.DEV_SEEDS[0]
TRUTH = "def predict(x):\n    return (3.0*x + 1.0)/(x*x + 2.0)\n"
CONSTANT = "def predict(x):\n    return 0.0\n"


class ModelCallbackTests(unittest.TestCase):
    def test_a_well_behaved_provider_becomes_the_brokers_shape(self):
        generate = pilot_broker.model_callback(lambda system, messages: ("answer", 12))
        self.assertEqual({"text": "answer", "cost_units": 12}, generate("sys", []))

    def test_the_shapes_the_broker_would_reject_are_refused_here(self):
        for provider, expected in (
            (lambda s, m: (None, 1), TypeError),          # no text
            (lambda s, m: ("ok", 1.5), TypeError),         # cost_units must be int
            (lambda s, m: ("ok", -1), TypeError),          # and non-negative
            (lambda s, m: ("x" * (64 * 1024 + 1), 1), ValueError),   # over 64 KiB
        ):
            with self.subTest(expected=expected.__name__):
                with self.assertRaises(expected):
                    pilot_broker.model_callback(provider)("sys", [])

    def test_a_model_name_is_passed_through_when_the_broker_supplies_one(self):
        seen = {}

        def provider(system, messages, model=None):
            seen["model"] = model
            return "answer", 1

        pilot_broker.model_callback(provider)("sys", [], model="sonnet")
        self.assertEqual("sonnet", seen["model"])


class EvaluateCallbackTests(unittest.TestCase):
    def test_quality_is_higher_is_better_and_the_truth_tops_it(self):
        evaluate = pilot_broker.evaluate_callback(TASK, SEED)
        truth, constant = evaluate(TRUTH), evaluate(CONSTANT)
        self.assertEqual("valid", truth["status"])
        self.assertEqual(1.0, truth["quality"], "nmse 0 maps to quality 1")
        self.assertGreater(truth["quality"], constant["quality"])

    def test_a_broken_candidate_is_invalid_not_an_exception(self):
        # An exception here closes the broker and loses the cell, not just the candidate.
        evaluate = pilot_broker.evaluate_callback(TASK, SEED)
        for code in ("x = 1\n",                                   # no predict
                     "def predict(x):\n    raise ValueError()\n",  # raises
                     "def predict(x):\n    return float('nan')\n", # unscorable
                     "def predict(x)\n"):                          # will not even parse
            with self.subTest(code=code.splitlines()[0]):
                result = evaluate(code)
                self.assertEqual("invalid", result["status"])
                self.assertNotIn("quality", result, "an invalid candidate must carry no quality")

    def test_scoring_uses_selection_rows_only(self):
        # The endpoint belongs to pilot_cells, from a holdout seed this process never sees. If this
        # callback ever asked for held-out rows, the arm would be scoring its own endpoint.
        asked = []
        genuine = pilot_broker.family.holdout
        try:
            pilot_broker.family.holdout = lambda *args, **kwargs: asked.append("holdout")
            pilot_broker.evaluate_callback(TASK, SEED)(TRUTH)
        finally:
            pilot_broker.family.holdout = genuine
        self.assertEqual([], asked)

    def test_an_unknown_task_is_refused_at_construction(self):
        with self.assertRaises(KeyError):
            pilot_broker.evaluate_callback("no_such_task", SEED)


if __name__ == "__main__":
    unittest.main()
