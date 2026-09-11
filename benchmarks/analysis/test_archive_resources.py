import copy
import hashlib
from pathlib import Path
import tempfile
import unittest
from run_archive_resources import METHODS, dump, plan, quality_fingerprint, summarize, validate_case, verify_directory


def report(spec, configuration):
    task, dimension, seed, method = spec
    budget = configuration["EvaluationBudget"]
    return {"SchemaVersion": 2, "Protocol": "archive-resource-case-development-v1", "SourceRevision": configuration["SourceRevision"],
            "Budget": budget, "Dimensions": dimension, "Seeds": 1, "SelectedSeed": seed, "EliteCapacity": 32,
            "MaximumGridCells": 10_000_000, "ReferenceDefinition": {"DefinitionHash": "reference-" + str(dimension)},
            "Measurement": {"PeakResidentBudgetBytes": 256 * 1024 * 1024, "PeakResidentBytes": 64 * 1024 * 1024,
                "MemoryBudgetExceeded": False, "ElapsedMilliseconds": 10, "CpuMilliseconds": 5, "AllocatedBytes": 1000,
                "WorkerSha256": "a" * 64, "CoreSha256": "b" * 64},
            "Runs": [{"Task": task + str(dimension), "Method": method, "Seed": seed, "Status": "completed",
                "InitialPopulationHash": "initial-" + str(seed), "StateHash": "state-" + str(seed),
                "EvaluatorCalls": budget, "Proposals": budget,
                "Samples": [{"Attempts": 1, "CostUnits": 1} for _ in range(budget)],
                "Resources": {"Spent": {"cost_units": budget, "proposal_calls": budget - 8}, "Reserved": {}, "Unknown": 0, "MaximumViolated": False},
                "ReferenceUtility": 0.25 if method == "SparseGrid" else 0.5, "OccupiedSearchCells": 10, "OccupiedReferenceCells": 8,
                "ArchiveDefinitionHash": "search-" + method, "Projection": {"SourceDefinitionHash": "search-" + method,
                    "SourceEliteCount": 10, "RetainedEliteCount": 8, "CollisionDiscardedEliteCount": 2, "SourceVersion": 10, "TargetVersion": 11}}]}


def fixture():
    configuration = plan("working-tree-smoke", True)
    configuration["BootstrapSamples"] = 200
    records = []
    for phase in configuration["Phases"]:
        for task in configuration["Tasks"]:
            for dimension in configuration["Dimensions"]:
                for seed in range(configuration["SeedCount"]):
                    for method in METHODS:
                        spec = [task, dimension, seed, method]
                        records.append({"Phase": phase, "Case": spec, "Report": report(spec, configuration)})
    return configuration, records


class ArchiveResourceTests(unittest.TestCase):
    def test_paired_effect_and_physical_replay_accounting(self):
        configuration, records = fixture()
        result = summarize(configuration, records)
        self.assertEqual(32, result["ScheduledCases"])
        self.assertEqual({"primary": 512, "replay": 512}, result["MeasuredPhysicalEvaluationsByPhase"])
        self.assertEqual(0.25, result["PrimaryEffect"])
        self.assertEqual([0.25, 0.25], result["PrimaryPairedSeedBootstrap95"])
        self.assertTrue(result["QualityReplayEqual"])

    def test_missing_or_duplicate_case_cannot_disappear(self):
        configuration, records = fixture()
        for changed in (records[:-1], records + [records[0]]):
            with self.assertRaises(ValueError):
                summarize(configuration, changed)

    def test_unknown_worker_failure_remains_penalized_and_cost_unknown(self):
        configuration, records = fixture()
        records[0]["Report"] = None
        result = summarize(configuration, records)
        self.assertEqual(1, result["FailedOrIncompleteCases"])
        self.assertEqual(1, result["UnknownPhysicalCallCases"])
        self.assertFalse(result["QualityReplayEqual"])
        self.assertEqual(480, result["MeasuredPhysicalEvaluationsByPhase"]["primary"])

    def test_resource_changes_are_not_false_quality_replay_failures(self):
        configuration, records = fixture()
        original = quality_fingerprint(records[-1]["Report"])
        records[-1]["Report"]["Measurement"]["PeakResidentBytes"] += 1000
        records[-1]["Report"]["Measurement"]["ElapsedMilliseconds"] += 1000
        self.assertEqual(original, quality_fingerprint(records[-1]["Report"]))
        self.assertTrue(summarize(configuration, records)["QualityReplayEqual"])
        records[-1]["Report"]["Runs"][0]["ReferenceUtility"] = 0.9
        self.assertFalse(summarize(configuration, records)["QualityReplayEqual"])

    def test_unpaired_initialization_geometry_and_binaries_are_rejected(self):
        configuration, records = fixture()
        for field, key in (("Runs", "InitialPopulationHash"), ("ReferenceDefinition", "DefinitionHash"), ("Measurement", "WorkerSha256")):
            changed = copy.deepcopy(records)
            target = changed[0]["Report"][field]
            if field == "Runs":
                target = target[0]
            target[key] = "c" * 64
            with self.subTest(key=key), self.assertRaises(ValueError):
                summarize(configuration, changed)

    def test_budget_overrun_and_lost_receipt_cannot_be_completed(self):
        configuration, records = fixture()
        for change in (lambda r: r["Measurement"].update(PeakResidentBytes=300 * 1024 * 1024),
                       lambda r: r["Measurement"].update(PeakResidentBudgetBytes=1024),
                       lambda r: r["Runs"][0]["Resources"].update(Unknown=1),
                       lambda r: r["Runs"][0]["Resources"]["Reserved"].update(cost_units=1),
                       lambda r: r["Runs"][0]["Samples"].pop(),
                       lambda r: r["Runs"][0]["Projection"].update(CollisionDiscardedEliteCount=0)):
            invalid = copy.deepcopy(records[0]["Report"])
            change(invalid)
            with self.assertRaises(ValueError):
                validate_case(invalid, records[0]["Case"], configuration)

    def test_primary_plan_requires_full_source_and_fixed_seed_count(self):
        with self.assertRaises(ValueError):
            plan("working-tree-smoke")
        configuration = plan("a" * 40)
        self.assertEqual(32, configuration["SeedCount"])
        self.assertEqual([12, 20], configuration["Dimensions"])
        self.assertEqual(256, configuration["EvaluationBudget"])

    def test_offline_verifier_checks_raw_bytes_and_recomputed_summary(self):
        configuration, records = fixture()
        configuration["BootstrapSamples"] = 10000
        with tempfile.TemporaryDirectory(prefix="archive-resource-proof-") as directory:
            root = Path(directory)
            dump(root / "plan.json", configuration)
            for index, record in enumerate(records):
                record["ReportFile"] = f"case-{index}.json"
                raw = root / record["ReportFile"]
                dump(raw, record["Report"])
                record["ReportSha256"] = hashlib.sha256(raw.read_bytes()).hexdigest()
                dump(root / f"case-{index}.record.json", record)
            expected = summarize(configuration, records)
            dump(root / "summary.json", expected)
            self.assertEqual(expected, verify_directory(root))
            # Only this test's owned artifact; a same-value byte change still invalidates its digest.
            raw = root / "case-0.json"
            with raw.open("a", encoding="utf-8") as output:
                output.write("\n")
            with self.assertRaisesRegex(ValueError, "Raw report differs"):
                verify_directory(root)


if __name__ == "__main__":
    unittest.main()
