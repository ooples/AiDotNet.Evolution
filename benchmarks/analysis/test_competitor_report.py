"""The lint is the whole value of the comparison, so every refusal it makes is exercised here.

A comparison report is the easiest document in a repository to write dishonestly: every number in it
is true and the selection does the lying. `competitor_report.lint` exists to refuse the three
selections that would flatter us most, and a refusal nothing tests is a comment. So each case below
takes the REAL report built from the committed bundles, changes exactly one thing, and requires the
lint to catch it - and the first test requires the unmodified report to pass, because a lint that
refused everything would satisfy all the others.

The document is checked the same way: `render` is re-run and compared with the committed markdown, so
the published table cannot be edited into saying something the bundles do not.
"""
import copy
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))

import competitor_report as cr

CENSUS = "benchmarks/evidence/competitor-comparison/77756eb/openevolve-config-census.json"
HEADROOM = "benchmarks/evidence/alphaevolve-headroom/77756eb/headroom.json"
DOCUMENT = ROOT / "docs/COMPETITOR_COMPARISON.md"


def build():
    census = json.loads((ROOT / CENSUS).read_text(encoding="utf-8"))
    census["Evidence"] = CENSUS
    return cr.build(ROOT, census, HEADROOM)


class CompetitorReportTests(unittest.TestCase):
    def setUp(self):
        self.report = build()

    def find(self, dimension):
        return next(item for item in self.report["Rows"] if item["Dimension"] == dimension)

    def test_the_committed_evidence_passes_the_lint(self):
        # Without this, every refusal below is satisfied by a lint that refuses everything.
        self.assertEqual(0, cr.lint(self.report, ROOT))
        self.assertEqual(0, self.report["Unsupported"])

    def test_every_row_names_a_file_that_is_really_there(self):
        for item in self.report["Rows"]:
            with self.subTest(row=item["Dimension"]):
                self.assertTrue((ROOT / item["Evidence"]).exists(), item["Evidence"])
                self.assertTrue(str(item["Reading"]).strip())
                self.assertIn(item["Verdict"], cr.VERDICTS)

    def test_evidence_outside_the_tree_is_refused(self):
        report = copy.deepcopy(self.report)
        report["Rows"][0]["Evidence"] = "benchmarks/evidence/does-not-exist.json"
        with self.assertRaises(ValueError) as caught:
            cr.lint(report, ROOT)
        self.assertIn("is not in the tree", str(caught.exception))

    def test_the_quality_row_cannot_be_promoted_without_a_superior_endpoint(self):
        # The pilot's two endpoints are both inconclusive. This is the single check between that and
        # a v1 claim of beating OpenEvolve on search quality.
        row = self.find("Search quality, head to head")
        self.assertEqual(cr.INCONCLUSIVE, row["Verdict"])
        self.assertTrue(row["Endpoints"])
        self.assertFalse(any(endpoint["Superior"] for endpoint in row["Endpoints"]))
        report = copy.deepcopy(self.report)
        promoted = next(item for item in report["Rows"]
                        if item["Dimension"] == "Search quality, head to head")
        promoted["Verdict"] = cr.AHEAD
        report["Counts"][cr.AHEAD] += 1
        report["Counts"][cr.INCONCLUSIVE] -= 1
        with self.assertRaises(ValueError) as caught:
            cr.lint(report, ROOT)
        self.assertIn("no endpoint it cites is marked superior", str(caught.exception))

    def test_a_floor_study_cannot_be_read_as_a_win(self):
        # "the floor is 10% behind AlphaEvolve" must never become "we are 10% behind AlphaEvolve".
        row = self.find("Distance to the published constructions")
        self.assertTrue(row["FloorStudy"])
        self.assertEqual(cr.UNMEASURED, row["Verdict"])
        report = copy.deepcopy(self.report)
        promoted = next(item for item in report["Rows"] if item["FloorStudy"])
        promoted["Verdict"] = cr.AHEAD
        report["Counts"][cr.AHEAD] += 1
        report["Counts"][cr.UNMEASURED] -= 1
        with self.assertRaises(ValueError) as caught:
            cr.lint(report, ROOT)
        self.assertIn("floor study cannot be", str(caught.exception))

    def test_a_win_needs_evidence_that_separates_the_arms(self):
        report = copy.deepcopy(self.report)
        row = next(item for item in report["Rows"]
                   if item["Dimension"] == "Engine overhead per evaluation")
        # Overlap the two ranges and widen the interval past parity: the numbers stay, the separation
        # goes, and the verdict must go with it.
        row["TheirsRange"] = [40.0, 31145.39]
        row["Interval"] = [0.5, 1.5]
        with self.assertRaises(ValueError) as caught:
            cr.lint(report, ROOT)
        self.assertIn("does not separate the arms", str(caught.exception))

    def test_a_win_over_nobody_is_refused(self):
        report = copy.deepcopy(self.report)
        row = next(item for item in report["Rows"]
                   if item["Dimension"] == "Engine overhead per evaluation")
        row["Theirs"] = None
        with self.assertRaises(ValueError) as caught:
            cr.lint(report, ROOT)
        self.assertIn("no competitor figure", str(caught.exception))

    def test_a_row_claiming_no_competitor_may_not_quote_one(self):
        report = copy.deepcopy(self.report)
        row = next(item for item in report["Rows"] if item["Verdict"] == cr.MEASURED)
        row["Theirs"] = "something they published"
        with self.assertRaises(ValueError) as caught:
            cr.lint(report, ROOT)
        self.assertIn("but one is quoted", str(caught.exception))

    def test_the_headline_counts_cannot_drift_from_the_rows(self):
        report = copy.deepcopy(self.report)
        report["Counts"][cr.AHEAD] += 1
        with self.assertRaises(ValueError) as caught:
            cr.lint(report, ROOT)
        self.assertIn("count says", str(caught.exception))

    def test_the_parity_claim_is_derived_from_the_census_and_not_asserted(self):
        census = json.loads((ROOT / CENSUS).read_text(encoding="utf-8"))
        self.assertEqual([], census["Unclassified"],
                         "an unclassified upstream option is a parity gap, not a rounding error")
        self.assertEqual(102, len(census["UpstreamOptions"]))
        self.assertTrue(census["Ref"], "a census that cannot say which OpenEvolve it read is worth little")
        row = self.find("Configuration surface")
        self.assertEqual(cr.PARITY, row["Verdict"])
        self.assertIn(str(len(census["UpstreamOptions"])), row["Theirs"])

    def test_an_unclassified_upstream_option_turns_parity_into_behind(self):
        census = json.loads((ROOT / CENSUS).read_text(encoding="utf-8"))
        census["Evidence"] = CENSUS
        census["Unclassified"] = ["llm.some_new_option"]
        report = cr.build(ROOT, census, HEADROOM)
        self.assertEqual(cr.BEHIND, next(item for item in report["Rows"]
                                         if item["Dimension"] == "Configuration surface")["Verdict"])

    def test_the_published_document_is_the_rendered_one(self):
        self.assertTrue(DOCUMENT.is_file(), "the comparison is published or it is not a report")
        self.assertEqual(cr.render(self.report), DOCUMENT.read_text(encoding="utf-8"),
                         "docs/COMPETITOR_COMPARISON.md was edited by hand - regenerate it instead")

    def test_the_document_quotes_the_numbers_its_rows_carry(self):
        text = DOCUMENT.read_text(encoding="utf-8")
        for item in self.report["Rows"]:
            with self.subTest(row=item["Dimension"]):
                self.assertIn(item["Reading"], text)
                self.assertIn(item["Evidence"], text)


if __name__ == "__main__":
    unittest.main()