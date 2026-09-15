import json
from pathlib import Path
import tempfile
import unittest

from design import digest
from program_scorecard import AXES, build, progress_metrics, render
from program_study import summarize
from test_program_study import plan


def curve():
    return dict(complete=True, unknown_work=False, points=[
        dict(operation="evaluate", model_tokens=0, evaluator_seconds=1, elapsed_seconds=2, best_search_utility=0.25),
        dict(operation="model", model_tokens=10, evaluator_seconds=1, elapsed_seconds=4, best_search_utility=0.25),
        dict(operation="evaluate", model_tokens=10, evaluator_seconds=3, elapsed_seconds=6, best_search_utility=0.75),
        dict(operation="model", model_tokens=20, evaluator_seconds=3, elapsed_seconds=8, best_search_utility=0.75)])


class ScorecardTests(unittest.TestCase):
    def test_auc_is_left_step_on_observed_domain_not_future_best(self):
        result = progress_metrics(curve())
        self.assertEqual(10, result["auc"]["model_tokens"]["area"])
        self.assertEqual(0.5, result["auc"]["model_tokens"]["mean_observed_utility"])
        self.assertEqual(0.5, result["auc"]["evaluator_seconds"]["area"])
        self.assertEqual(2.5, result["auc"]["elapsed_seconds"]["area"])
        self.assertEqual(0.25, result["auc"]["evaluation_attempts"]["area"])
        self.assertEqual(dict(model_tokens=10, evaluation_attempts=2, evaluator_seconds=3, elapsed_seconds=6), result["target"]["at"])

    def test_missing_and_unknown_work_never_produce_auc(self):
        for changes in (dict(complete=False, reason="dropped-or-reordered-records"), dict(unknown_work=True)):
            result = progress_metrics({**curve(), **changes})
            self.assertEqual("unavailable", result["status"])
            self.assertIsNone(result["auc"])
            self.assertIsNone(result["target"])

    def test_initial_unknown_quality_and_unspent_budget_are_not_filled(self):
        value = curve()
        value["points"][0]["best_search_utility"] = None
        value["points"][1]["best_search_utility"] = None
        result = progress_metrics(value)
        self.assertEqual(6, result["auc"]["elapsed_seconds"]["start"])
        self.assertEqual(8, result["auc"]["elapsed_seconds"]["end"])
        self.assertIsNone(result["auc"]["evaluation_attempts"]["mean_observed_utility"])
        self.assertEqual(20, result["target"]["observed_end"]["model_tokens"])

    def test_nonhit_is_censored_at_actual_end_on_all_axes(self):
        value = curve()
        for point in value["points"]:
            point["best_search_utility"] = 0.25
        target = progress_metrics(value)["target"]
        self.assertEqual("not-hit", target["status"])
        self.assertIsNone(target["at"])
        self.assertEqual(set(AXES), set(target["observed_end"]))
        self.assertEqual(8, target["observed_end"]["elapsed_seconds"])

    def fixture(self):
        registration = plan()
        registration["source_revision"] = "frozen"
        study = dict(schema="evolution-program-study-v1", status="failed", failure={"error": "fixture"},
                     claim="none", superiority_established=False, registration_sha256=digest(registration), blocks=[], resources={},
                     calibration=summarize(registration, [], "calibration"), confirmation=summarize(registration, [], "confirmation"))
        return registration, study

    def write(self, root, registration, study):
        (root / "registration.json").write_text(json.dumps(registration))
        (root / "study.json").write_text(json.dumps(study))

    def test_failed_study_retains_every_missing_run_and_unknown_metric(self):
        registration, study = self.fixture()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write(root, registration, study)
            result = build(root)
        self.assertEqual(24, len(result["rows"]))
        self.assertTrue(all(r["independent_counters"] is None for r in result["rows"]))
        for summary in result["summary"]:
            self.assertEqual(12, summary["audit_unknown"])
            self.assertEqual(12, summary["search_target_unknown"])
        self.assertIn("peak_memory_bytes", result["unavailable"])
        self.assertEqual("none", result["claim"])
        text, webpage = render(result, study)
        self.assertIn("not a fixed-budget ranking", text)
        self.assertNotIn("<path", webpage)

    def test_changed_registered_estimate_is_rejected(self):
        registration, study = self.fixture()
        study["confirmation"]["comparisons"][0]["paired_effect"] = 0.5
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write(root, registration, study)
            with self.assertRaisesRegex(ValueError, "phase summary changed"):
                build(root)

    def test_html_escapes_labels_and_renders_all_four_axes(self):
        _, study = self.fixture()
        row = dict(block="<script>", task="task", mode="mode", method="method", progress=progress_metrics(curve()))
        value = dict(rows=[row], summary=[], unavailable={})
        _, webpage = render(value, study)
        self.assertNotIn("<script>", webpage)
        self.assertIn("&lt;script&gt;", webpage)
        self.assertEqual(4, webpage.count("<svg"))
