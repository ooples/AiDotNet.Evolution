import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from design import digest
from program_report import TRACKS
from program_study import execute, hashes, render, resource_summary, sample_advice, summarize, trajectory, validate_registration


def plan():
    return dict(schema="evolution-program-estimation-registration-v1", artifacts={}, tasks={"task": {}},
                scales_seconds={"task": 1}, alpha=0.05, planning_power=0.8, planning_utility_effect=0.05,
                confirmation_search_runs_per_task_method=2, model_calls_maximum=0, timing_samples=3,
                model="scripted", image="image", upstream="upstream", openevolve="upstream", dll="dll", codex=None,
                schedule=[dict(id=f"{phase}-{seed}", phase=phase, seed=seed, tasks=["task"],
                               search_instance_seed=seed * 10 + 1, diagnostic_instance_seed=seed * 10 + 2, audit_instance_seed=seed * 10 + 3)
                          for phase, seeds in (("calibration", [101, 103]), ("confirmation", [1009, 1013])) for seed in seeds])


def blocks(registration):
    return [dict(id=b["id"], phase=b["phase"], seed=b["seed"], rows=[dict(task="task", mode=mode, method=method,
                 status="validated", deployed_seconds=1 if method == "aidotnet" else 2,
                 model_tokens=10, search_evaluator_seconds=2) for mode, method in TRACKS], progress=[])
            for b in registration["schedule"]]


class StudyTests(unittest.TestCase):
    def test_repeated_search_seeds_and_phase_overlap_rejected(self):
        registration = plan()
        registration["schedule"][-1]["seed"] = 101
        with self.assertRaisesRegex(ValueError, "Repeated"):
            validate_registration(registration)

    def test_confirmation_count_cannot_be_extended(self):
        registration = plan()
        registration["confirmation_search_runs_per_task_method"] = 30
        with self.assertRaisesRegex(ValueError, "fixed model-call/run budget"):
            validate_registration(registration)
    def test_nested_pairs_count_search_runs_not_tasks_or_timings(self):
        registration = plan()
        result = summarize(registration, blocks(registration), "confirmation")
        self.assertEqual(12, result["scheduled_rows"])
        self.assertEqual(2, result["independent_search_runs_per_task_method"])
        for comparison in result["comparisons"]:
            self.assertAlmostEqual(1 / 6, comparison["paired_effect"])
            self.assertEqual([-1, 1], comparison["simultaneous_interval"])
            self.assertEqual(0, comparison["tasks"][0]["variance"])
        self.assertEqual(120, result["known_model_tokens"])

    def test_missing_block_remains_in_denominator_and_prevents_interval(self):
        registration = plan()
        result = summarize(registration, blocks(registration)[:-1], "confirmation")
        self.assertEqual(12, result["scheduled_rows"])
        self.assertEqual(6, result["failed_missing_or_fallback"])
        self.assertEqual(6, result["unknown_cost_rows"])
        self.assertTrue(all(c["simultaneous_interval"] is None for c in result["comparisons"]))

    def test_fallback_is_not_removed_from_paired_effect(self):
        registration = plan()
        observed = blocks(registration)
        observed[-1]["rows"][0].update(status="fallback", deployed_seconds=4)
        result = summarize(registration, observed, "confirmation")
        self.assertEqual(1, result["failed_missing_or_fallback"])
        self.assertTrue(any(p["failed_or_fallback"] for p in result["pairs"]))

    def test_power_recommendation_is_not_truncated_to_confirmation_budget(self):
        registration = plan()
        advice = sample_advice(registration, summarize(registration, blocks(registration), "calibration"))
        self.assertGreater(advice["conservative_powered_runs"], 2)
        self.assertEqual(2, advice["fixed_confirmation_runs"])
        self.assertFalse(advice["powered_design_feasible"])

    def test_duplicate_block_is_rejected(self):
        registration = plan()
        observed = blocks(registration)
        with self.assertRaisesRegex(ValueError, "Duplicate study block"):
            summarize(registration, observed + [observed[0]], "confirmation")

    def test_one_use_registration_and_confirmation_precedes_dispatch(self):
        registration = plan()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "registration.json").write_text(json.dumps(registration))
            calls = []
            original_read = __import__("program_study").read
            def fake_read(path):
                return {"runs": []} if Path(path).name == "comparison.json" else original_read(path)
            def run(output, *args, **kwargs):
                calls.append(kwargs["seed"])
                if kwargs["seed"] >= 1000:
                    self.assertTrue((root / "confirmation-registration.json").exists())
                    self.assertTrue((root / "sample-advice.json").exists())
                return dict(model_calls=0, evaluator_attempts=0, unknown_evaluator_attempts=0, status="completed")
            rows = blocks(registration)[0]["rows"]
            with patch("program_study.read", side_effect=fake_read), patch("program_study.run_pilot", side_effect=run), \
                    patch("program_study.run_audit"), patch("program_study.pilot_report", return_value={"rows": rows}):
                result = execute(root, digest(registration))
                self.assertEqual("completed", result["status"])
                self.assertEqual([101, 103, 1009, 1013], calls)
                with self.assertRaises(FileExistsError):
                    execute(root, digest(registration))
                self.assertEqual(4, len(calls))

    def test_artifact_change_refuses_before_any_dispatch(self):
        registration = plan()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source"
            source.write_text("initial")
            registration["artifacts"] = hashes([source])
            (root / "registration.json").write_text(json.dumps(registration))
            source.write_text("changed")
            with patch("program_study.run_pilot") as dispatch:
                with self.assertRaisesRegex(ValueError, "implementation changed"):
                    execute(root, digest(registration))
                dispatch.assert_not_called()

    def test_dispatch_failure_retains_every_unscheduled_row(self):
        registration = plan()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "registration.json").write_text(json.dumps(registration))
            with patch("program_study.run_pilot", side_effect=RuntimeError("failed")) as dispatch:
                result = execute(root, digest(registration))
                self.assertEqual(1, dispatch.call_count)
                self.assertEqual("failed", result["status"])
                self.assertEqual(12, result["confirmation"]["scheduled_rows"])
                self.assertEqual(12, result["confirmation"]["failed_missing_or_fallback"])

    def test_partial_model_attempt_is_not_lost_on_block_failure(self):
        registration = plan()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            attempt = root / "calibration-101" / "model" / "0"
            attempt.mkdir(parents=True)
            result = resource_summary(root, registration)
            self.assertEqual(1, result["model_attempts"])
            self.assertEqual(1, result["unknown_model_attempts"])


class TrajectoryTests(unittest.TestCase):
    def fixture(self):
        counters = dict(attempted={"model": 1, "evaluate": 1}, unknown={"model": 0, "evaluate": 0},
                        model_tokens=10, evaluation_seconds=2)
        rows = [dict(sequence=1, operation="evaluate", status="completed", result={"status": "valid", "quality": 1},
                     started_elapsed_seconds=0, finished_elapsed_seconds=2, model_tokens_after=0, evaluation_seconds_after=2),
                dict(sequence=2, operation="model", status="completed", result="code",
                     started_elapsed_seconds=2, finished_elapsed_seconds=3, model_tokens_after=10, evaluation_seconds_after=2)]
        return dict(independent_counters=counters, receipts=rows)

    def test_complete_curve_has_independent_work_and_target(self):
        result = trajectory(self.fixture(), 1)
        self.assertTrue(result["complete"])
        self.assertEqual(10, result["points"][-1]["model_tokens"])
        self.assertEqual("hit", result["cost_to_target"]["status"])

    def test_dropped_trace_keeps_complete_counters_not_fabricated_points(self):
        raw = self.fixture()
        raw["receipts"].pop()
        result = trajectory(raw, 1)
        self.assertFalse(result["complete"])
        self.assertEqual(10, result["counters"]["model_tokens"])
        self.assertIsNone(result["points"])

    def test_counter_mismatch_cannot_form_a_curve(self):
        raw = self.fixture()
        raw["independent_counters"]["model_tokens"] = 999
        self.assertEqual("counter-mismatch", trajectory(raw, 1)["reason"])

    def test_invalid_timestamps_cannot_form_a_curve(self):
        raw = self.fixture()
        raw["receipts"][-1]["finished_elapsed_seconds"] = 0
        self.assertFalse(trajectory(raw, 1)["complete"])
