import copy
import unittest
import gzip
import hashlib
import json
from pathlib import Path

from compare_profiles import compare


class ProfileComparisonTests(unittest.TestCase):
    def test_committed_campaign_hashes_and_comparison_recompute(self):
        directory = Path(__file__).resolve().parents[1] / "evidence/performance/us10-integration"
        saved = json.loads((directory / "comparison.json").read_text())
        reports = []
        for name in ("before", "after"):
            content = gzip.decompress((directory / (name + "-report.json.gz")).read_bytes())
            digest = hashlib.sha256(content).hexdigest()
            self.assertEqual(saved[name + "RawSha256"], digest)
            summary = json.loads((directory / (name + "-summary.json")).read_text())
            self.assertEqual(summary["raw"]["sha256"], digest)
            self.assertEqual(summary["raw"]["bytes"], len(content))
            reports.append(json.loads(content))
        result = compare(*reports)
        self.assertEqual({key: value for key, value in saved.items() if key not in ("beforeRawSha256", "afterRawSha256")}, result)

    def report(self):
        case = dict(id="engine", kind="engine")
        measurement = dict(case=case, environment={"runtime": "fixed"}, operations=100, iterations=1,
                           elapsedMilliseconds=500, operationsPerSecond=200, managedAllocatedBytes=1000,
                           processLifetimePeakWorkingSetBytes=2000, stateHash="same", bestQuality=1)
        return dict(status="passed", smoke=False, workingTreeStatus="clean", cases=[case], repetitions=3,
                    affinityHex="F", pinnedTopology="fixed", sourceRevision="a" * 40,
                    attempts=[dict(caseId="engine", repetition=i, status="passed", measurement=copy.deepcopy(measurement)) for i in range(3)])

    def test_normalized_medians_and_unchanged_state(self):
        before, after = self.report(), self.report()
        for attempt in after["attempts"]:
            attempt["measurement"].update(elapsedMilliseconds=250, operationsPerSecond=400, managedAllocatedBytes=500)
        result = compare(before, after)["rows"][0]
        self.assertEqual(50, result["elapsedReductionPercent"])
        self.assertEqual(50, result["allocationReductionPercent"])

    def test_unpaired_incomplete_failed_or_changed_work_cannot_pass(self):
        for corruption in ("failed", "dirty", "affinity", "missing", "duplicate", "quality", "hash", "throughput", "environment", "negative-allocation"):
            with self.subTest(corruption=corruption):
                before, after = self.report(), self.report()
                row = after["attempts"][0]["measurement"]
                if corruption == "failed": after["status"] = "failed"
                elif corruption == "dirty": after["workingTreeStatus"] = "dirty"
                elif corruption == "affinity": after["affinityHex"] = "3"
                elif corruption == "missing": after["attempts"].pop()
                elif corruption == "duplicate": after["attempts"][0]["repetition"] = 1
                elif corruption == "quality": row["bestQuality"] = 2
                elif corruption == "hash": row["stateHash"] = "changed"
                elif corruption == "throughput": row["operationsPerSecond"] = 900
                elif corruption == "environment": row["environment"] = {"runtime": "changed"}
                else: row["managedAllocatedBytes"] = -1
                with self.assertRaises(ValueError): compare(before, after)

    def test_zero_allocation_baseline_has_no_percentage_and_failed_attempt_is_not_hidden(self):
        before, after = self.report(), self.report()
        for attempt in before["attempts"]:
            attempt["measurement"]["managedAllocatedBytes"] = 0
        self.assertIsNone(compare(before, after)["rows"][0]["allocationReductionPercent"])
        after["attempts"].append(dict(caseId="engine", repetition=0, status="failed", measurement=None))
        with self.assertRaises(ValueError): compare(before, after)


if __name__ == "__main__":
    unittest.main()
