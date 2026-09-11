import copy
import gzip
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from analyze_fidelity import analyze


def campaign():
    rows = []
    for task in ("Aligned", "LateImprover"):
        for method in ("GreedyHalving", "ExploratoryHalving"):
            for seed in range(2):
                quality = 0.75 if method == "ExploratoryHalving" else 0.5
                batches, receipts = [], [{"Charged": {"Amounts": {"cost_units": 0.08}}}]
                for index, genome in enumerate(list(range(8)) + [7, 6, 5, 4, 7, 6, 7, 6]):
                    level = 1 if index < 8 else 3 if index < 12 else 9
                    cost = 1 if index < 8 else 2 if index < 12 else 6 if index < 14 else 9
                    samples = [{"Context": {"SampleIdentity": f"{task}-{method}-{seed}-{index}-{rep}"}, "Status": "Completed",
                                "Quality": quality, "UnknownCost": False, "ChargedCostUnits": cost} for rep in range(2)]
                    receipts.extend({"Charged": {"Amounts": {"cost_units": cost}}} for _ in range(2))
                    batches.append({"Candidate": {"Id": f"candidate-{genome}"}, "Purpose": "Search" if index < 14 else "Confirmation",
                                    "Level": {"ResourceLevel": level}, "ResumedFromSampleIdentities": [None, None],
                                    "Measurements": {"Samples": samples, "MeanQuality": quality}})
                rows.append({"Task": task, "Method": method, "Seed": seed, "Status": "completed", "InitialPopulationHash": str(seed) * 64,
                             "BestConfirmedQuality": quality, "BestConfirmedGenome": "candidate-6", "EvaluatorCalls": 32, "ResumedCalls": 12,
                             "ConfirmationCalls": 4, "ExecutedSteps": 92,
                             "Report": {"IsComplete": True, "StopReason": "Completed", "InitialCandidateIds": [f"candidate-{i}" for i in range(8)],
                                        "Batches": batches, "ChargedCostUnits": 92, "Resources": {"Spent": {"cost_units": 92.08}, "Reserved": {"cost_units": 0},
                                                                                               "Unknown": 0, "MaximumViolated": False, "DroppedReceipts": 0,
                                                                                               "Admitted": 33, "Settled": 33, "Receipts": receipts}}})
    return {"Protocol": "synthetic-fidelity-example-v1", "Seeds": 2, "CostCap": 100, "AssemblyVersion": "1.0+" + "a" * 40, "Runs": rows}


class FidelityAnalysisTests(unittest.TestCase):
    def test_paired_effect_and_repeatability(self):
        result = analyze(campaign())
        self.assertEqual(8, result["ScheduledRuns"]); self.assertEqual(0, result["FailedOrInvalidRuns"])
        self.assertEqual(result, analyze(campaign()))
        for comparison in result["Comparisons"]:
            self.assertEqual(0.25, comparison["MeanPairedQualityDifference"])
            self.assertEqual([0.25, 0.25], comparison["PairedPercentile95Interval"])

    def test_missing_duplicate_changed_cap_or_mismatched_cohort_rejected(self):
        for change in (lambda report: report["Runs"].pop(), lambda report: report["Runs"].__setitem__(0, copy.deepcopy(report["Runs"][1])),
                       lambda report: report.update(CostCap=101), lambda report: report["Runs"][0].update(InitialPopulationHash="f" * 64)):
            report = campaign(); change(report)
            with self.assertRaises(ValueError): analyze(report)

    def test_failed_unknown_unconfirmed_and_invalid_numeric_evidence_is_penalized(self):
        for change in (lambda row: row.update(Status="failed"), lambda row: row.update(BestConfirmedQuality=float("nan")),
                       lambda row: row.update(EvaluatorCalls=float("nan")), lambda row: row["Report"].update(IsComplete=False),
                       lambda row: row["Report"]["Resources"].update(Unknown=1), lambda row: row["Report"]["Resources"].update(Settled=32),
                       lambda row: row.update(BestConfirmedGenome="unmeasured"),
                       lambda row: row["Report"].update(InitialCandidateIds=None),
                       lambda row: row["Report"]["InitialCandidateIds"].__setitem__(0, "candidate-1"),
                       lambda row: row["Report"]["Batches"][-1]["Measurements"].update(MeanQuality=0.9),
                       lambda row: row["Report"]["Batches"][-1]["ResumedFromSampleIdentities"].__setitem__(0, "search-token"),
                       lambda row: row["Report"]["Resources"]["Spent"].update(cost_units=float("nan"))):
            report = campaign(); change(report["Runs"][0]); result = analyze(report)
            self.assertEqual(8, result["ScheduledRuns"]); self.assertEqual(1, result["FailedOrInvalidRuns"])
            self.assertEqual(0, result["Runs"][0]["PenalizedQuality"])
            json.dumps(result, allow_nan=False)

    def test_duplicate_samples_or_unaccounted_receipts_cannot_receive_quality_credit(self):
        for change in (lambda row: row["Report"]["Batches"][1]["Measurements"]["Samples"].__setitem__(0, copy.deepcopy(row["Report"]["Batches"][0]["Measurements"]["Samples"][0])),
                       lambda row: row["Report"]["Resources"]["Receipts"].pop()):
            report = campaign(); change(report["Runs"][0]); result = analyze(report)
            self.assertEqual(1, result["FailedOrInvalidRuns"])

    def test_cli_preserves_raw_bytes_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as scratch:
            source = Path(scratch) / "campaign.json"; output = Path(scratch) / "evidence"
            source.write_text(json.dumps(campaign()), encoding="utf-8")
            command = [sys.executable, str(Path(__file__).with_name("analyze_fidelity.py")), str(source), str(output)]
            self.assertEqual(0, subprocess.run(command, capture_output=True).returncode)
            raw = source.read_bytes(); packed = (output / "raw.json.gz").read_bytes()
            result = json.loads((output / "analysis.json").read_text(encoding="utf-8"))
            self.assertEqual(raw, gzip.decompress(packed))
            self.assertEqual(hashlib.sha256(raw).hexdigest(), result["FullTraceSha256"])
            self.assertEqual(hashlib.sha256(packed).hexdigest(), result["RawGzipSha256"])
            self.assertNotEqual(0, subprocess.run(command, capture_output=True).returncode)
            self.assertEqual(packed, (output / "raw.json.gz").read_bytes())


if __name__ == "__main__":
    unittest.main()
