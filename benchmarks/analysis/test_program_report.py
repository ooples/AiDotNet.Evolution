import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from design import digest
from program_report import TRACKS, markdown, report


class ProgramReportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.pilot, self.audit = self.root / "pilot", self.root / "audit"
        self.pilot.mkdir()
        self.audit.mkdir()
        self.plan = {"schema": "evolution-program-pilot-v1", "claim": "none", "search_seeds": [37],
                     "tasks": {"task": {"source_sha256": "original"}}}
        receipt = dict(status="valid", unknown_work=False, duration_seconds=2, samples=[1, 2, 3],
                       phase="confirmation", input_sha256="input", oracle_sha256="oracle", candidate_hash="original")
        pairs, checks = [], []
        for mode, method in TRACKS:
            selected = {**copy.deepcopy(receipt), "candidate_hash": method, "samples": [0.5, 1, 1.5], "duration_seconds": 1}
            pairs.append(dict(mode=mode, method=method, original=copy.deepcopy(receipt), selected=selected,
                              search_status="completed", fallback=False, model_tokens=10, search_evaluation_seconds=3,
                              speedup=999))  # Derived values must be recomputed, never trusted.
            checks.append(dict(task="task", mode=mode, method=method, valid=True, selected_hash=method))
        self.campaign = dict(status="completed", claim="none", planned_tasks=["task"], tasks=[dict(task="task", pairs=pairs)])
        self.audited = dict(status="passed", runs=checks)

    def write(self):
        self.campaign["plan_sha256"] = digest(self.plan)
        raw = json.dumps(self.campaign).encode()
        audit_plan = dict(schema="evolution-post-selection-audit-v1", pilot_report_sha256=hashlib.sha256(raw).hexdigest())
        self.audited["plan_sha256"] = digest(audit_plan)
        for path, value in ((self.pilot / "plan.json", self.plan), (self.pilot / "report.json", self.campaign),
                            (self.audit / "plan.json", audit_plan), (self.audit / "report.json", self.audited)):
            path.write_text(json.dumps(value), encoding="utf-8")
        return report(self.pilot, self.audit)

    def test_timing_repeats_never_become_independent_search_runs(self):
        result = self.write()
        self.assertEqual(1, result["independent_search_runs_per_task_method"])
        self.assertEqual(6, len(result["rows"]))
        self.assertEqual(2, result["rows"][0]["speedup"])
        self.assertEqual(60, result["known_model_tokens"])
        self.assertIsNone(result["sample_size_plan"])
        self.assertFalse(result["superiority_established"])
        for comparison in result["comparisons"]:
            self.assertIsNone(comparison["confidence_interval"])
            self.assertIsNone(comparison["paired_search_variance"])
        self.assertIn("confidence intervals and sample-size planning withheld", markdown(result))
        self.assertIn("Descriptive comparisons, not demonstrated superiority", markdown(result))

    def test_audit_failure_forces_original_fallback_without_erasing_cost(self):
        self.audited["runs"][0]["valid"] = False
        self.audited["status"] = "failed"
        row = self.write()["rows"][0]
        self.assertEqual("fallback", row["status"])
        self.assertEqual(1, row["speedup"])
        self.assertEqual(10, row["model_tokens"])

    def test_failed_search_remains_in_comparison_as_fallback(self):
        self.campaign["tasks"][0]["pairs"][0]["search_status"] = "failed"
        result = self.write()
        self.assertTrue(result["comparisons"][0]["task_pairs"][0]["fallback_in_pair"])
        self.assertEqual(0.5, result["comparisons"][0]["descriptive_geometric_runtime_ratio"])

    def test_unchanged_source_cannot_gain_from_timing_noise(self):
        self.campaign["tasks"][0]["pairs"][0]["selected"]["candidate_hash"] = "original"
        self.audited["runs"][0]["selected_hash"] = "original"
        row = self.write()["rows"][0]
        self.assertEqual("fallback", row["status"])
        self.assertEqual(1, row["speedup"])

    def test_missing_track_is_retained_and_blocks_paired_summary(self):
        self.campaign["tasks"][0]["pairs"].pop(0)
        result = self.write()
        self.assertEqual(6, len(result["rows"]))
        self.assertEqual("missing", result["rows"][0]["status"])
        self.assertIsNone(result["comparisons"][0]["descriptive_geometric_runtime_ratio"])
        self.assertEqual(1, result["unknown_token_rows"])

    def test_missing_audit_is_unknown_not_validity(self):
        self.audited["runs"].pop(0)
        self.assertEqual("unknown", self.write()["rows"][0]["status"])

    def test_duplicate_track_refused(self):
        self.campaign["tasks"][0]["pairs"].append(copy.deepcopy(self.campaign["tasks"][0]["pairs"][0]))
        with self.assertRaisesRegex(ValueError, "Duplicate scheduled"):
            self.write()

    def test_changed_audited_source_refused(self):
        self.audited["runs"][0]["selected_hash"] = "another"
        with self.assertRaisesRegex(ValueError, "selection identity"):
            self.write()

    def test_fabricated_median_refused(self):
        self.campaign["tasks"][0]["pairs"][0]["selected"]["duration_seconds"] = 0.001
        with self.assertRaisesRegex(ValueError, "Median"):
            self.write()

    def test_mismatched_inputs_refused(self):
        self.campaign["tasks"][0]["pairs"][0]["selected"]["input_sha256"] = "different"
        with self.assertRaisesRegex(ValueError, "Diagnostic inputs"):
            self.write()

    def test_unknown_cost_is_not_a_zero_total(self):
        self.campaign["tasks"][0]["pairs"][0]["model_tokens"] = None
        result = self.write()
        self.assertEqual(50, result["known_model_tokens"])
        self.assertEqual(1, result["unknown_token_rows"])

    def test_changed_pilot_binding_refused(self):
        self.write()
        self.campaign["status"] = "failed"
        (self.pilot / "report.json").write_text(json.dumps(self.campaign))
        with self.assertRaisesRegex(ValueError, "another pilot"):
            report(self.pilot, self.audit)

    def test_unplanned_task_refused(self):
        self.campaign["tasks"].append(dict(task="extra", pairs=[]))
        with self.assertRaisesRegex(ValueError, "Unplanned task"):
            self.write()
