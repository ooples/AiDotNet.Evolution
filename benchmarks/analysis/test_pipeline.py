import copy
import gzip
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from analyze_pipeline import METHODS, PROFILES, TASKS, analyze, execution_valid, verify_evidence


def fixture(seeds=2, repeats=2):
    live = dict(Status="completed", Error=None, ProposalResponses=[dict(Generation=i) for i in range(1, 25)],
                EvaluationResponses=[dict(EvaluationId=i) for i in range(32)],
                Counters=dict(Proposals=32, EvaluationAttempts=32, CompletedEvaluations=32), ObservedGenerations=list(range(1, 25)),
                BestQuality=0.75, ElapsedSeconds=1, ProcessCpuSeconds=0.1, ProcessAllocatedBytes=100,
                PhysicalProposalCalls=24, PhysicalEvaluationCalls=32, ReplayedProposalCalls=0, ReplayedEvaluationCalls=0,
                Resources=dict(Spent=dict(cost_units=38.08), Reserved=dict(cost_units=0), Unknown=0, Admitted=1, Settled=1,
                               DroppedReceipts=0, MaximumViolated=False, Receipts=[dict(OperationId="work", Charged=dict(Amounts=dict(cost_units=38.08)))]),
                Pipeline=None, RunId="fixture", StateHash="a" * 64, LogicalEvidenceSha256="b" * 64, InitialPopulationHash="c" * 64)
    rows = []
    for task in TASKS:
        for profile in PROFILES:
            for method in METHODS:
                for seed in range(seeds):
                    for repeat in range(repeats):
                        measured = copy.deepcopy(live)
                        if method.startswith("Pipeline"):
                            measured["Pipeline"] = dict(RunId="fixture", FirstEvaluationId=0, AbortedWaves=0, WaveSize=8,
                                                        ProposalQueueCapacity=2, EvaluationQueueCapacity=2, ProposalQueuePeak=2,
                                                        EvaluationQueuePeak=2, ProposalRunningPeak=1, ProposalWorkers=4 if method == "PipelineConcurrent" else 1,
                                                        EvaluationWorkers=4, EvaluationRunningPeak=4, IsScheduleComplete=True,
                                                        DroppedScheduleRecords=0, Schedule=list(range(64)))
                        replay = copy.deepcopy(measured)
                        replay.update(PhysicalProposalCalls=0, PhysicalEvaluationCalls=0, ReplayedProposalCalls=24, ReplayedEvaluationCalls=32)
                        rows.append(dict(Task=task, Profile=profile, Method=method, Seed=seed, TimingRepetition=repeat,
                                         Status="completed", ReplayMatches=True, Live=measured, Replay=replay))
    return dict(Protocol="authored-proposal-pipeline-v1", AssemblyVersion="1+" + "a" * 40, CoreAssemblyVersion="1+" + "a" * 40,
                AssemblySha256="b" * 64, CoreAssemblySha256="c" * 64, Seeds=seeds, TimingRepetitions=repeats,
                ProposalCap=32, EvaluationAttemptCap=32, CostCap=40, Runs=rows, WarmupRuns=[copy.deepcopy(live) for _ in range(4)])


class PipelineAnalysisTests(unittest.TestCase):
    def test_complete_pairs_and_nested_repetition_clusters(self):
        report = analyze(fixture(), "a" * 40)
        self.assertEqual(report["Valid"], 96)
        self.assertEqual(report["SeedClusters"], 2)
        self.assertFalse(report["ConfirmatoryEligible"])
        for pair in report["Comparisons"]:
            self.assertEqual(pair["QualityInterval"], [0, 0])
            self.assertEqual(pair["SpeedRatioInterval"], [1, 1])
            self.assertEqual(pair["SeedClusters"], 2)

    def test_failed_run_is_zero_quality_and_suppresses_affected_timing(self):
        campaign = fixture(1, 1)
        row = next(row for row in campaign["Runs"] if row["Method"] == "PipelineConcurrent")
        row["Status"] = "failed"
        report = analyze(campaign, "a" * 40)
        self.assertEqual(report["Scheduled"], 24)
        self.assertEqual(report["Valid"], 23)
        pair = report["Comparisons"][0]
        self.assertEqual(pair["MeanPairedQualityDifference"], -0.375)
        self.assertIsNone(pair["SpeedRatioInterval"])

    def test_false_replay_receipt_and_pipeline_claims_are_invalid(self):
        for corrupt in (
            lambda r: r["Replay"].update(PhysicalEvaluationCalls=1),
            lambda r: r["Replay"]["EvaluationResponses"][0].update(Quality=0.9),
            lambda r: r["Live"]["Resources"].update(Unknown=1),
            lambda r: r["Live"]["Resources"]["Receipts"][0]["Charged"]["Amounts"].update(cost_units=0),
            lambda r: r["Live"].update(Pipeline=None),
            lambda r: r["Live"]["Pipeline"].update(AbortedWaves=1),
        ):
            campaign = fixture(1, 1)
            row = next(row for row in campaign["Runs"] if row["Method"] == "PipelineConcurrent")
            corrupt(row)
            self.assertEqual(analyze(campaign, "a" * 40)["Valid"], 23)

    def test_source_cohort_grid_and_bounds_fail_closed(self):
        for corrupt in (
            lambda c: c.update(AssemblyVersion="unpinned"),
            lambda c: c.update(CoreAssemblyVersion="other+" + "b" * 40),
            lambda c: c.update(Seeds=1000),
            lambda c: c["Runs"].pop(),
            lambda c: c["Runs"].__setitem__(0, copy.deepcopy(c["Runs"][1])),
            lambda c: c["Runs"][0]["Live"].update(InitialPopulationHash="different"),
        ):
            campaign = fixture(1, 1); corrupt(campaign)
            with self.assertRaises(ValueError):
                analyze(campaign, "a" * 40)

    def test_warmup_failure_retains_evidence_without_timing_eligibility(self):
        campaign = fixture(1, 1); campaign["WarmupRuns"][0]["Status"] = "failed"
        report = analyze(campaign, "a" * 40)
        self.assertEqual(report["WarmupsValid"], 3)
        self.assertTrue(all(pair["SpeedRatioInterval"] is None for pair in report["Comparisons"]))

    def test_empty_receipts_fail_validation(self):
        self.assertFalse(execution_valid({}))

    def test_retained_evidence_hashes_and_recomputed_analysis_reject_corruption(self):
        campaign = fixture(1, 1); report = analyze(campaign, "a" * 40)
        raw = json.dumps(campaign).encode(); compressed = gzip.compress(raw, mtime=0)
        report.update(InputSha256=hashlib.sha256(raw).hexdigest(), CompressedSha256=hashlib.sha256(compressed).hexdigest())
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "raw.json.gz").write_bytes(compressed)
            (root / "analysis.json").write_text(json.dumps(report))
            self.assertEqual(verify_evidence(root), report)
            for field, value in (("Valid", 0), ("InputSha256", "f" * 64), ("CompressedSha256", "f" * 64)):
                corrupt = dict(report); corrupt[field] = value
                (root / "analysis.json").write_text(json.dumps(corrupt))
                with self.assertRaises(ValueError):
                    verify_evidence(root)


if __name__ == "__main__":
    unittest.main()
