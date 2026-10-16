"""V1-43 (#123): the report generator, its lint, and the worked report committed beside the pilot.

The lint is this story's metric - *claims without linked evidence: 0* - so most of what is asserted
here is that it actually refuses the four ways a report can carry an unsupported claim. A lint that
only ever passes would measure nothing.
"""
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import release_report as release  # noqa: E402

PILOT = ROOT / "benchmarks" / "evidence" / "pilot-v1-07" / "aafd609"
WORKED = ROOT / "benchmarks" / "evidence" / "release-report-v1-43" / "0354e33" / "report.json"


class ReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report = release.build(PILOT)

    def test_the_committed_report_regenerates_byte_for_byte_from_the_pilot_bundle(self):
        self.assertEqual(WORKED.read_text(encoding="utf-8"), release.render(self.report))

    def test_the_report_names_what_the_criterion_asks_it_to_name(self):
        for field in ("Benchmark", "Tasks", "Models", "Arms", "Budget", "Hardware", "Bundle"):
            with self.subTest(field=field):
                self.assertTrue(self.report[field], field + " is empty")
        self.assertEqual(["rational_gain"], self.report["Benchmark"])
        self.assertEqual(["sonnet"], self.report["Models"])
        self.assertIn("aidotnet", self.report["Arms"])
        # Every file in the bundle is listed, which is what "publishes the full bundle" means here.
        self.assertEqual(sorted(path.name for path in PILOT.iterdir() if path.is_file()), self.report["Bundle"])

    def test_absent_hardware_reads_as_absent_rather_than_as_a_value(self):
        # The pilot bundle does not record hardware. Inventing one would be the worst possible
        # failure for a report whose job is to be checkable, so absence is stated.
        self.assertIn("not recorded", self.report["Hardware"])

    def test_losses_and_inconclusive_results_stay_visible(self):
        verdicts = {endpoint["Verdict"] for endpoint in self.report["Endpoints"]}
        self.assertEqual({"inconclusive"}, verdicts, "both pilot endpoints were inconclusive")
        lost = [item for item in self.report["Claims"] if "Cells lost" in item["Claim"]]
        self.assertEqual(2, len(lost), "one published loss list per config")
        for item in lost:
            self.assertEqual("ranked-losses.json", item["Evidence"])

    def test_the_lint_counts_zero_on_the_worked_report(self):
        self.assertEqual(0, release.lint(self.report, PILOT))
        self.assertEqual(0, self.report["Unsupported"])

    def test_a_claim_with_no_evidence_is_refused(self):
        broken = json.loads(release.render(self.report))
        broken["Claims"][0]["Evidence"] = ""
        with self.assertRaisesRegex(ValueError, "no evidence named"):
            release.lint(broken, PILOT)

    def test_evidence_outside_the_bundle_is_refused(self):
        broken = json.loads(release.render(self.report))
        broken["Claims"][0]["Evidence"] = "somewhere-else.json"
        with self.assertRaisesRegex(ValueError, "is not in the bundle"):
            release.lint(broken, PILOT)

    def test_an_inconclusive_endpoint_promoted_to_superior_is_refused(self):
        # The failure that would matter most, and the one every other check here would miss.
        broken = json.loads(release.render(self.report))
        broken["Endpoints"][0]["Superior"] = True
        broken["Endpoints"][0]["Verdict"] = "superior"
        with self.assertRaisesRegex(ValueError, "interval that includes no difference"):
            release.lint(broken, PILOT)

    def test_a_recorded_count_that_disagrees_with_the_lint_is_refused(self):
        broken = json.loads(release.render(self.report))
        broken["Unsupported"] = 3
        with self.assertRaisesRegex(ValueError, "records 3 unsupported claims"):
            release.lint(broken, PILOT)

    def test_a_bundle_missing_a_required_file_is_refused(self):
        with self.assertRaisesRegex(ValueError, "missing"):
            release.build(ROOT / "benchmarks" / "evidence" / "distributed-v1-61" / "818b642")


if __name__ == "__main__":
    unittest.main()
