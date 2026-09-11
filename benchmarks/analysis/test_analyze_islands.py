import copy
import unittest

from analyze_islands import analyze


class IslandAnalysisTests(unittest.TestCase):
    def report(self):
        runs = []
        for task in ("ShiftedQuadratic1", "SeparatedBasins1"):
            for method in ("Uniform", "Adaptive", "UniformRestart", "AdaptiveRestart"):
                runs.append(dict(Task=task, Method=method, Seed=0, Status="completed", FinalQuality=0.5,
                                 EvaluatorCalls=32, Spent={"cost_units": 32}, Unknown=0, MaximumViolated=False,
                                 StateHash="state", ReplayStateHash="state", ResumeStateHash="state", InitialPopulationHash="seed"))
        return dict(Protocol="fixed-adaptive-islands-pilot-v1", AllValid=True, Seeds=1, EvaluatorCallCap=32,
                    SourceRevision="a" * 40, Runs=runs)

    def test_identical_methods_have_zero_paired_differences(self):
        result = analyze(self.report())
        self.assertEqual(8, len(result["Contrasts"]))
        self.assertTrue(all(row["MeanDifference"] == 0 and row["PairedPercentile95"] == [0, 0] for row in result["Contrasts"]))

    def test_missing_and_duplicate_runs_fail(self):
        report = self.report()
        report["Runs"].pop()
        with self.assertRaises(ValueError):
            analyze(report)
        report = self.report()
        report["Runs"][1] = copy.deepcopy(report["Runs"][0])
        with self.assertRaises(ValueError):
            analyze(report)

    def test_failures_and_invalid_accounting_cannot_disappear(self):
        for field, value in (("Status", "failed"), ("EvaluatorCalls", 31), ("Unknown", 1), ("MaximumViolated", True),
                             ("Spent", {"cost_units": 31}), ("FinalQuality", float("nan"))):
            with self.subTest(field=field):
                report = self.report()
                report["Runs"][0][field] = value
                with self.assertRaises(ValueError):
                    analyze(report)

    def test_unpaired_or_unreplayed_runs_fail(self):
        for field, value in (("InitialPopulationHash", "different"), ("ReplayStateHash", "different"),
                             ("ResumeStateHash", "different"), ("ResumeStateHash", None)):
            with self.subTest(field=field):
                report = self.report()
                report["Runs"][0][field] = value
                with self.assertRaises(ValueError):
                    analyze(report)


if __name__ == "__main__":
    unittest.main()
