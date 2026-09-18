import copy
import json
import math
from pathlib import Path
import tempfile
import unittest

from analyze import analyze, load_json, markdown, validate_plan
from design import digest, execute, freeze, plan_design
from presentation import html_report
from test_analyze import fixture


class DesignTests(unittest.TestCase):
    def design(self, **kwargs):
        campaign, plan = fixture()
        return plan_design(campaign, plan, effect=kwargs.pop("effect", .8), **kwargs)

    def test_degenerate_pilot_cannot_claim_two_runs_suffice(self):
        design = self.design()
        self.assertEqual(2, design["NormalApproximationRuns"])
        self.assertGreater(design["RequiredRunsPerTaskMethod"], 30)
        self.assertTrue(design["Feasible"])
        self.assertFalse(design["ConfirmatoryEligible"])

    def test_bound_algebra_meets_declared_power(self):
        design = self.design()
        n = design["RequiredRunsPerTaskMethod"]
        t, m = 2, 1
        alpha, beta = design["FamilywiseAlpha"], 1 - design["TargetPower"]
        h = math.sqrt(2 * math.log(t * m / alpha) / n)
        miss = t * math.exp(-n * (design["AnticipatedEffect"] - h)**2 / 2)
        self.assertLessEqual(miss, beta)
        self.assertAlmostEqual(alpha, t * m * math.exp(-n * h*h / 2))

    def test_smaller_effect_higher_power_and_multiplicity_require_more_runs(self):
        base = self.design()["RequiredRunsPerTaskMethod"]
        self.assertGreater(self.design(effect=.4)["RequiredRunsPerTaskMethod"], base)
        self.assertGreater(self.design(power=.95)["RequiredRunsPerTaskMethod"], base)
        self.assertGreater(self.design(alpha=.01)["RequiredRunsPerTaskMethod"], base)

    def test_infeasible_is_preserved_not_truncated(self):
        design = self.design(maximum_runs=10)
        self.assertFalse(design["Feasible"])
        self.assertGreater(design["RequiredRunsPerTaskMethod"], 10)
        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises(ValueError):
                freeze(Path(root) / "plan", design, {"runtime": "a" * 64})
            self.assertFalse((Path(root) / "plan").exists())

    def test_invalid_numeric_design_and_boolean_inputs(self):
        for kwargs in ({"effect": 0}, {"effect": float("nan")}, {"effect": 1e-300}, {"effect": True},
                       {"power": 1}, {"alpha": 1e-300}, {"maximum_runs": True}):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                self.design(**kwargs)

    def test_pilot_failures_influence_variance_and_are_not_dropped(self):
        campaign, plan = fixture()
        campaign["Runs"].pop(0)
        design = plan_design(campaign, plan, effect=.8)
        self.assertEqual(1, design["PilotFailedOrMissing"])
        self.assertGreater(design["PilotVariance"][0]["PairedVariance"], 0)

    def test_single_pilot_seed_rejected(self):
        campaign, plan = fixture()
        plan["SeedCount"] = campaign["Seeds"] = 1
        campaign["Runs"] = [r for r in campaign["Runs"] if r["Seed"] == 0]
        with self.assertRaises(ValueError):
            plan_design(campaign, plan, effect=.8)

    def register(self, root):
        contract = {"numeric": "a" * 64, "core": "b" * 64}
        directory = Path(root) / "registered"
        registration = freeze(directory, self.design(), contract)
        return directory, registration, contract

    def campaign_for(self, schedule):
        campaign, _ = fixture()
        prototypes = [r for r in campaign["Runs"] if r["Seed"] == 0]
        campaign["Seeds"] = len(schedule["SearchSeeds"])
        campaign["RegistrationSha256"] = schedule["RegistrationSha256"]
        campaign["Runs"] = [dict(copy.deepcopy(row), Seed=seed) for row in prototypes for seed in schedule["SearchSeeds"]]
        return campaign

    def test_one_use_claim_before_dispatch_and_exact_fresh_seeds(self):
        with tempfile.TemporaryDirectory() as root:
            directory, registration, contract = self.register(root)
            self.assertFalse(set(registration["SearchSeeds"]).intersection(range(3)))
            calls = []
            def runner(schedule):
                self.assertTrue((directory / "started.json").exists())
                calls.append(schedule)
                return self.campaign_for(schedule)
            report = execute(directory, registration["RegistrationSha256"], contract, runner)
            self.assertEqual(1, len(calls))
            self.assertEqual(4 * len(registration["SearchSeeds"]), len(report["Runs"]))
            self.assertFalse(report["ConfirmatoryEligible"])
            self.assertIn("Locked fixed-sample", (directory / "report.md").read_text())
            self.assertTrue((directory / "report.html").exists())
            with self.assertRaises(FileExistsError):
                execute(directory, registration["RegistrationSha256"], contract, runner)
            self.assertEqual(1, len(calls))

    def test_runtime_identity_and_registration_tampering_dispatch_nothing(self):
        for mutation in ("runtime", "plan", "external-hash"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as root:
                directory, registration, contract = self.register(root)
                identity = registration["RegistrationSha256"]
                if mutation == "runtime":
                    contract["core"] = "c" * 64
                elif mutation == "plan":
                    changed = json.loads((directory / "registration.json").read_text())
                    changed["SearchSeeds"][0] += 1
                    (directory / "registration.json").write_text(json.dumps(changed))
                else:
                    identity = "f" * 64
                with self.assertRaises(ValueError):
                    execute(directory, identity, contract, lambda _: self.fail("must not dispatch"))
                self.assertFalse((directory / "started.json").exists())

    def test_failed_controller_preserves_every_scheduled_run_and_refuses_retry(self):
        with tempfile.TemporaryDirectory() as root:
            directory, registration, contract = self.register(root)
            def fail(_):
                raise RuntimeError("worker failed")
            with self.assertRaises(RuntimeError):
                execute(directory, registration["RegistrationSha256"], contract, fail)
            failed, _ = load_json(directory / "failed-schedule.json", 1024 * 1024)
            self.assertEqual(4 * len(registration["SearchSeeds"]), len(failed["Runs"]))
            self.assertTrue(all(r["Work"] is None for r in failed["Runs"]))
            with self.assertRaises(FileExistsError):
                execute(directory, registration["RegistrationSha256"], contract, fail)

    def test_reused_pilot_seed_or_extra_run_cannot_enter_analysis(self):
        for mode in ("pilot", "duplicate", "unplanned"):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as root:
                directory, registration, contract = self.register(root)
                def runner(schedule):
                    campaign = self.campaign_for(schedule)
                    if mode == "pilot":
                        campaign["Runs"][0]["Seed"] = 0
                    elif mode == "duplicate":
                        campaign["Runs"][-1] = copy.deepcopy(campaign["Runs"][0])
                    else:
                        campaign["Runs"][0]["Method"] = "unplanned"
                    return campaign
                with self.assertRaises(ValueError):
                    execute(directory, registration["RegistrationSha256"], contract, runner)
                self.assertTrue((directory / "raw-campaign.json").exists())
                self.assertFalse((directory / "report.json").exists())

    def test_changed_n_cannot_be_frozen(self):
        design = self.design()
        design["FixedAnalysisPlan"]["SeedCount"] = 2
        with tempfile.TemporaryDirectory() as root, self.assertRaises(ValueError):
            freeze(Path(root) / "registered", design, {"runtime": "a" * 64})


class PresentationTests(unittest.TestCase):
    def test_dropped_trace_keeps_independent_work_but_not_curve_or_auc(self):
        campaign, plan = fixture()
        campaign["Runs"][0]["DroppedTraceRecords"] = 1
        report = analyze(campaign, plan)
        row = report["Runs"][0]
        self.assertEqual(8, row["EvaluatorCalls"])
        self.assertFalse(row["TrajectoryComplete"])
        self.assertEqual(2, report["Scorecard"][0]["AucKnownRuns"])

    def test_auc_is_left_continuous_no_invented_initial_or_failed_quality(self):
        campaign, plan = fixture()
        report = analyze(campaign, plan)
        expected = (1/2 + 1/3 + 1/4) / 3 * 7/8
        self.assertAlmostEqual(expected, report["Scorecard"][0]["MeanUtilityAucKnownOnly"])
        campaign["Runs"][0]["Status"] = "failed"
        report = analyze(campaign, plan)
        self.assertEqual(2, report["Scorecard"][0]["AucKnownRuns"])

    def test_unknown_cost_and_missing_runs_never_become_zero_cost_successes(self):
        campaign, plan = fixture()
        campaign["Runs"][0]["Resources"]["Unknown"] = 1
        campaign["Runs"].pop(1)
        report = analyze(campaign, plan)
        self.assertEqual(2, report["Summaries"][0]["UnknownWorkRuns"])
        self.assertEqual(16, report["Scorecard"][0]["KnownResourceTotals"]["cost_units"])
        self.assertEqual(2, report["Scorecard"][0]["ResourceUnknownRuns"]["cost_units"])
        self.assertIsNone(report["Scorecard"][0]["CorrectnessPassRate"])
        self.assertIsNone(report["Scorecard"][0]["RuntimeSpeedup"])

    def test_predeclared_targets_censor_failures_and_flag_unknowns(self):
        campaign, plan = fixture()
        plan["SchemaVersion"] = 2
        for task in plan["Tasks"]:
            task["TargetLoss"] = 1.5
        campaign["Runs"][2]["Samples"] = []
        report = analyze(campaign, plan)
        score = report["Scorecard"][0]
        self.assertAlmostEqual(1/3, score["TargetHitRate"])
        self.assertEqual(1, score["UnknownTargetRuns"])
        self.assertEqual([1, 8, 8], [r["CostUnits"] for r in score["TargetCostObservations"]])
        self.assertEqual([False, True, True], [r["Censored"] for r in score["TargetCostObservations"]])

    def test_schema_one_cannot_silently_gain_posthoc_targets(self):
        _, plan = fixture()
        plan["Tasks"][0]["TargetLoss"] = 1
        with self.assertRaises(ValueError):
            validate_plan(plan)

    def test_html_escapes_diagnostics_and_renders_offline_progress(self):
        campaign, plan = fixture()
        plan["Budget"] = campaign["Budget"] = 16
        # Incomplete runs still have known curves up to their actual final budget;
        # use complete 16-point fixtures here to exercise the SVG path.
        for row in campaign["Runs"]:
            row.update(EvaluatorCalls=16, Proposals=16)
            row["Resources"]["Spent"] = dict(cost_units=16, proposal_calls=8)
            row["Samples"] += [dict(EvaluationId=i, CostUnits=1, Attempts=1, BestLoss=row["FinalLoss"]) for i in range(8, 16)]
        report = analyze(campaign, plan)
        report["Caveats"].append('<script>alert("x")</script>')
        rendered = html_report(report)
        self.assertNotIn("<script>", rendered)
        self.assertIn("&lt;script&gt;", rendered)
        self.assertIn("<svg", rendered)
        self.assertIn("default-src", rendered)
        self.assertIn("Budget-indexed progress", markdown(report))

    def test_win_tie_loss_counts_include_missing_pair(self):
        campaign, plan = fixture()
        campaign["Runs"].pop(0)
        report = analyze(campaign, plan)
        self.assertEqual(dict(Wins=0, Ties=5, Losses=1), report["Comparisons"][0]["WinTieLoss"])


if __name__ == "__main__":
    unittest.main()
