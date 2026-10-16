"""US-11 (#29): the committed AutoML evidence, regenerated rather than quoted."""
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import automl_tuning as automl  # noqa: E402

EVIDENCE = ROOT / "benchmarks" / "evidence" / "automl-us11" / "0354e33"


class CommittedAutoMlEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = json.loads((EVIDENCE / "rows.json").read_text(encoding="utf-8"))
        cls.committed = (EVIDENCE / "summary.json").read_text(encoding="utf-8")

    def test_the_summary_regenerates_byte_for_byte_from_the_rows(self):
        self.assertEqual(self.committed, automl.render(automl.analyse(self.report)))

    def test_both_searchers_were_given_the_same_number_of_model_fits(self):
        # An unequal budget would make the comparison meaningless, so the analysis refuses it and
        # this asserts the committed rows actually satisfy that.
        for row in self.report["Rows"]:
            with self.subTest(seed=row["Seed"]):
                self.assertEqual(row["GridFits"], row["EngineFits"])
                self.assertEqual(self.report["Budget"], row["EngineFits"])

    def test_every_seed_has_its_own_sealed_test_split(self):
        seals = [row["TestSeal"] for row in self.report["Rows"]]
        self.assertEqual(len(seals), len(set(seals)), "two seeds sharing a split are not independent")
        for seal in seals:
            self.assertRegex(seal, r"^[0-9a-f]{64}$")

    def test_the_engine_matches_grid_search_rather_than_beating_it(self):
        # The honest reading: the interval straddles 1.0 on a two-parameter space, where an
        # exhaustive grid is near-optimal. A later change that claims a win has to move this test.
        summary = automl.analyse(self.report)
        ratio = summary["TestMseRatio"]
        self.assertLess(ratio["LowerBound95"], 1.0)
        self.assertGreater(ratio["UpperBound95"], 1.0)
        self.assertEqual(summary["Seeds"],
                         summary["SeedsEngineBetter"] + summary["SeedsGridBetter"],
                         "no seed is an exact tie, so the split of wins is the whole story")

    def test_a_ratio_that_disagrees_with_its_two_errors_is_refused(self):
        rows = [dict(row) for row in self.report["Rows"]]
        rows[0]["TestMseRatio"] = 0.5
        with self.assertRaisesRegex(ValueError, "not the one the two errors produce"):
            automl.analyse(dict(self.report, Rows=rows))

    def test_unequal_budgets_are_refused(self):
        rows = [dict(row) for row in self.report["Rows"]]
        rows[0]["GridFits"] = rows[0]["EngineFits"] * 2
        with self.assertRaisesRegex(ValueError, "same number of model fits"):
            automl.analyse(dict(self.report, Rows=rows))


if __name__ == "__main__":
    unittest.main()
