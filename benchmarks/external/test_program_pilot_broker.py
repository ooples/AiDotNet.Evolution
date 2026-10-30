"""The broker callbacks: the shapes the broker enforces, and invalid-as-a-status.

The broker closes admission on a malformed reply, which would lose the whole cell rather than one
candidate - so the wrapper's job is to turn a provider's or a candidate's bad behaviour into
something the broker can accept, and to refuse only what it genuinely cannot.
"""
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

import pilot_broker  # noqa: E402
import pilot_task_evaluator  # noqa: E402
from program_broker import COST_METRICS, ProgramBroker  # noqa: E402
import symbolic_regression_family as family  # noqa: E402

TASK = "rational_gain"
SEED = family.DEV_SEEDS[0]
TRUTH = "def predict(x):\n    return (3.0*x + 1.0)/(x*x + 2.0)\n"
CONSTANT = "def predict(x):\n    return 0.0\n"


class ModelCallbackTests(unittest.TestCase):
    def test_a_well_behaved_provider_becomes_the_brokers_shape(self):
        # cost_metric is part of that shape, not an extra: the broker refuses a reply without one
        # (program_broker.py:299-302), so a reply that omits it closes admission.
        generate = pilot_broker.model_callback(lambda system, messages: ("answer", 12))
        self.assertEqual({"text": "answer", "cost_units": 12,
                          "cost_metric": "reported_input_plus_cache_plus_output_tokens"},
                         generate("sys", []))

    def test_the_declared_cost_metric_is_reported_and_an_undeclared_one_is_refused(self):
        generate = pilot_broker.model_callback(lambda system, messages: ("answer", 12),
                                               cost_metric="reported_input_plus_output_tokens")
        self.assertEqual("reported_input_plus_output_tokens", generate("sys", [])["cost_metric"])
        with self.assertRaises(ValueError):
            pilot_broker.model_callback(lambda system, messages: ("answer", 1), cost_metric="tokens")

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

    def test_a_provider_already_in_the_brokers_shape_is_passed_through_intact(self):
        """The real transport answers in that shape, and two of its fields are only there.

        `claude_transport.generate_metered` reports `throttle_seconds` and `rate_limit_events`,
        which the broker records apart from search work - provider backoff is the provider's queue,
        not the search's. Rebuilding the reply from its text and cost would drop both and swap the
        transport's own metric for this module's default.
        """
        measured = {"text": "answer", "cost_units": 12, "cost_metric": "reported_input_plus_output_tokens",
                    "throttle_seconds": 4.5, "rate_limit_events": 2}
        reply = pilot_broker.model_callback(lambda system, messages, model=None: dict(measured))("sys", [])
        self.assertEqual(measured, reply)

    def test_a_mapping_reply_is_held_to_the_same_rules(self):
        for measured, expected in (
            ({"text": "ok", "cost_units": 1, "cost_metric": "tokens"}, ValueError),       # undeclared metric
            ({"text": "ok", "cost_units": 1}, ValueError),                                 # no metric at all
            ({"text": None, "cost_units": 1, "cost_metric": COST_METRICS[0]}, TypeError),  # no text
            ({"text": "ok", "cost_units": 1.5, "cost_metric": COST_METRICS[0]}, TypeError),
        ):
            with self.subTest(measured=measured):
                with self.assertRaises(expected):
                    pilot_broker.model_callback(lambda system, messages, model=None: dict(measured))("sys", [])

    def test_a_model_name_is_passed_through_when_the_broker_supplies_one(self):
        seen = {}

        def provider(system, messages, model=None):
            seen["model"] = model
            return "answer", 1

        pilot_broker.model_callback(provider)("sys", [], model="sonnet")
        self.assertEqual("sonnet", seen["model"])


class EvaluateCallbackTests(unittest.TestCase):
    def test_every_valid_reply_carries_the_qualitys_standard_error(self):
        # E-4: the delta method through quality = 1 / (1 + score), from the two splits' own errors.
        evaluate = pilot_broker.evaluate_callback(TASK, SEED)
        rows = family.selection(TASK, SEED)
        predict = lambda x: 0.0  # noqa: E731 - CONSTANT, as a function
        train, train_error = family.nmse_and_error(predict, rows["train"])
        validation, validation_error = family.nmse_and_error(predict, rows["validation"])
        score = (train + validation) / 2.0
        expected = ((train_error ** 2 + validation_error ** 2) ** 0.5 / 2.0) / (1.0 + score) ** 2
        reply = evaluate(CONSTANT)
        self.assertAlmostEqual(score, reply["selection_nmse"], places=12)
        self.assertAlmostEqual(expected, reply["quality_standard_error"], places=12)
        self.assertGreater(reply["quality_standard_error"], 0.0)

    def test_the_selection_rows_are_the_ones_asked_for(self):
        # More rows is the other half of E-4's fix: the reply must score on exactly them.
        rows = family.selection(TASK, SEED, train=600, validation=200)
        expected = (family.nmse(lambda x: 0.0, rows["train"]) + family.nmse(lambda x: 0.0, rows["validation"])) / 2.0
        reply = pilot_broker.evaluate_callback(TASK, SEED, train=600, validation=200)(CONSTANT)
        self.assertAlmostEqual(expected, reply["selection_nmse"], places=12)
        self.assertNotAlmostEqual(pilot_broker.evaluate_callback(TASK, SEED)(CONSTANT)["selection_nmse"],
                                  reply["selection_nmse"], places=6)

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
                self.assertNotIn("artifacts", result)

    def test_a_valid_reply_shows_the_inputs_and_training_rows_with_the_candidates_residuals(self):
        # E-4: the model saw a scalar score alone; the rows are training rows only, and the candidate's own predictions.
        rows = family.selection(TASK, SEED)
        train = {round(xs[0], 4) for xs, _ in rows["train"]}
        validation = {round(xs[0], 4) for xs, _ in rows["validation"]}
        text = pilot_broker.evaluate_callback(TASK, SEED)(TRUTH.replace("+ 1.0)/", "+ 2.0)/"))["artifacts"]["training_rows"]
        lines = text.splitlines()
        self.assertEqual("inputs: x = xs[0] in [-3, 3]", lines[0])
        self.assertEqual("x target prediction residual", lines[2])
        self.assertEqual(pilot_broker.FEEDBACK_ROWS, len(lines) - 3)
        for line in lines[3:]:
            x, target, prediction, residual = (float(value) for value in line.split())
            self.assertIn(x, train - validation)
            self.assertAlmostEqual((3.0 * x + 1.0) / (x * x + 2.0), target, places=3)
            self.assertAlmostEqual((3.0 * x + 2.0) / (x * x + 2.0), prediction, places=3)
            self.assertAlmostEqual(1.0 / (x * x + 2.0), residual, places=3)

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


class RealBrokerTests(unittest.TestCase):
    """The callbacks against the real broker, which is the only thing that enforces their shape.

    The unit tests above check the shapes the module declares. They passed while both callbacks were
    missing fields the broker requires - `candidate_hash`, `unknown_work`, `work_units`,
    `cost_metric` - so the first candidate of the first cell would have raised "Evaluator did not
    reconcile the dispatched program", closed admission, and lost the cell. Only a live broker
    catches that, so one runs here.
    """

    def dispatch(self, broker, code):
        return broker.dispatch("evaluate", {"code": code})

    def test_an_evaluation_and_a_model_call_both_reconcile_against_a_live_broker(self):
        generate = pilot_broker.model_callback(lambda system, messages, model=None: ("answer", 7))
        with ProgramBroker(generate, pilot_broker.evaluate_callback(TASK, SEED), model_calls=2,
                           evaluations=3, seconds=60, initial=CONSTANT, model_tokens=1000) as broker:
            seed = self.dispatch(broker, CONSTANT)  # the broker requires the initial program first
            self.assertEqual("valid", seed["status"])
            self.assertEqual("answer", broker.dispatch("model", {"system": "s", "messages": []}))
            truth = self.dispatch(broker, TRUTH)
            self.assertEqual(1.0, truth["quality"])
        self.assertFalse(broker.closed, "a reply the broker cannot reconcile closes it and loses the cell")
        self.assertEqual([], broker.refusals)
        self.assertEqual(7, broker.model_tokens)
        self.assertEqual(0, broker.unknown_attempts["evaluate"], "every evaluation accounted for its work")

    def test_an_invalid_candidate_is_one_candidate_lost_not_the_cell(self):
        generate = pilot_broker.model_callback(lambda system, messages, model=None: ("answer", 1))
        with ProgramBroker(generate, pilot_broker.evaluate_callback(TASK, SEED), model_calls=1,
                           evaluations=3, seconds=60, initial=CONSTANT, model_tokens=1000) as broker:
            self.dispatch(broker, CONSTANT)
            self.assertEqual("invalid", self.dispatch(broker, "def predict(x)\n")["status"])
            self.assertEqual("answer", broker.dispatch("model", {"system": "s", "messages": []}))
        self.assertFalse(broker.closed)


class SessionTests(unittest.TestCase):
    """One broker per cell, bound to that cell's own seed."""

    def session(self, **overrides):
        budget = dict(model_calls=2, evaluations=3, seconds=60, initial=CONSTANT, model_tokens=1000)
        budget.update(overrides)
        return pilot_broker.session(lambda system, messages, model=None: ("answer", 1), TASK, **budget)

    def test_each_cell_is_scored_on_its_own_seed(self):
        # A campaign-wide broker would have bound one seed's rows and scored every later cell on
        # them; this is the measurement that would have silently gone wrong.
        open_for, scores = self.session(), []
        for seed in (family.DEV_SEEDS[0], family.DEV_SEEDS[1]):
            with open_for({"Family": TASK, "Seed": seed}) as broker:
                scores.append(broker.dispatch("evaluate", {"code": CONSTANT})["selection_nmse"])
        self.assertNotEqual(scores[0], scores[1], "two seeds must not be the same rows")

    def test_a_cell_cannot_be_given_a_budget_the_broker_refuses(self):
        # The reason a broker is per cell: it will not take a campaign-sized budget at all.
        with self.assertRaises(ValueError):
            with self.session(model_calls=180)({"Family": TASK, "Seed": SEED}):
                pass

    def test_the_cells_broker_is_closed_when_the_cell_ends(self):
        with self.session()({"Family": TASK, "Seed": SEED}) as broker:
            endpoint = broker.endpoint
        self.assertTrue(broker.shut, "a cell's broker must not outlive the cell: " + endpoint)



class TaskEvaluatorTests(unittest.TestCase):
    def test_the_standard_error_reaches_the_search_with_the_score(self):
        # Our CLI reads it from the evaluator's metrics; without it a champion rule refuses the run.
        replies = [{"status": "valid", "quality": 0.5, "selection_nmse": 1.0, "quality_standard_error": 0.01},
                   {"status": "valid", "quality": 0.5, "selection_nmse": 1.0}]
        genuine = pilot_task_evaluator.request
        try:
            pilot_task_evaluator.request = lambda endpoint, capability, operation, payload: replies.pop(0)
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "candidate.py"
                path.write_text(CONSTANT, encoding="utf-8")
                evaluate = pilot_task_evaluator.bind("http://127.0.0.1:1", "capability")
                self.assertEqual(0.01, evaluate(str(path)).metrics["quality_standard_error"])
                self.assertNotIn("quality_standard_error", evaluate(str(path)).metrics)
        finally:
            pilot_task_evaluator.request = genuine

    def test_the_brokers_artifacts_reach_the_search_as_artifacts(self):
        # Our shim reads `.artifacts` from the result, as OpenEvolve does from its EvaluationResult.
        genuine = pilot_task_evaluator.request
        try:
            pilot_task_evaluator.request = lambda endpoint, capability, operation, payload: {
                "status": "valid", "quality": 0.5, "selection_nmse": 1.0, "artifacts": {"training_rows": "rows"}}
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "candidate.py"
                path.write_text(CONSTANT, encoding="utf-8")
                result = pilot_task_evaluator.bind("http://127.0.0.1:1", "capability")(str(path))
                self.assertEqual({"training_rows": "rows"}, result.artifacts)
                self.assertEqual(0.5, result.metrics["combined_score"])
        finally:
            pilot_task_evaluator.request = genuine


if __name__ == "__main__":
    unittest.main()
