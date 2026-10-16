"""US-17 (#35): the committed repair-ablation evidence, regenerated rather than quoted.

The summary is a projection off `rows.json`, so it is reproduced here from the committed rows and
compared byte for byte - the same contract the pilot's `report.json` is held to. The directional
assertions below are what the story's criteria actually rest on; the absolute lift is not one of
them, because the broken-first-reply rate it depends on is an input to the harness.
"""
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import repair_ablation as ablation  # noqa: E402

EVIDENCE = ROOT / "benchmarks" / "evidence" / "repair-us17" / "0354e33"


class CommittedRepairEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.rows = json.loads((EVIDENCE / "rows.json").read_text(encoding="utf-8"))
        cls.committed = (EVIDENCE / "summary.json").read_text(encoding="utf-8")

    def test_the_summary_regenerates_byte_for_byte_from_the_rows(self):
        self.assertEqual(self.committed, ablation.render(ablation.analyse(self.rows)),
                         "summary.json must be a projection off rows.json, not an edited file")

    def test_repair_recovers_every_broken_proposal_in_this_fixture(self):
        summary = ablation.analyse(self.rows)
        self.assertEqual(1.0, summary["RepairValidRate"], "the repair reply always compiles by construction")
        self.assertLess(summary["NoRepairValidRate"], 1.0, "and without repair a broken proposal stays broken")
        self.assertGreater(summary["ValidProposalRatePoints"]["LowerBound95"], 0,
                           "the recovery must be an interval away from zero, not a point estimate")

    def test_the_extra_calls_buy_more_than_they_cost(self):
        # The non-obvious result: repair spends more calls and is still cheaper per valid candidate.
        summary = ablation.analyse(self.rows)
        self.assertGreater(summary["ModelCalls"]["LowerBound95"], 0, "repair does spend more calls")
        self.assertLess(summary["CallsPerValidCandidate"]["UpperBound95"], 0,
                        "yet calls per valid candidate falls, interval and all")

    def test_the_summary_refuses_to_claim_a_token_measurement(self):
        # The scripted client reports a fixed 100/50 tokens per call, so a token figure here would be a
        # restatement of the call count. The story's token row stays unmeasured until the gate campaign.
        summary = ablation.analyse(self.rows)
        self.assertFalse(summary["TokensMeasured"])
        self.assertNotIn("Tokens", json.dumps(summary["CallsPerValidCandidate"]))

    def test_rows_missing_an_arm_are_refused_rather_than_half_analysed(self):
        half = dict(self.rows, Rows=[row for row in self.rows["Rows"] if row["MaxRepairs"] == 0])
        with self.assertRaisesRegex(ValueError, "repair budgets"):
            ablation.analyse(half)
        dropped = [row for row in self.rows["Rows"] if not (row["MaxRepairs"] == 2 and row["Seed"] == self.rows["Seeds"][0])]
        with self.assertRaisesRegex(ValueError, "both arms"):
            ablation.analyse(dict(self.rows, Rows=dropped))


if __name__ == "__main__":
    unittest.main()
