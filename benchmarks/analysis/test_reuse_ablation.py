"""US-18 (#36): the committed reuse-ablation evidence, regenerated rather than quoted."""
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import reuse_ablation as ablation  # noqa: E402

EVIDENCE = ROOT / "benchmarks" / "evidence" / "reuse-us18" / "0354e33"


class CommittedReuseEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bundle = json.loads((EVIDENCE / "rows-compact.json").read_text(encoding="utf-8"))
        cls.committed = (EVIDENCE / "summary.json").read_text(encoding="utf-8")

    def test_the_summary_regenerates_byte_for_byte_from_the_rows(self):
        self.assertEqual(self.committed, ablation.render(ablation.analyse(self.bundle)))

    def test_novelty_and_reuse_suppress_proposals_an_interval_away_from_zero(self):
        summary = ablation.analyse(self.bundle)
        self.assertGreater(summary["SuppressedProposalRatePoints"]["LowerBound95"], 0)
        self.assertGreater(summary["NoveltySuppressedRate"], summary["BaselineSuppressedRate"])

    def test_quality_is_reported_as_non_inferior_rather_than_better(self):
        # The interval straddles zero. Reading it as an improvement is the error this pins.
        summary = ablation.analyse(self.bundle)
        quality = summary["QualityDifference"]
        self.assertLess(quality["LowerBound95"], 0)
        self.assertGreater(quality["UpperBound95"], 0)

    def test_the_calls_to_target_claim_is_not_supported_by_this_evidence(self):
        # US-18's second metric row wants fewer calls to target. The measured point estimate is in the
        # opposite direction and its interval straddles zero, so the row must not read as met. If a
        # later change actually earns it, this test fails and the claim gets re-read deliberately.
        summary = ablation.analyse(self.bundle)
        calls = summary["CallsToTargetDifference"]
        self.assertIsNotNone(calls)
        self.assertGreater(calls["Mean"], 0, "the mechanism did not reach the target in fewer calls here")
        self.assertLess(calls["LowerBound95"], 0, "and the interval includes no difference at all")
        self.assertEqual(34, calls["Pairs"], "pairs where both arms reached the target")

    def test_the_bundle_records_what_it_was_reduced_from(self):
        self.assertEqual(64, len(self.bundle["SourceSha256"]))
        self.assertGreater(self.bundle["SourceBytes"], 10_000_000, "the full campaign output is excluded by size")
        self.assertEqual(96, len(self.bundle["Rows"]), "two arms x 48 pairs")

    def test_a_missing_arm_is_refused_rather_than_half_analysed(self):
        half = dict(self.bundle, Rows=[row for row in self.bundle["Rows"] if row["Configuration"] == "baseline"])
        with self.assertRaisesRegex(ValueError, "both the baseline and the novelty arm"):
            ablation.analyse(half)
        dropped = dict(self.bundle, Rows=self.bundle["Rows"][1:])
        with self.assertRaisesRegex(ValueError, "missing an arm"):
            ablation.analyse(dropped)


if __name__ == "__main__":
    unittest.main()
