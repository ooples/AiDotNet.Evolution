import copy
import gzip
import hashlib
import json
from pathlib import Path
import unittest

from analyze_surrogate import METHODS, TASKS, analyze


def campaign():
    return {"SchemaVersion": 2, "Kind": "compact-surrogate-example-evidence", "Protocol": "synthetic-surrogate-example-v3-cost-ratios",
            "SourceRevision": "a" * 40, "Seeds": 2, "BaseCostCap": 32, "EvaluationUnitCosts": [0.1, 1, 10],
            "Runs": [{"Task": task, "Method": method, "Seed": seed, "EvaluationUnitCost": price, "CostCap": 32 * price,
                      "InitialPopulationHash": str(seed) * 64, "Status": "completed", "FinalLoss": 0.25 if method == "ValidatedPool" else 0.5,
                      "EvaluatorCalls": 8, "MeasurementCount": 8, "StageCostUnits": {"Evaluation": 8 * price, "Proposal": 0.1},
                      "Resources": {"Unknown": 0, "MaximumViolated": False, "Spent": {"cost_units": 8 * price + 0.1},
                                    "Reserved": {"cost_units": 0}, "Admitted": 9, "Settled": 9, "DroppedReceipts": 0}}
                     for task in TASKS for price in (0.1, 1, 10) for method in METHODS for seed in range(2)]}


class SurrogateAnalysisTests(unittest.TestCase):
    def test_checked_in_evidence_hash_chain_survives_git_checkout(self):
        evidence = Path(__file__).resolve().parents[1] / "evidence" / "surrogates" / "c770896"
        summary_bytes = (evidence / "summary.json").read_bytes()
        summary = json.loads(summary_bytes)
        analysis = json.loads((evidence / "analysis.json").read_bytes())
        self.assertEqual(analysis["InputSummarySha256"], hashlib.sha256(summary_bytes).hexdigest())
        compressed = evidence / "raw.json.gz"
        self.assertEqual(summary["RawGzipSha256"], hashlib.sha256(compressed.read_bytes()).hexdigest())
        raw_hash = hashlib.sha256(); size = 0
        with gzip.open(compressed, "rb") as raw:
            while block := raw.read(65536):
                size += len(block)
                self.assertLessEqual(size, 64 * 1024 * 1024)
                raw_hash.update(block)
        self.assertEqual(summary["FullTraceSha256"], raw_hash.hexdigest())
        self.assertEqual(38307179, size)

    def test_pairs_are_stratified_by_tariff_and_task(self):
        result = analyze(campaign())
        self.assertEqual(48, result["ScheduledRuns"])
        self.assertEqual([], result["FailedOrInvalidRuns"])
        self.assertEqual(18, len(result["Comparisons"]))
        for comparison in result["Comparisons"]:
            self.assertEqual(-0.25, comparison["MeanPairedLossDifference"])
            self.assertEqual([-0.25, -0.25], comparison["PairedPercentile95Interval"])

    def test_missing_duplicate_or_mismatched_pair_is_rejected(self):
        for change in (lambda rows: rows.pop(), lambda rows: rows.__setitem__(0, copy.deepcopy(rows[1])),
                       lambda rows: rows[0].update(InitialPopulationHash="f" * 64), lambda rows: rows[0].update(CostCap=999)):
            report = campaign(); change(report["Runs"])
            with self.assertRaises(ValueError):
                analyze(report)

    def test_failed_and_invalid_rows_are_penalized_not_dropped(self):
        for change in (lambda row: row.update(Status="failed"), lambda row: row.update(FinalLoss=None),
                       lambda row: row["Resources"].update(Unknown=1), lambda row: row["StageCostUnits"].update(Evaluation=0),
                       lambda row: row.update(MeasurementCount=0), lambda row: row.update(Resources=None),
                       lambda row: row["Resources"].update(Settled=8), lambda row: row["Resources"].update(DroppedReceipts=1),
                       lambda row: row.update(EvaluatorCalls=None), lambda row: row["Resources"]["Spent"].update(cost_units=float("nan"))):
            report = campaign(); change(report["Runs"][0])
            result = analyze(report)
            self.assertEqual(48, result["ScheduledRuns"])
            self.assertEqual(1, len(result["FailedOrInvalidRuns"]))
            self.assertEqual(8, result["FailedOrInvalidRuns"][0]["PenaltyLoss"])
            self.assertEqual(2, result["Comparisons"][0]["PairedSeeds"])
            json.dumps(result, allow_nan=False)

    def test_unknown_protocol_fractional_seed_and_nonfinite_budget_rejected(self):
        for field, value in (("Protocol", "future"), ("Seeds", 1.5), ("BaseCostCap", float("nan")),
                             ("EvaluationUnitCosts", [0.1, 10]), ("SourceRevision", "short")):
            report = campaign(); report[field] = value
            with self.assertRaises(ValueError):
                analyze(report)


if __name__ == "__main__":
    unittest.main()
