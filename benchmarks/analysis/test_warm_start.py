"""US-23 (#41): the committed warm-start evidence, regenerated rather than quoted."""
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import warm_start as warm  # noqa: E402

EVIDENCE = ROOT / "benchmarks" / "evidence" / "warm-start-us23" / "0354e33"


class CommittedWarmStartEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = json.loads((EVIDENCE / "rows.json").read_text(encoding="utf-8"))
        cls.committed = (EVIDENCE / "summary.json").read_text(encoding="utf-8")

    def test_the_summary_regenerates_byte_for_byte_from_the_rows(self):
        self.assertEqual(self.committed, warm.render(warm.analyse(self.report)))

    def test_the_warm_run_reaches_the_cold_bar_in_at_most_half_the_evaluations(self):
        summary = warm.analyse(self.report)
        self.assertLess(summary["EvaluationRatio"]["UpperBound95"], summary["Target"],
                        "the interval, not just the mean, must sit under the target")
        self.assertEqual(summary["Seeds"], summary["SeedsWithinTarget"])
        self.assertEqual(0, summary["SeedsWarmNeverReachedBar"])

    def test_seeds_that_met_the_bar_on_resume_are_counted_apart_from_the_ratio(self):
        # Averaging their zeros into the ratio would flatter the mechanism on exactly the seeds where
        # it did most work, so they are reported as a count and excluded from the interval.
        summary = warm.analyse(self.report)
        self.assertEqual(2, summary["SeedsBarMetOnResume"])
        self.assertEqual(summary["Seeds"] - summary["SeedsBarMetOnResume"], summary["EvaluationRatio"]["Seeds"])
        for row in self.report["Rows"]:
            if row["BarMetOnResume"]:
                with self.subTest(seed=row["Seed"]):
                    self.assertEqual(0, row["WarmEvaluationsAfterResume"])
                    self.assertGreaterEqual(row["PrefixBest"], row["ColdBar"] - 1e-12,
                                            "the restored state has to actually meet the bar")

    def test_the_cold_bar_cost_varies_enough_that_pairing_matters(self):
        # Cold runs reached their own final quality anywhere from 10 to 121 evaluations in, which is
        # why this is paired by seed rather than averaged across arms.
        costs = [row["ColdEvaluationsToBar"] for row in self.report["Rows"]]
        self.assertLess(min(costs), 20)
        self.assertGreater(max(costs), 100)

    def test_a_row_that_charges_for_a_bar_met_on_resume_is_refused(self):
        rows = [dict(row) for row in self.report["Rows"]]
        met = next(index for index, row in enumerate(rows) if row["BarMetOnResume"])
        rows[met]["WarmEvaluationsAfterResume"] = 7
        rows[met]["EvaluationRatio"] = 7 / rows[met]["ColdEvaluationsToBar"]
        with self.assertRaisesRegex(ValueError, "met on resume but charges evaluations"):
            warm.analyse(dict(self.report, Rows=rows))

    def test_a_ratio_that_disagrees_with_its_counts_is_refused(self):
        rows = [dict(row) for row in self.report["Rows"]]
        rows[0]["EvaluationRatio"] = 0.01
        with self.assertRaisesRegex(ValueError, "not the one the two counts produce"):
            warm.analyse(dict(self.report, Rows=rows))

    def test_tokens_are_not_claimed(self):
        self.assertFalse(warm.analyse(self.report)["TokensMeasured"])


if __name__ == "__main__":
    unittest.main()
