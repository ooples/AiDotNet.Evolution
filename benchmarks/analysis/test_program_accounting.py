import copy
import json
import math
import unittest

import test_program_report as fixtures
from program_study import sample_advice, summarize, trajectory, validate_registration
from test_program_study import blocks, plan


class IndependentReportingTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.ProgramReportTests(methodName="runTest")
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.raw = dict(mode="controlled", method="aidotnet", selected_hash="aidotnet", status="completed", receipts=[],
                        independent_counters=dict(attempted=dict(model=2, evaluate=3), unknown=dict(model=0, evaluate=0),
                                                  model_tokens=29, evaluation_seconds=9.0))
        self.comparison = dict(schema="aidotnet-program-comparison-v1", seed=37, initial_program_hash="original", runs=[self.raw])

    def write(self):
        task = self.fixture.pilot / "task"
        task.mkdir(exist_ok=True)
        (task / "comparison.json").write_text(json.dumps(self.comparison))
        return self.fixture.write()

    def test_dropped_trace_suppresses_curve_but_preserves_independent_costs(self):
        result = self.write()
        self.assertFalse(trajectory(self.raw, 1)["complete"])
        self.assertEqual(29, result["rows"][0]["model_tokens"])
        self.assertEqual(9, result["rows"][0]["search_evaluator_seconds"])
        self.assertEqual(79, result["known_model_tokens"])
        self.assertEqual(24, result["known_search_evaluator_seconds"])
        self.assertEqual("independent-counters", result["rows"][0]["cost_provenance"])
        self.assertEqual(1, result["independently_accounted_rows"])

    def test_missing_outcome_does_not_erase_known_independent_costs(self):
        self.fixture.campaign["tasks"][0]["pairs"].pop(0)
        row = self.write()["rows"][0]
        self.assertEqual("missing", row["status"])
        self.assertEqual(29, row["model_tokens"])
        self.assertEqual(9, row["search_evaluator_seconds"])

    def test_unknown_attempts_keep_subtotals_but_prevent_complete_cost_claim(self):
        self.raw["independent_counters"]["unknown"] = dict(model=1, evaluate=1)
        result = self.write()
        self.assertEqual(79, result["known_model_tokens"])
        self.assertEqual(1, result["unknown_token_rows"])
        self.assertEqual(1, result["unknown_evaluator_rows"])
        self.assertEqual(1, result["rows"][0]["unknown_model_attempts"])

    def test_impossible_independent_counters_are_rejected(self):
        for change in (dict(unknown=dict(model=3, evaluate=0)), dict(model_tokens=-1), dict(evaluation_seconds=float("inf"))):
            with self.subTest(change=change):
                original = copy.deepcopy(self.raw["independent_counters"])
                self.raw["independent_counters"].update(change)
                with self.assertRaises(ValueError):
                    self.write()
                self.raw["independent_counters"] = original

    def test_wrong_raw_selection_or_search_seed_is_rejected(self):
        self.raw["selected_hash"] = "wrong"
        with self.assertRaisesRegex(ValueError, "Raw selection"):
            self.write()
        self.raw["selected_hash"] = "aidotnet"
        self.comparison["seed"] = 999
        with self.assertRaisesRegex(ValueError, "Raw comparison"):
            self.write()

    def test_legacy_summary_never_claims_independent_counter_provenance(self):
        self.raw.pop("independent_counters")
        result = self.write()
        self.assertEqual(10, result["rows"][0]["model_tokens"])
        self.assertEqual("legacy-summary-only", result["rows"][0]["cost_provenance"])
        self.assertEqual(0, result["independently_accounted_rows"])


class ProgramPlanningTests(unittest.TestCase):
    def test_registry_cannot_claim_target_or_iterations_the_driver_does_not_use(self):
        for change in (dict(target_utility=0.9), dict(iterations=30), dict(tuning_trials=1)):
            with self.subTest(change=change):
                registration = {**plan(), **change}
                with self.assertRaisesRegex(ValueError, "fixed driver"):
                    validate_registration(registration)

    def test_sufficient_power_bound_matches_two_sided_reported_interval(self):
        registration = plan()
        registration["tasks"] = {name: {} for name in ("a", "b", "c")}
        calibration = dict(comparisons=[dict(tasks=[dict(variance=0.001)] * 3)] * 4)
        advice = sample_advice(registration, calibration)
        self.assertEqual(13648, advice["conservative_powered_runs"])
        self.assertEqual("program-sample-advice-v2", advice["schema"])
        n = advice["conservative_powered_runs"]
        def miss_bound(count):
            width = math.sqrt(2 * math.log(2 * 3 * 4 / 0.05) / count)
            return 3 * math.exp(-count * (0.05 - width)**2 / 2) if width < 0.05 else 1
        self.assertLessEqual(miss_bound(n), 0.2)
        self.assertGreater(miss_bound(n - 1), 0.2)
        self.assertGreater(miss_bound(12715), 0.2)
        self.assertEqual(2, advice["fixed_confirmation_runs"])
        self.assertFalse(advice["powered_design_feasible"])

    def test_study_cost_summary_counts_unknown_work_even_with_known_subtotals(self):
        registration = plan()
        observed = blocks(registration)
        observed[-1]["rows"][0]["unknown_model_attempts"] = 1
        result = summarize(registration, observed, "confirmation")
        self.assertEqual(120, result["known_model_tokens"])
        self.assertEqual(1, result["unknown_cost_rows"])
