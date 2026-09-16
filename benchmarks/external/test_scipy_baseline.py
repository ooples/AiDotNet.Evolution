import copy
import json
from pathlib import Path
from types import SimpleNamespace
import unittest

import scipy_baseline as baseline

DLL = Path(__file__).resolve().parents[1] / "AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll"


class ExternalTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        baseline.versions()
        if not DLL.is_file():
            raise RuntimeError("Build the Release numeric quality executable before these integration tests.")

    def manifest(self, task="Sphere", budget=32):
        bridge = baseline.Bridge(DLL, task, 0, budget)
        try:
            return bridge.receive()
        finally:
            bridge.close()

    def test_real_scipy_all_objectives_match_exact_initialization_and_replay(self):
        for task in baseline.TASKS:
            with self.subTest(task=task):
                manifest = self.manifest(task)
                first = baseline.run_one(DLL, task, 0, 32, manifest["InitialPopulationHash"])
                second = baseline.run_one(DLL, task, 0, 32, manifest["InitialPopulationHash"])
                self.assertEqual("completed", first["Status"], first["Error"])
                self.assertEqual(32, first["IndependentEvaluatorCalls"])
                self.assertEqual(24, first["Resources"]["Spent"]["proposal_calls"])
                self.assertEqual(first, second)

    def test_valid_early_convergence_is_not_a_failed_run(self):
        def converged(objective, bounds, **kwargs):
            losses = [objective(value) for value in kwargs["init"]]
            return SimpleNamespace(nfev=8, fun=min(losses), success=True)
        row = baseline.run_one(DLL, "Sphere", 0, 32, self.manifest()["InitialPopulationHash"], optimizer=converged)
        self.assertEqual(("completed", "converged", 8), (row["Status"], row["StopReason"], row["EvaluatorCalls"]))

    def test_invalid_counter_and_unexpected_stop_fail_with_known_costs(self):
        for nfev, success in [(9, True), (8, False)]:
            def invalid(objective, bounds, **kwargs):
                losses = [objective(value) for value in kwargs["init"]]
                return SimpleNamespace(nfev=nfev, fun=min(losses), success=success)
            row = baseline.run_one(DLL, "Sphere", 0, 32, self.manifest()["InitialPopulationHash"], optimizer=invalid)
            self.assertEqual("failed", row["Status"])
            self.assertEqual(8, row["Resources"]["Spent"]["cost_units"])
            self.assertEqual(8, row["IndependentEvaluatorCalls"])

    def test_optimizer_exception_retains_observed_and_unknown_work(self):
        def broken(objective, bounds, **kwargs):
            objective(kwargs["init"][0])
            raise RuntimeError("simulated optimizer failure")
        row = baseline.run_one(DLL, "Sphere", 0, 32, self.manifest()["InitialPopulationHash"], optimizer=broken)
        self.assertEqual("failed", row["Status"])
        self.assertEqual(1, row["Resources"]["Spent"]["cost_units"])
        self.assertTrue(row["UnknownWork"])
        self.assertIsNone(row["IndependentEvaluatorCalls"])

    def test_wrong_initial_identity_or_binary_revision_fails_before_dispatch(self):
        for identity, revision in [("0" * 64, None), (self.manifest()["InitialPopulationHash"], "0" * 40)]:
            row = baseline.run_one(DLL, "Sphere", 0, 32, identity, source_revision=revision)
            self.assertEqual("failed", row["Status"])
            self.assertEqual(0, row["ControllerDispatches"])

    def test_service_rejects_invalid_coordinates_and_oversized_requests(self):
        for value in ([0.5] * 7, [2.0] * 8, [0.5] * 8, "x" * 4097):
            bridge = baseline.Bridge(DLL, "Sphere", 0, 8)
            try:
                bridge.receive()
                with self.assertRaises(RuntimeError):
                    bridge.request(value)
                self.assertEqual(0, bridge.last["EvaluatorCalls"])
                self.assertEqual(0, bridge.last["Resources"]["Spent"]["cost_units"])
            finally:
                bridge.close()

    def test_service_hard_cap_and_explicit_finish(self):
        for overrun in (False, True):
            bridge = baseline.Bridge(DLL, "Sphere", 0, 8)
            try:
                manifest = bridge.receive()
                for units in manifest["InitialUnits"]:
                    bridge.request(units)
                if overrun:
                    with self.assertRaises(RuntimeError):
                        bridge.request(manifest["InitialUnits"][0])
                    summary = bridge.last
                else:
                    summary = bridge.request(None)
                self.assertEqual(8, summary["EvaluatorCalls"])
                self.assertEqual(8, summary["Resources"]["Spent"]["cost_units"])
                self.assertEqual(0, summary["Resources"]["Spent"]["proposal_calls"])
            finally:
                bridge.close()

    def test_campaign_requires_every_scheduled_row_and_identical_population(self):
        campaign = dict(SchemaVersion=2, Protocol="numeric-development-v3-diagonal-cma", Partition="development",
                        SourceRevision="a" * 40, Methods=baseline.CORE_METHODS, TaskCount=4, Dimensions=8,
                        InitialPopulation=8, Seeds=1, Budget=32,
                        Runs=[dict(Task=task, Method=method, Seed=0, InitialPopulationHash="b" * 64)
                              for task in baseline.TASKS for method in baseline.CORE_METHODS])
        self.assertEqual(4, len(baseline.validate_campaign(campaign)))
        for change in (lambda c: c["Runs"].pop(), lambda c: c["Runs"].__setitem__(0, c["Runs"][1]),
                       lambda c: c["Runs"][0].__setitem__("InitialPopulationHash", "c" * 64),
                       lambda c: c.__setitem__("Budget", 31), lambda c: c.__setitem__("Seeds", True)):
            invalid = copy.deepcopy(campaign)
            change(invalid)
            with self.assertRaises(ValueError):
                baseline.validate_campaign(invalid)


if __name__ == "__main__":
    unittest.main()
