import copy
import json
import hashlib
from pathlib import Path
import tempfile
import subprocess
import sys
import unittest

from analyze import analyze, interval, load_json, markdown, validate_plan


def fixture():
    plan = dict(SchemaVersion=1, Purpose="retrospective-development", SourceRevision="a" * 40,
                Protocol="numeric-development-v3-diagonal-cma", Tasks=[dict(Name="One", Scale=1), dict(Name="Two", Scale=1)],
                Methods=["Candidate", "Baseline"], SeedCount=3, Budget=8, PrimaryMethod="Candidate", Comparators=["Baseline"],
                BootstrapSamples=400, BootstrapSeed=42, ResampleTasks=True, ConfidenceLevel=0.95)
    runs = []
    for task in ("One", "Two"):
        for method in plan["Methods"]:
            for seed in range(3):
                loss = seed + 1.0
                runs.append(dict(Task=task, Method=method, Seed=seed, InitialPopulationHash="b" * 64, Status="completed",
                                 EvaluatorCalls=8, Proposals=8, FinalLoss=loss,
                                 Resources=dict(Spent=dict(cost_units=8, proposal_calls=0), Reserved=dict(cost_units=0), Unknown=0, MaximumViolated=False),
                                 Samples=[dict(EvaluationId=i, CostUnits=1, Attempts=1, BestLoss=loss) for i in range(8)]))
    campaign = dict(SchemaVersion=2, Protocol=plan["Protocol"], Partition="development", SourceRevision=plan["SourceRevision"],
                    Seeds=3, Budget=8, TaskCount=2, Dimensions=8, InitialPopulation=8, Methods=plan["Methods"], Runs=runs)
    return campaign, plan


class AnalysisTests(unittest.TestCase):
    def external_fixture(self):
        campaign, plan = fixture()
        external = "ScipyDifferentialEvolutionMatched8"
        campaign.update(Protocol="numeric-development-v4-external", Budget=16, WorkingTreeSmoke=False, EvaluatorBinarySha256="c" * 64)
        plan.update(Protocol=campaign["Protocol"], Budget=16, Methods=["Candidate", external], Comparators=[external])
        campaign["Methods"] = plan["Methods"]
        for row in campaign["Runs"]:
            if row["Method"] == "Baseline":
                row.update(Method=external, StopReason="converged", IndependentEvaluatorCalls=8, ControllerDispatches=8,
                           OptimizerNfev=8, UnknownWork=False,
                           EvaluatorManifest=dict(AssemblySha256="c" * 64, InitialPopulationHash="b" * 64,
                                                  Task=row["Task"], Seed=row["Seed"], Budget=16))
            else:
                row.update(StopReason="evaluation-cap", EvaluatorCalls=16, Proposals=16)
                row["Resources"]["Spent"] = dict(cost_units=16, proposal_calls=8)
                row["Samples"] += [dict(EvaluationId=i, CostUnits=1, Attempts=1, BestLoss=row["FinalLoss"]) for i in range(8, 16)]
        return campaign, plan

    def test_external_convergence_preserves_utility_and_terminal_progress_without_extra_cost(self):
        campaign, plan = self.external_fixture()
        result = analyze(campaign, plan)
        rows = [r for r in result["Runs"] if r["Method"] == plan["Comparators"][0]]
        self.assertTrue(all(r["Status"] == "completed" and r["EvaluatorCalls"] == 8 and r["TerminalCarryForward"] for r in rows))
        self.assertEqual(0, result["Comparisons"][0]["MeanPairedUtilityDifference"])
        progress = [r for r in result["Progress"] if r["Method"] == plan["Comparators"][0] and r["CostUnits"] == 16]
        self.assertTrue(all(r["KnownRuns"] == 3 and r["MissingRuns"] == 0 for r in progress))

    def test_external_counter_provenance_and_stop_corruption_have_zero_utility(self):
        for field, value in [("OptimizerNfev", 9), ("IndependentEvaluatorCalls", None), ("ControllerDispatches", 9),
                             ("UnknownWork", True), ("StopReason", "evaluation-cap"), ("EvaluatorManifest", {})]:
            campaign, plan = self.external_fixture()
            target = next(r for r in campaign["Runs"] if r["Method"] == plan["Comparators"][0])
            target[field] = value
            result = analyze(campaign, plan)
            row = next(r for r in result["Runs"] if r["Method"] == target["Method"] and r["Task"] == target["Task"] and r["Seed"] == target["Seed"])
            self.assertEqual(0, row["Utility"], field)
            self.assertEqual(8, row["EvaluatorCalls"])
            self.assertFalse(row["TerminalCarryForward"])

    def test_external_protocol_requires_binary_and_smoke_provenance(self):
        for field in ("WorkingTreeSmoke", "EvaluatorBinarySha256"):
            campaign, plan = self.external_fixture()
            del campaign[field]
            with self.assertRaises(ValueError):
                analyze(campaign, plan)

    def test_cli_writes_auditable_artifacts_and_refuses_overwrite(self):
        campaign, plan = fixture()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            campaign_path, plan_path, output = root / "input.json", root / "plan.json", root / "result"
            campaign_path.write_text(json.dumps(campaign), encoding="utf-8")
            plan_path.write_text(json.dumps(plan), encoding="utf-8")
            command = [sys.executable, str(Path(__file__).with_name("analyze.py")), "--input", str(campaign_path),
                       "--plan", str(plan_path), "--output-dir", str(output)]
            result = subprocess.run(command, capture_output=True, text=True, timeout=30)
            self.assertEqual(0, result.returncode, result.stderr)
            report = json.loads((output / "report.json").read_text(encoding="utf-8"))
            self.assertEqual(hashlib.sha256(campaign_path.read_bytes()).hexdigest(), report["InputSha256"])
            self.assertEqual(hashlib.sha256(plan_path.read_bytes()).hexdigest(), report["PlanSha256"])
            self.assertTrue((output / "report.md").exists())
            before = (output / "report.json").read_bytes()
            self.assertNotEqual(0, subprocess.run(command, capture_output=True, timeout=30).returncode)
            self.assertEqual(before, (output / "report.json").read_bytes())

    def test_paired_identical_methods_have_zero_effect_and_interval(self):
        campaign, plan = fixture()
        report = analyze(campaign, plan)
        self.assertEqual(0, report["Comparisons"][0]["MeanPairedUtilityDifference"])
        self.assertEqual([0, 0], report["Comparisons"][0]["Interval"])
        self.assertFalse(report["ConfirmatoryEligible"])
        self.assertTrue(all(r["TrajectoryComplete"] for r in report["Runs"]))

    def test_run_order_and_replay_do_not_change_results(self):
        campaign, plan = fixture()
        first = analyze(campaign, plan)
        campaign["Runs"].reverse()
        self.assertEqual(first, analyze(campaign, plan))

    def test_task_nesting_is_not_flattened_into_pseudo_independent_runs(self):
        groups = [[0.4] * 20, [-0.4] * 20]
        self.assertEqual([0, 0], interval(groups, 400, 1, 0.05, False))
        nested = interval(groups, 400, 1, 0.05, True)
        self.assertLess(nested[0], -0.39)
        self.assertGreater(nested[1], 0.39)

    def test_failed_incumbent_does_not_receive_success_credit(self):
        campaign, plan = fixture()
        campaign["Runs"][0].update(Status="failed", FinalLoss=0)
        report = analyze(campaign, plan)
        self.assertEqual(0, report["Runs"][0]["Utility"])
        self.assertEqual(1, report["Summaries"][0]["FailedOrMissing"])
        self.assertLess(report["Comparisons"][0]["MeanPairedUtilityDifference"], 0)
        self.assertIn("seed 0: failed-or-incomplete", markdown(report))

    def test_missing_run_stays_in_the_denominator_with_unknown_work(self):
        campaign, plan = fixture()
        campaign["Runs"].pop(0)
        report = analyze(campaign, plan)
        self.assertEqual(12, len(report["Runs"]))
        self.assertEqual("missing", report["Runs"][0]["Status"])
        self.assertIsNone(report["Runs"][0]["EvaluatorCalls"])
        self.assertEqual(1, report["Summaries"][0]["UnknownWorkRuns"])
        self.assertEqual(16, report["Summaries"][0]["RecordedEvaluatorCalls"])

    def test_dropped_trace_uses_independent_counters_and_is_not_interpolated(self):
        campaign, plan = fixture()
        campaign["Runs"][0]["Samples"].pop()
        report = analyze(campaign, plan)
        row = report["Runs"][0]
        self.assertEqual("completed", row["Status"])
        self.assertFalse(row["TrajectoryComplete"])
        self.assertEqual(8, row["EvaluatorCalls"])
        self.assertEqual(0, row["TrajectoryPoints"])
        self.assertEqual(1, report["Progress"][0]["MissingRuns"])

    def test_malformed_and_reordered_traces_are_flagged(self):
        for fault in ("duplicate", "order", "loss", "cost", "null"):
            campaign, plan = fixture()
            samples = campaign["Runs"][0]["Samples"]
            if fault == "duplicate":
                samples[1]["EvaluationId"] = 0
            if fault == "order":
                samples.reverse()
            if fault == "loss":
                samples[1]["BestLoss"] = 10
            if fault == "cost":
                samples[0]["CostUnits"] = 2
            if fault == "null":
                samples[0] = None
            self.assertFalse(analyze(campaign, plan)["Runs"][0]["TrajectoryComplete"], fault)

    def test_repeated_timing_rows_cannot_be_counted_as_extra_search_seeds(self):
        campaign, plan = fixture()
        campaign["Runs"][-1] = copy.deepcopy(campaign["Runs"][0])
        with self.assertRaisesRegex(ValueError, "Duplicate task/method/seed"):
            analyze(campaign, plan)

    def test_unpaired_starting_points_and_revision_mismatch_are_rejected(self):
        for fault in ("population", "revision", "budget", "task", "method"):
            campaign, plan = fixture()
            if fault == "population":
                campaign["Runs"][0]["InitialPopulationHash"] = "c" * 64
            if fault == "revision":
                campaign["SourceRevision"] = "c" * 40
            if fault == "budget":
                campaign["Budget"] = 9
            if fault == "task":
                campaign["Runs"][0]["Task"] = "Unplanned"
            if fault == "method":
                campaign["Methods"] = ["Candidate"]
            with self.assertRaises(ValueError, msg=fault):
                analyze(campaign, plan)

    def test_unknown_or_overrun_resources_prevent_success_credit(self):
        for fault in ("unknown", "maximum", "reserved", "calls", "cost"):
            campaign, plan = fixture()
            run = campaign["Runs"][0]
            if fault == "unknown":
                run["Resources"]["Unknown"] = 1
            if fault == "maximum":
                run["Resources"]["MaximumViolated"] = True
            if fault == "reserved":
                run["Resources"]["Reserved"]["cost_units"] = 1
            if fault == "calls":
                run["EvaluatorCalls"] = 9
            if fault == "cost":
                run["Resources"]["Spent"]["cost_units"] = 9
            self.assertEqual(0, analyze(campaign, plan)["Runs"][0]["Utility"], fault)

    def test_nonfinite_or_malformed_loss_is_failed_and_json_remains_serializable(self):
        for loss in (float("nan"), float("inf"), -1, True, "invalid", 10**500):
            campaign, plan = fixture()
            campaign["Runs"][0]["FinalLoss"] = loss
            report = analyze(campaign, plan)
            self.assertEqual(0, report["Runs"][0]["Utility"])
            json.dumps(report, allow_nan=False)

    def test_plan_cannot_claim_confirmation_or_unbounded_sampling(self):
        for key, value in (("Purpose", "confirmation"), ("BootstrapSamples", 1000000), ("SeedCount", True),
                           ("ConfidenceLevel", 1), ("SourceRevision", "working-tree"), ("Unexpected", 1)):
            _, plan = fixture()
            plan[key] = value
            with self.assertRaises(ValueError, msg=key):
                validate_plan(plan)

    def test_loader_rejects_duplicate_keys_nonfinite_json_and_oversized_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "input.json"
            for payload in ('{"Seeds":1,"Seeds":2}', '{"Loss":NaN}', '{"Loss":Infinity}'):
                path.write_text(payload, encoding="utf-8")
                with self.assertRaises(ValueError):
                    load_json(path, 1024)
            with self.assertRaises(ValueError):
                load_json(path, 1)


if __name__ == "__main__":
    unittest.main()
