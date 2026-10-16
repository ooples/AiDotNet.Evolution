"""US-19 (#37): the committed routing evidence, regenerated rather than quoted."""
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import routing_study as routing  # noqa: E402

EVIDENCE = ROOT / "benchmarks" / "evidence" / "routing-us19" / "0354e33"


class CommittedRoutingEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bundle = json.loads((EVIDENCE / "rows-compact.json").read_text(encoding="utf-8"))
        cls.committed = (EVIDENCE / "summary.json").read_text(encoding="utf-8")

    def test_the_summary_regenerates_byte_for_byte_from_the_rows(self):
        self.assertEqual(self.committed, routing.render(routing.analyse(self.bundle)))

    def test_the_comparator_was_selected_on_development_not_on_these_rows(self):
        # The study's own claim file carries the selection and the hash of the development rows it
        # came from; copying it here is what keeps the comparator from being chosen after the fact.
        self.assertEqual({"kernel", "numeric", "program"}, set(self.bundle["BestStatic"]))
        self.assertEqual(64, len(self.bundle["DevelopmentHash"]))
        self.assertEqual(64, len(self.bundle["PlanHash"]))

    def test_quality_is_non_inferior_on_every_family(self):
        for family in routing.analyse(self.bundle)["Families"]:
            quality = family["QualityDifference"]
            with self.subTest(family=family["Family"]):
                self.assertLess(quality["LowerBound95"], 0, "the interval must straddle zero to read as parity")
                self.assertGreater(quality["UpperBound95"], 0)

    def test_the_call_reduction_target_is_not_supported_by_this_evidence(self):
        # US-19 asks for <= 0.75x the calls of the best single method. Measured: about 1.05x on two
        # families and 0.99x on the third, so the row must not read as met. If routing ever earns it,
        # this test fails and the claim is re-read deliberately.
        for family in routing.analyse(self.bundle)["Families"]:
            ratio = family["ObjectiveCallRatio"]
            with self.subTest(family=family["Family"]):
                self.assertGreater(ratio["Mean"], 0.75, "nowhere near the declared 0.75x")
                self.assertGreater(ratio["LowerBound95"], 0.9, "and its interval is nowhere near it either")

    def test_two_families_share_a_call_pattern_by_construction(self):
        # numeric and program produce identical per-seed call counts under the same seeds and budget,
        # so their ratios are identical. That is the schedule, not a copied row, and this says so.
        rows = self.bundle["Rows"]
        calls = {family: [row["ObjectiveCalls"] for row in rows
                          if row["Family"] == family and row["Method"] == routing.ROUTER]
                 for family in ("numeric", "program")}
        self.assertEqual(calls["numeric"], calls["program"])
        quality = {family: [row["Quality"] for row in rows
                            if row["Family"] == family and row["Method"] == routing.ROUTER]
                   for family in ("numeric", "program")}
        self.assertNotEqual(quality["numeric"], quality["program"], "the objectives still differ")

    def test_a_missing_arm_is_refused(self):
        dropped = dict(self.bundle, Rows=[row for row in self.bundle["Rows"] if row["Method"] != "restart"])
        with self.assertRaisesRegex(ValueError, "missing an arm"):
            routing.analyse(dropped)


if __name__ == "__main__":
    unittest.main()
