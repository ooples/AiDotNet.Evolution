import copy
import json
from pathlib import Path
import tempfile
import unittest
from analyze_reuse import digest, raw_observation, text_json, validate_plan, validate_run


def plan():
    return {"Protocol": "noisy-reuse-authored-development-v3", "SchemaVersion": 1, "SeedCount": 2,
        "Tasks": ["Quadratic", "Rippled"], "Phases": ["cold", "warm", "force-fresh", "expired"],
        "Methods": ["AlwaysFresh", "ExistingSamples"], "Executions": ["primary", "replay"],
        "AggregateEvaluationCap": 64, "SamplesPerAggregate": 5, "PhysicalObservationCap": 320,
        "ProposalCallCap": 256, "PriorAggregates": 16, "ImportedSeeds": 8,
        "MaximumAgeMinutes": 60, "WarmAgeMinutes": 1, "ExpiredAgeMinutes": 121,
        "PhaseSeedOffsets": {"prior": 100000, "cold": 200000, "warm": 300000, "force-fresh": 400000, "expired": 500000},
        "BootstrapSamples": 10000, "BootstrapSeed": 20260911, "SourceRevision": "working-tree-smoke", "Clock": "2026-09-11T00:00:00+00:00",
        "ConfirmationAggregates": 5, "ConfirmationSamples": 25, "ConfirmationSeedOffset": 1000000}


def fixture(group):
    (group / "raw").mkdir()
    run = "primary/Quadratic/0/prior"
    row = {"RunId": run, "Objective": "Quadratic", "Seed": 0, "Phase": "prior", "Method": "ExistingSamples", "Valid": True,
           "ScopeKey": "a" * 64, "FreshAggregates": 16, "CurrentPhysicalObservations": 80, "Trace": [], "Decisions": [],
           "PriorCostAttributed": 0, "CopiedPriorRecords": 0, "PriorCacheFiles": {}, "RepertoireSha256": None,
           "CacheHits": 0, "OriginalSampleReferences": 0, "RawReads": 0, "RawWrites": 16,
           "InitialGenomes": list(range(16)), "BestGenome": 15, "BestObservedQuality": 15,
           "Counters": {"EvaluationAttempts": 16, "Proposals": 16}, "Confirmation": None}
    spent = {"cost_units": 80, "cache_store_invocations": 32, "validation_calls": 16, "proposal_calls": 0}
    row["Resources"] = {"Spent": spent, "Unknown": 0, "MaximumViolated": False, "Reserved": {}, "Admitted": 4, "Settled": 4,
        "DroppedReceipts": 0, "Receipts": [{"OperationId": key, "Outcome": 0, "ExceededMaximum": False,
            "Charged": {"Amounts": {key: value}}} for key, value in spent.items()]}
    for index in range(16):
        origin = {"Kind": 0, "ScopeKey": "a" * 64, "SourceRunId": run, "SourceEvaluationId": str(index),
                  "SampleIds": [f"{run}/{index}/{sample}" for sample in range(5)], "ObservedAt": plan()["Clock"],
                  "OriginalCostUnits": 5, "StatisticsVersion": "arithmetic-mean-sample-se-v1", "StandardError": 0,
                  "LowerConfidenceBound": None, "UpperConfidenceBound": None, "ConfidenceLevel": None}
        raw = {"Genome": index, "ScopeKey": "a" * 64, "Origin": json.dumps(origin), "Values": [index] * 5,
               "Mean": index, "StandardError": 0, "RootSeed": 100000, "SeedStream": index}
        encoded = json.dumps(raw).encode()
        checksum = digest(encoded)
        (group / "raw" / (checksum + ".json")).write_bytes(encoded)
        row["Trace"].append({"EvaluationId": index, "Genome": index, "Status": 0, "Quality": index, "CostUnits": 5, "Origin": raw["Origin"]})
        row["Decisions"].append({"EvaluationId": index, "Decision": 3, "Origin": raw["Origin"], "EvidenceSha256": checksum})
    return row


class ReuseAnalysisTests(unittest.TestCase):
    def test_fixed_primary_plan_and_smoke_pin_rules(self):
        validate_plan(plan())
        for key, value in (("SeedCount", 32), ("SamplesPerAggregate", 1), ("PhysicalObservationCap", 321),
                           ("ExpiredAgeMinutes", 59), ("BootstrapSamples", 100), ("Methods", ["ExistingSamples"]), ("ConfirmationSamples", 0)):
            changed = plan(); changed[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_plan(changed)

    def test_embedded_json_rejects_duplicates_and_nonfinite_values(self):
        for value in ('{"x":1,"x":2}', '{"x":NaN}', '{"x":Infinity}'):
            with self.assertRaises(ValueError):
                text_json(value)

    def test_valid_prior_charges_and_unique_original_samples(self):
        with tempfile.TemporaryDirectory(prefix="reuse-analysis-") as directory:
            root = Path(directory); row = fixture(root); samples = {}; artifacts = set()
            compared = validate_run(root, row, plan(), {}, None, [], samples, artifacts)
            self.assertEqual(80, compared["CurrentPhysicalObservations"])
            self.assertEqual(80, len(samples)); self.assertEqual(16, len(artifacts))

    def test_charges_trace_winner_and_hidden_prior_are_checked(self):
        with tempfile.TemporaryDirectory(prefix="reuse-analysis-") as directory:
            root = Path(directory); original = fixture(root)
            changes = [lambda row: row["Resources"]["Spent"].update(cost_units=79),
                       lambda row: row["Resources"].update(Unknown=1),
                       lambda row: row["Resources"].update(DroppedReceipts=1),
                       lambda row: row["Resources"]["Receipts"].pop(),
                       lambda row: row["Trace"][0].update(Genome=99),
                       lambda row: row.update(BestObservedQuality=999),
                       lambda row: row.update(PriorCostAttributed=80),
                       lambda row: row.update(OriginalSampleReferences=5),
                       lambda row: row["Decisions"].pop()]
            for change in changes:
                changed = copy.deepcopy(original); change(changed)
                with self.assertRaises(ValueError):
                    validate_run(root, changed, plan(), {}, None, [], {}, set())

    def test_reused_samples_keep_ids_uncertainty_and_do_not_increase_sample_power(self):
        with tempfile.TemporaryDirectory(prefix="reuse-analysis-") as directory:
            root = Path(directory); row = fixture(root); trace = row["Trace"][0]; checksum = row["Decisions"][0]["EvidenceSha256"]
            samples = {}; artifacts = set()
            raw_observation(root, checksum, trace, samples, artifacts)
            reused = copy.deepcopy(trace)
            origin = text_json(trace["Origin"]); origin["Kind"] = 1
            reused["Origin"] = json.dumps(origin); reused["CostUnits"] = 0
            raw_observation(root, checksum, reused, samples, artifacts)
            self.assertEqual(5, len(samples)); self.assertEqual(1, len(artifacts))
            origin["StandardError"] = 1; reused["Origin"] = json.dumps(origin)
            with self.assertRaises(ValueError):
                raw_observation(root, checksum, reused, samples, artifacts)

    def test_corrupt_raw_values_fail_even_if_digest_is_recomputed(self):
        with tempfile.TemporaryDirectory(prefix="reuse-analysis-") as directory:
            root = Path(directory); row = fixture(root)
            checksum = row["Decisions"][0]["EvidenceSha256"]
            raw = json.loads((root / "raw" / (checksum + ".json")).read_bytes())
            raw["Values"][0] = 100
            encoded = json.dumps(raw).encode(); changed_digest = digest(encoded)
            (root / "raw" / (changed_digest + ".json")).write_bytes(encoded)
            with self.assertRaisesRegex(ValueError, "mean/standard error"):
                raw_observation(root, changed_digest, row["Trace"][0], {}, set())


if __name__ == "__main__":
    unittest.main()
