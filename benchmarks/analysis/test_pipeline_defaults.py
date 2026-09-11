import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

from compare_pipeline_defaults import compare, verify_archive


def fixture():
    cases = [dict(id="case-" + str(index), kind="engine") for index in range(44)]
    attempts = [dict(caseId=case["id"], repetition=repeat, status="passed", measurement=dict(case=case,
                environment=dict(processorGroup="0", affinityHex="F"), operations=256, evaluationCalls=256,
                occupiedCells=100, stateHash="a", bestQuality=1, elapsedMilliseconds=10, managedAllocatedBytes=1000))
                for case in cases for repeat in range(3)]
    before = dict(sourceRevision="a" * 40, protocol="engine-profile-v1", status="passed", smoke=False,
                  repetitions=3, cases=cases, attempts=attempts, affinityHex="F")
    after = copy.deepcopy(before); after["sourceRevision"] = "b" * 40
    return before, after


class DefaultProfileComparisonTests(unittest.TestCase):
    def test_complete_equal_work_and_descriptive_allocation_differences(self):
        before, after = fixture()
        for row in after["attempts"]:
            row["measurement"]["managedAllocatedBytes"] += 100
        report = compare(before, after, "a" * 40, "b" * 40)
        self.assertEqual(len(report["Comparisons"]), 44)
        self.assertTrue(report["AllStateQualityAndWorkIdentical"])
        self.assertFalse(report["ConfirmatoryEligible"])
        self.assertTrue(all(row["AllocationDifferenceBytes"] == 100 for row in report["Comparisons"]))

    def test_hardware_omission_failure_and_logical_mismatch_fail_closed(self):
        for corrupt in (
            lambda r: r["attempts"][0]["measurement"]["environment"].update(processorGroup="1"),
            lambda r: r["attempts"][0]["measurement"].update(stateHash="different"),
            lambda r: r["attempts"][0].update(status="failed"),
            lambda r: r["attempts"].pop(),
            lambda r: r.update(sourceRevision="c" * 40),
        ):
            before, after = fixture(); corrupt(after)
            with self.assertRaises(ValueError):
                compare(before, after, "a" * 40, "b" * 40)

    def test_archive_hash_chain_and_recomputation(self):
        before, after = fixture()
        report = compare(before, after, "a" * 40, "b" * 40)
        first, second = json.dumps(before).encode(), json.dumps(after).encode()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); archive_path = root / "profiles.zip"; comparison_path = root / "comparison.json"
            with zipfile.ZipFile(archive_path, "w") as archive:
                archive.writestr("before/report.json", first); archive.writestr("after/report.json", second)
            report.update(BeforeReportSha256=hashlib.sha256(first).hexdigest(), AfterReportSha256=hashlib.sha256(second).hexdigest(),
                          BeforeReportMember="before/report.json", AfterReportMember="after/report.json",
                          ProfilesZipSha256=hashlib.sha256(archive_path.read_bytes()).hexdigest())
            comparison_path.write_text(json.dumps(report))
            self.assertEqual(verify_archive(archive_path, comparison_path), report)
            for field, value in (("AllStateQualityAndWorkIdentical", False), ("ProfilesZipSha256", "f" * 64),
                                 ("BeforeReportSha256", "f" * 64), ("BeforeReportMember", "../report.json")):
                corrupted = dict(report); corrupted[field] = value
                comparison_path.write_text(json.dumps(corrupted))
                with self.assertRaises(ValueError):
                    verify_archive(archive_path, comparison_path)
