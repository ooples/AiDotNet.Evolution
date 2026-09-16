import copy
import unittest
import gzip
import json
from pathlib import Path

from analyze_islands import analyze, load_verified, verify_raw


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
                             ("Spent", {"cost_units": 31}), ("FinalQuality", float("nan")), ("FinalQuality", 1.1), ("FinalQuality", 0.49)):
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


class IslandRawEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = Path(__file__).resolve().parents[1] / "evidence/adaptive-islands/a6199ee/summary.json"
        cls.summary = json.loads(cls.path.read_text())
        cls.raw = json.loads(gzip.decompress((cls.path.parent / "raw.json.gz").read_bytes()))

    def test_historical_raw_chain_and_analysis_recompute(self):
        result = analyze(load_verified(self.path))
        self.assertEqual(json.loads((self.path.parent / "analysis.json").read_text()), result)

    def test_summary_cannot_lie_with_unchanged_raw_hashes(self):
        report = copy.deepcopy(self.summary)
        report["Runs"][0]["FinalQuality"] = 0.75
        with self.assertRaisesRegex(ValueError, "Raw summary mismatch"):
            verify_raw(report, self.raw)

    def test_raw_objective_curve_receipts_and_identity_are_recomputed(self):
        for corruption in ("quality", "curve", "missing", "duplicate", "negative-charge", "reserved"):
            with self.subTest(corruption=corruption):
                raw = copy.deepcopy(self.raw)
                run = raw["Runs"][0]
                if corruption == "quality":
                    run["Measurements"][0]["Quality"] = 0.9
                elif corruption == "curve":
                    run["Measurements"][0]["BestQuality"] = 0.9
                elif corruption == "missing":
                    run["Measurements"].pop()
                elif corruption == "duplicate":
                    run["Measurements"][1]["EvaluationId"] = run["Measurements"][0]["EvaluationId"]
                elif corruption == "negative-charge":
                    run["Resources"]["Receipts"][0]["Charged"]["Amounts"]["cost_units"] = -1
                    run["Resources"]["Receipts"][1]["Charged"]["Amounts"]["cost_units"] = 3
                else:
                    run["Resources"]["Reserved"]["cost_units"] = 1
                with self.assertRaises(ValueError):
                    verify_raw(self.summary, raw)

    def test_invalid_timing_is_rejected(self):
        for value in (-1, 0, float("nan"), float("inf"), True):
            report = IslandAnalysisTests().report()
            report["Protocol"] = "fixed-adaptive-islands-pilot-v2"
            for row in report["Runs"]:
                row["ElapsedMilliseconds"] = 1
            report["Runs"][0]["ElapsedMilliseconds"] = value
            with self.assertRaises(ValueError):
                analyze(report)


if __name__ == "__main__":
    unittest.main()
