import copy
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from docker_sandbox import encode
from run_warm_study import execute, predecessor, prepare, verify
from warm_evaluator import WarmEvaluator
from warm_study_design import call_cap, choose_profiles, digest, grid, power_requirement
from warm_study_report import interval, summarize


def fixture(phase="selection", count=4):
    cells = grid(phase, list(range(count)), dict(aidotnet="uniform",openevolve="default"))
    plan = dict(phase=phase, grid=cells)
    rows = []
    for cell in cells:
        pairs = []
        for mode, method in cell["tracks"]:
            value = dict(status="valid",duration_seconds=1,throughput_cases_per_second=2,
                         resources=[dict(cpu_seconds_through_response=.5,warm_cpu_seconds=.1,peak_bytes_through_response=500)])
            pairs.append(dict(mode=mode,method=method,speedup=1,deployed_seconds=1,deployed=value,original=value,selected=value,
                              audits=[value],fallback=True,model_tokens=10,model_calls=4,search_wall_seconds=2,search_evaluation_seconds=1))
        rows.append(dict(**cell,status="completed",pairs=pairs))
    return dict(plan=plan,plan_sha256=digest(plan),phase=phase,status="completed",unknown_work=0,rows=rows)


class WarmDesignTests(unittest.TestCase):
    def test_budget_counts_all_tuning_trials_controls_and_final_cells(self):
        profiles = dict(aidotnet="uniform",openevolve="default")
        self.assertEqual(160,call_cap(grid("development",[1,2],profiles)))
        self.assertEqual(252,call_cap(grid("selection",list(range(4)),profiles)))
        self.assertEqual(756,call_cap(grid("final",list(range(12)),profiles)))

    def test_equal_tuning_grid_and_default_tie(self):
        rows = fixture("development",2)["rows"]
        self.assertEqual(dict(aidotnet="uniform",openevolve="default"),choose_profiles(rows))
        for r in rows:
            if r["evolution_profile"] == "best":
                for p in r["pairs"]:
                    if p["method"] == "aidotnet":
                        p["speedup"] = 2
        self.assertEqual("best",choose_profiles(rows)["aidotnet"])
        with self.assertRaises(ValueError):
            choose_profiles(rows[:-1])

    def test_power_uses_search_units_not_container_repeats(self):
        report = fixture()
        self.assertEqual(12,power_requirement(report["rows"])["searches_per_task"])
        with self.assertRaises(ValueError):
            power_requirement(report["rows"][:-1])
        for row in report["rows"]:
            for pair in row["pairs"]:
                if pair["method"] == "aidotnet":
                    pair["speedup"] = 1 if row["seed"] % 2 else 4
        self.assertGreater(power_requirement(report["rows"])["searches_per_task"],12)

    def test_unchanged_fallback_cannot_win_from_noisy_original_timings(self):
        report = fixture("final",12)
        for row in report["rows"]:
            for pair in row["pairs"]:
                pair["deployed_seconds"] = .01 if pair["method"] == "aidotnet" else 1
        result = summarize(report)
        self.assertFalse(result["all_registered_latency_gates_pass"])
        self.assertTrue(all(c["ratio"] == 1 for c in result["comparisons"]))
        self.assertEqual("none",result["claim"])

    def test_missing_unknown_duplicate_or_changed_cells_block_summary(self):
        base = fixture()
        for change in (lambda r:r["rows"].pop(),lambda r:r.update(unknown_work=1),
                       lambda r:r["rows"][0]["pairs"].pop(),
                       lambda r:r["rows"][0]["pairs"].append(copy.deepcopy(r["rows"][0]["pairs"][0])),
                       lambda r:r["rows"][0].update(seed=123)):
            value = copy.deepcopy(base)
            change(value)
            with self.assertRaises(ValueError):
                summarize(value)

    def test_interval_and_scope_do_not_create_all_metric_claim(self):
        report = fixture("final",12)
        for row in report["rows"]:
            for pair in row["pairs"]:
                if pair["method"] == "aidotnet":
                    pair["speedup"] = 2
        result = summarize(report)
        self.assertTrue(result["all_registered_latency_gates_pass"])
        self.assertEqual("none",result["claim"])
        self.assertAlmostEqual(.5,result["comparisons"][0]["upper"])
        with self.assertRaises(ValueError):
            interval([1],.05)

    def test_bad_hash_and_one_use_marker_prevent_model_dispatch(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/"plan.json"
            path.write_bytes(encode({"phase":"development"}))
            with self.assertRaises(ValueError):
                execute(path,"wrong")
            (path.parent/"execution-started.json").write_text("{}")
            with patch("run_warm_study.verify"), patch("run_warm_study.CodexTransport") as provider:
                with self.assertRaises(FileExistsError):
                    execute(path,digest({"phase":"development"}))
                provider.assert_not_called()

    def test_runtime_mutation_and_wrong_predecessor_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/"file"
            path.write_text("changed")
            with self.assertRaises(ValueError):
                verify({"artifacts_sha256":{str(path):"wrong"}})
            path.write_text(json.dumps(fixture()))
            with self.assertRaises(ValueError):
                predecessor(path,"selection")

    def test_budget_change_requires_explicit_new_approval_before_next_partition(self):
        prior = dict(cumulative_model_calls=412,plan={"call_limit":412,"profiles":dict(aidotnet="uniform",openevolve="default")},rows=[])
        with patch("run_warm_study.predecessor",return_value=prior), patch("run_warm_study.power_requirement",return_value={"searches_per_task":12}):
            with self.assertRaisesRegex(ValueError,"approval reference"):
                prepare("unused","unused","unused","unused","unused","unused",phase="final",previous="fixture",call_limit=1168)
            with self.assertRaisesRegex(ValueError,"756 more calls required"):
                prepare("unused","unused","unused","unused","unused","unused",phase="final",previous="fixture",call_limit=412)

    def test_evaluator_refuses_empty_input_and_zero_clock_measurement(self):
        class Sandbox:
            identity = {}
            def run(self,*a,**kw):
                return dict(status="completed",resource_status="measured",output=[1],elapsed_seconds=0,
                            unknown_work=False,id="sample",resources={},startup_seconds=1)
        with self.assertRaises(ValueError):
            WarmEvaluator(Sandbox(),"Solver",[],lambda x:True,identity="test")
        evaluator = WarmEvaluator(Sandbox(),"Solver",[1],lambda x:True,identity="test",samples=1)
        self.assertEqual("invalid",evaluator("class Solver: pass")["status"])

    def test_failed_dispatch_retains_planned_cells_and_attempted_calls(self):
        from types import SimpleNamespace
        value = fixture("development",1)
        plan = dict(value["plan"], cumulative_calls_before=0,image="fixture",upstream="fixture",openevolve="fixture",
                    codex="fixture",model="fixture",dll="fixture",search_instance_seeds=[1,2],scale_multiplier=1,iterations=4,samples=3)
        task = dict(metadata={"class":"Solver"},cases=[1],validate=lambda x:True,initial="class Solver: pass")
        transport = SimpleNamespace(calls=1,failed=True,generate_metered=None)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/"plan.json"
            path.write_bytes(encode(plan))
            with patch("run_warm_study.verify"), patch("run_warm_study.prepare_task",return_value=task), \
                 patch("run_warm_study.description",return_value="fixture"), \
                 patch("run_warm_study.WarmDockerSandbox",return_value=SimpleNamespace(rows=[],identity={})), \
                 patch("run_warm_study.CodexTransport",return_value=transport), \
                 patch("run_warm_study.run_campaign",side_effect=RuntimeError("injected provider outage")):
                report = execute(path,digest(plan))
            self.assertEqual("failed",report["status"])
            self.assertEqual(len(plan["grid"]),len(report["rows"]))
            self.assertEqual("failed",report["rows"][0]["status"])
            self.assertTrue(all(r["status"] == "not-run" for r in report["rows"][1:]))
            self.assertEqual(1,report["model_calls"])
            self.assertEqual(1,report["unknown_work"])

    @unittest.skipUnless(all(os.environ.get(k) for k in ("EVOLUTION_SANDBOX_IMAGE","EVOLUTION_PROFILE_DLL","EVOLUTION_PROFILE_UPSTREAM")),
                         "Requires actual Docker image, built host and pinned OpenEvolve")
    def test_complete_warm_pipeline_with_real_controllers_and_no_provider(self):
        from types import SimpleNamespace
        value = fixture("selection",1)
        plan = dict(value["plan"],grid=value["plan"]["grid"][:1], cumulative_calls_before=0,
                    image=os.environ["EVOLUTION_SANDBOX_IMAGE"],upstream="fixture",openevolve=os.environ["EVOLUTION_PROFILE_UPSTREAM"],
                    codex="fixture-no-provider",model="contract-no-provider",dll=os.environ["EVOLUTION_PROFILE_DLL"],
                    search_instance_seeds=[1,2],diagnostic_instance_seeds=[3,4],audit_instance_seeds=list(range(5,13)),
                    scale_multiplier=1,iterations=1,samples=1)
        initial = "class Solver:\n def solve(self,p): return p['x']+1\n"
        task = dict(metadata={"id":"fixture","class":"Solver","scale":1},cases=[{"x":1},{"x":2}],
                    validate=lambda x:x==[2,3],initial=initial)
        transport = SimpleNamespace(calls=0,failed=False)
        def generate(system,messages):
            transport.calls += 1
            return dict(text="```python\n"+initial+f"# fixture-{transport.calls}\n```",cost_units=0,
                        cost_metric="reported_input_plus_output_tokens")
        transport.generate_metered = generate
        parent = os.environ.get("EVOLUTION_SANDBOX_EVIDENCE")
        if parent:
            Path(parent).mkdir(parents=True,exist_ok=True)
        root = Path(tempfile.mkdtemp(prefix="warm-controller-proof-",dir=parent))
        path = root/"plan.json"
        path.write_bytes(encode(plan))
        with patch("run_warm_study.verify"), patch("run_warm_study.prepare_task",return_value=task), \
             patch("run_warm_study.CodexTransport",return_value=transport):
            report = execute(path,digest(plan))
        self.assertEqual("completed",report["status"],report.get("error"))
        self.assertEqual(6,transport.calls)
        self.assertEqual(0,report["unknown_work"])
        self.assertEqual(6,len(report["rows"][0]["pairs"]))
        self.assertTrue(all(p["selected"]["status"] == "valid" and all(a["status"] == "valid" for a in p["audits"])
                            for p in report["rows"][0]["pairs"]))
        self.assertEqual(48,report["evaluator_attempts"])


if __name__ == "__main__":
    unittest.main()
