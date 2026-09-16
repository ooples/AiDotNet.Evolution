import copy
import os
import tempfile
import unittest
from pathlib import Path

from warm_budget import CampaignBudget, requirements
from warm_evaluator import WarmEvaluator
from warm_screening import ScreenedEvaluator, policy, selected_rejects, selected_population, audit_summary, validate_registration, validate_policy
from warm_study_design import digest


class Sandbox:
    identity = {"fixture":"deterministic-no-provider"}
    def __init__(self):
        self.calls = []
    def run(self,code,request,*,phase):
        small = request["problems"] == ["small"]
        self.calls.append((code,small,phase))
        elapsed = (.1 if code in ("slow","crossover") and small else
                   .1 if code == "slow" else .001 if code == "crossover" else .01)
        elapsed *= 1 + len(self.calls)*.0001
        return dict(status="completed",resource_status="measured",output=[code != "wrong" and (code != "small-only" or small)],
                    elapsed_seconds=elapsed,unknown_work=False,id=str(len(self.calls)),startup_seconds=.1,
                    resources=dict(cpu_seconds_through_response=.1,peak_bytes_through_response=100))


class ScreeningTests(unittest.TestCase):
    def test_frozen_policy_rejects_equal_but_wrong_types(self):
        for key,value in (("scale_divisor",True),("baseline_samples",3.0),("audit_pairs",3.0),("slow_ratio",4)):
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_policy(dict(policy(True),**{key:value}))
        with self.assertRaises(ValueError):
            validate_policy(None)

    def test_invalid_individual_timing_cannot_hide_in_a_valid_median(self):
        for elapsed in (0,-1,float("nan"),float("inf"),None,True,"0.1"):
            with self.subTest(elapsed=elapsed), tempfile.TemporaryDirectory() as root:
                pipeline = self.setup_pipeline(root)
                run = self.sandbox.run
                def bad_timing(*args,**kwargs):
                    row = run(*args,**kwargs)
                    if len(self.sandbox.calls) == 2:
                        row["elapsed_seconds"] = elapsed
                    return row
                self.sandbox.run = bad_timing
                result = pipeline.full("good",owner="track")
                self.assertEqual("invalid",result["status"])
                self.assertIsNone(result["duration_seconds"])
                self.assertEqual(2,len(result["sample_ids"]))
                self.assertEqual(2,self.budget.snapshot()["spent"]["search_containers"])

    def test_invalid_full_baseline_stops_search_and_cannot_be_retried(self):
        with tempfile.TemporaryDirectory() as root:
            pipeline = self.setup_pipeline(root)
            pipeline.full.validate = lambda output: False
            with self.assertRaisesRegex(ValueError,"Original failed full evaluation"):
                pipeline("original",owner="track")
            self.assertEqual("invalid",pipeline.states["track"]["events"][0]["status"])
            spent = self.budget.snapshot()["spent"]
            self.assertEqual(4,spent["search_containers"])
            for code in ("original","good"):
                with self.assertRaises(ValueError):
                    pipeline(code,owner="track")
            with self.assertRaises(ValueError):
                pipeline.finish()
            self.assertEqual(spent,self.budget.snapshot()["spent"])

    def test_failed_cheap_baseline_cannot_be_retried_until_lucky(self):
        with tempfile.TemporaryDirectory() as root:
            pipeline = self.setup_pipeline(root)
            pipeline.baseline.validate = lambda output: False
            with self.assertRaises(ValueError):
                pipeline("original",owner="track")
            pipeline.baseline.validate = lambda output: True
            with self.assertRaises(ValueError):
                pipeline("original",owner="track")
            self.assertEqual(1,self.budget.snapshot()["spent"]["search_containers"])

    def test_retention_metric_excludes_candidates_failing_after_the_cheap_pass(self):
        from run_screening_pilot import retained_fraction
        before = [dict(candidate_hash="original",duration_seconds=10),dict(candidate_hash="improved",duration_seconds=1)]
        after = [dict(candidate_hash="original",status="valid"),dict(candidate_hash="improved",status="invalid")]
        self.assertEqual(.1,retained_fraction(before,after))
        with self.assertRaises(ValueError):
            retained_fraction(before,[dict(candidate_hash="original",status="invalid")])

    def test_screen_cannot_open_final_acceptance_inputs(self):
        plan = dict(screening_policy=policy(True),screening_seed=917,screening_instance_seeds=[1],
                    search_instance_seeds=[2],diagnostic_instance_seeds=[3],audit_instance_seeds=[4])
        validate_registration(plan)
        for seed in (2,3,4,True,-1):
            with self.assertRaises(ValueError):
                validate_registration(dict(plan,screening_instance_seeds=[seed]))

    @unittest.skipUnless(os.environ.get("EVOLUTION_SANDBOX_IMAGE"),"Requires the actual isolated warm image")
    def test_public_local_screening_ablation_without_provider(self):
        from run_screening_pilot import run
        parent = os.environ.get("EVOLUTION_SANDBOX_EVIDENCE")
        with tempfile.TemporaryDirectory(prefix="screening-driver-") as temporary:
            root = Path(tempfile.mkdtemp(prefix="screening-proof-",dir=parent)) if parent else Path(temporary)
            result = run(root/"study",os.environ["EVOLUTION_SANDBOX_IMAGE"])
        self.assertEqual(0,result["model_calls"])
        self.assertFalse(result["default_enabled"])
        self.assertEqual(["reject_heavy","all_advance","size_crossover"],[r["panel"] for r in result["panels"]])
        # Timing effects are measured outcomes, not flaky pass/fail targets. Cost
        # accounting, complete paired panels and correctness are hard assertions.
        self.assertTrue(all(r["before_containers"] > 0 and r["after_containers"] > 0 for r in result["panels"]))

    def setup_pipeline(self, folder, *, budget_override=None):
        self.sandbox = Sandbox()
        frozen = policy(True)
        limits = requirements([dict(tracks=[("controlled","aidotnet")])],4,3,3,frozen)
        self.budget = CampaignBudget(Path(folder)/"journal.jsonl",budget_override or limits,"fixture")
        def evaluator(cases,samples,phase,stage):
            return WarmEvaluator(self.sandbox,"Solver",cases,lambda output:output==[True],
                identity=digest(cases),samples=samples,phase=phase,stage=stage,budget=self.budget)
        return ScreenedEvaluator("original",evaluator(["small"],1,"search","screen"),
            evaluator(["full"],3,"search","search"),evaluator(["full"],1,"confirmation","rejection-audit"),
            frozen,tracks=1,seed=42)

    def test_full_score_only_after_screen_and_full_correctness(self):
        with tempfile.TemporaryDirectory() as root:
            pipeline = self.setup_pipeline(root)
            initial = pipeline("original",owner="track")
            passed = pipeline("good",owner="track")
            self.assertEqual("valid",passed["status"])
            self.assertEqual(passed["quality"],passed["screening"]["full"]["quality"])
            self.assertEqual(4,len(passed["sample_ids"]))
            self.assertEqual("baseline",initial["screening"]["reason"])
            self.assertEqual(0,pipeline.finish()["track"]["summary"]["rejected"])

    def test_one_noisy_initial_measurement_cannot_disable_all_screening(self):
        with tempfile.TemporaryDirectory() as root:
            pipeline = self.setup_pipeline(root)
            run = self.sandbox.run
            def noisy(*args,**kwargs):
                row = run(*args,**kwargs)
                if len(self.sandbox.calls) == 1:
                    row["elapsed_seconds"] = 10.
                return row
            self.sandbox.run = noisy
            original = pipeline("original",owner="track")
            self.assertEqual(3,len(original["screening"]["screen"]["sample_ids"]))
            self.assertEqual("invalid",pipeline("slow",owner="track")["status"])

    def test_wrong_and_slow_rejects_are_audited_but_never_search_feedback(self):
        with tempfile.TemporaryDirectory() as root:
            pipeline = self.setup_pipeline(root)
            pipeline("original",owner="track")
            for code in ("wrong","slow"):
                result = pipeline(code,owner="track")
                self.assertEqual("invalid",result["status"])
                self.assertIsNone(result["screening"]["full"])
                self.assertEqual(1,len(result["sample_ids"]))
            before = copy.deepcopy(pipeline.states["track"]["events"])
            report = pipeline.finish()["track"]
            self.assertEqual(before,pipeline.states["track"]["events"])
            self.assertEqual(2,report["summary"]["audited"])
            self.assertEqual(0,report["summary"]["upper"])
            self.assertEqual(12,self.budget.snapshot()["spent"]["confirmation_containers"])
            with self.assertRaises(ValueError):
                pipeline.finish()
            with self.assertRaises(ValueError):
                pipeline("good",owner="track")

    def test_cheap_correctness_never_substitutes_for_full_correctness(self):
        with tempfile.TemporaryDirectory() as root:
            pipeline = self.setup_pipeline(root)
            pipeline("original",owner="track")
            value = pipeline("small-only",owner="track")
            self.assertEqual("valid",value["screening"]["screen"]["status"])
            self.assertEqual("advance",value["screening"]["reason"])
            self.assertEqual("invalid",value["status"])
            self.assertEqual("invalid",value["screening"]["full"]["status"])

    def test_size_crossover_false_rejection_is_exposed(self):
        with tempfile.TemporaryDirectory() as root:
            pipeline = self.setup_pipeline(root)
            pipeline("original",owner="track")
            self.assertEqual("invalid",pipeline("crossover",owner="track")["status"])
            report = pipeline.finish()["track"]
            self.assertEqual("useful",report["audits"][0]["classification"])
            self.assertEqual(1,report["summary"]["lower"])
            self.assertEqual(1,report["summary"]["upper"])

    def test_reserve_cannot_be_consumed_by_search(self):
        with tempfile.TemporaryDirectory() as root:
            pipeline = self.setup_pipeline(root,budget_override=dict(model_calls=0,search_containers=7,confirmation_containers=6))
            pipeline("original",owner="track")
            pipeline("slow",owner="track")
            with self.assertRaises(ValueError):
                pipeline("good",owner="track")
            with self.assertRaises(ValueError):
                pipeline("original",owner="track")
            self.assertEqual(1,pipeline.finish()["track"]["summary"]["audited"])
            self.assertEqual(6,self.budget.snapshot()["spent"]["confirmation_containers"])

    def test_frozen_population_selection_is_order_independent_and_bounded(self):
        values = ["a","b","c","d"]
        self.assertEqual(selected_rejects(values,42,"track",2),selected_rejects(values[::-1],42,"track",2))
        self.assertEqual(2,len(selected_rejects(values,42,"track",2)))
        report = audit_summary(values,[dict(classification="unresolved")]*2,1)
        self.assertEqual(1,report["upper"])
        self.assertEqual(0,report["lower"])

    def test_wrong_initial_and_mutable_caller_results_cannot_replace_baseline(self):
        with tempfile.TemporaryDirectory() as root:
            pipeline = self.setup_pipeline(root)
            with self.assertRaises(ValueError):
                pipeline("good",owner="track")
            original = pipeline("original",owner="track")
            original["screening"]["screen"]["duration_seconds"] = 100
            self.assertEqual("invalid",pipeline("slow",owner="track")["status"])

    def test_unknown_screen_work_stops_instead_of_becoming_a_cheap_reject(self):
        with tempfile.TemporaryDirectory() as root:
            pipeline = self.setup_pipeline(root)
            def unknown(*args,**kwargs):
                raise RuntimeError("resource probe unavailable")
            pipeline.screen.sandbox.run = unknown
            with self.assertRaises(RuntimeError):
                pipeline("original",owner="track")
            snapshot = self.budget.snapshot()
            self.assertTrue(snapshot["closed"])
            self.assertEqual(1,snapshot["spent"]["search_containers"])
            self.assertEqual("unknown",snapshot["rows"][-1]["status"])

    def test_opt_in_policy_and_full_worst_case_capacity(self):
        self.assertFalse(policy()["enabled"])
        with self.assertRaises(ValueError):
            policy("yes")
        grid = [dict(tracks=[("controlled","aidotnet")])]
        off = requirements(grid,4,3,16,policy())
        on = requirements(grid,4,3,16,policy(True))
        self.assertEqual(off["model_calls"],on["model_calls"])
        self.assertEqual(15,off["search_containers"])
        self.assertEqual(22,on["search_containers"])
        self.assertEqual(52,on["confirmation_containers"])

    def test_audit_reserve_is_amortized_across_a_frozen_task_seed_block(self):
        grid = [dict(tracks=[("controlled",str(i)) for i in range(6)])]
        limits = requirements(grid,4,3,16,policy(True))
        self.assertEqual(6*40+12,limits["confirmation_containers"])
        population = {str(i):["a","b","c"] for i in range(6)}
        selected = selected_population(population,917,2)
        self.assertEqual(2,len(selected))
        self.assertEqual(selected,selected_population(dict(reversed(list(population.items()))),917,2))
        self.assertEqual(1,audit_summary(["a"],[],6)["upper"])


if __name__ == "__main__":
    unittest.main()
