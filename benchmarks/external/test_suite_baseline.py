from pathlib import Path
from types import SimpleNamespace
import json
import tempfile
import unittest

from scipy_baseline import Bridge
from suite_baseline import run_one
from run_numeric_comparison import run

DLL = Path(__file__).resolve().parents[1] / "AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll"


class SuiteBaselineTests(unittest.TestCase):
    def test_campaign_separates_tracks_and_refuses_overwrite_or_registered_final(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            request = dict(schema="aidotnet-numeric-suite-request-v1", mode="contract-smoke", partition="development",
                           plan_hash="a" * 64, configuration_hash="b" * 64, source_revision="c" * 40, budget=121,
                           methods=["RandomSearch", "HillClimb", "FixedMapElites", "AdaptiveMapElites", "DiagonalCma"],
                           instances=[dict(id=task, seed=42, search_seeds=[37]) for task in ("block-trap", "rastrigin", "knapsack")])
            path = root / "request.json"
            path.write_text(json.dumps(request), encoding="utf-8")
            report = run(path, DLL, root / "result", ("controlled", "native-sized"))
            self.assertEqual("completed", report["status"], report["failures"])
            self.assertEqual(15, len(report["core"]["runs"]))
            self.assertEqual(9, len(report["external"]))
            self.assertIn("ribs_baseline.py", report["adapters"])
            self.assertEqual({"controlled", "native-sized"}, {row["mode"] for row in report["external"]})
            with self.assertRaises(FileExistsError):
                run(path, DLL, root / "result")
            request.update(mode="registered", partition="final")
            path.write_text(json.dumps(request), encoding="utf-8")
            with self.assertRaises(ValueError):
                run(path, DLL, root / "forbidden")
            self.assertFalse((root / "forbidden").exists())

    def manifest(self, task, budget=17):
        bridge = Bridge(DLL, task, 37, budget, instance_seed=42)
        try:
            return bridge.receive()
        finally:
            bridge.close()

    def test_all_families_reconcile_partial_generation_and_raw_receipts(self):
        for task in ("block-trap", "rastrigin", "knapsack", "stochastic-regression", "diffusion-control",
                     "coupled-absolute", "spin-glass", "inventory-risk", "robust-design"):
            with self.subTest(task=task):
                manifest = self.manifest(task)
                row = run_one(DLL, task, 42, 37, 17, manifest["InitialPopulationHash"])
                self.assertEqual("completed", row["status"], row["error"])
                self.assertLessEqual(row["independent_evaluator_calls"], 17)
                self.assertFalse(row["unknown_work"])
                self.assertEqual(row["samples"], row["evaluator_summary"]["Samples"])
                self.assertEqual(row["controller_dispatches"] * manifest["WorkUnitsPerEvaluation"],
                                 row["evaluator_summary"]["Resources"]["Spent"]["cost_units"])

    def test_native_sized_control_discloses_modified_initialization(self):
        manifest = self.manifest("rastrigin", 121)
        row = run_one(DLL, "rastrigin", 42, 37, 121, manifest["InitialPopulationHash"], mode="native-sized")
        self.assertEqual("completed", row["status"], row["error"])
        self.assertEqual(121, row["independent_evaluator_calls"])
        self.assertIn("not untouched defaults", row["settings"]["initialization"])
        self.assertEqual(0, row["settings"]["tuning_trials"])

    def test_mismatched_initialization_dispatches_nothing(self):
        row = run_one(DLL, "rastrigin", 42, 37, 17, "0" * 64)
        self.assertEqual("failed", row["status"])
        self.assertEqual(0, row["controller_dispatches"])

    def test_incorrect_optimizer_counter_and_exception_preserve_failure(self):
        def incorrect(objective, bounds, **kwargs):
            losses = [objective(value) for value in kwargs["init"]]
            return SimpleNamespace(nfev=9, fun=min(losses), success=True)

        def broken(objective, bounds, **kwargs):
            objective(kwargs["init"][0])
            raise RuntimeError("deliberate optimizer failure")

        for optimizer, expected in ((incorrect, 8), (broken, 1)):
            row = run_one(DLL, "rastrigin", 42, 37, 17, self.manifest("rastrigin")["InitialPopulationHash"], optimizer=optimizer)
            self.assertEqual("failed", row["status"])
            self.assertEqual(expected, len(row["samples"]))
            self.assertTrue(row["unknown_work"])

    def test_invalid_work_contracts_fail_before_process_creation(self):
        for overrides in ({"budget": True}, {"budget": 7}, {"budget": 4097}, {"instance_seed": -1},
                          {"search_seed": 2**64}, {"mode": "native-default"}, {"mode": "native-sized"}):
            args = dict(dll=DLL, task="rastrigin", instance_seed=42, search_seed=37, budget=17, expected_hash="0" * 64)
            args.update(overrides)
            with self.assertRaises(ValueError):
                run_one(**args)


if __name__ == "__main__":
    unittest.main()
