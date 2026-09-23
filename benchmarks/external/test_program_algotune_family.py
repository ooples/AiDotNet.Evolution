"""V1-04b contracts. The mutant gate needs EVOLUTION_CORRECTNESS_UPSTREAM (pinned AlgoTune)."""
import json
import os
from pathlib import Path
import tempfile
import unittest

import algotune_family as family


class SealTests(unittest.TestCase):
    def test_test_seeds_are_drawn_at_seal_time_and_only_open_with_the_registered_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            record = family.seal(directory)
            sealed = family.open_seal(record["path"], record["sha256"])
            self.assertEqual(sorted(family.tasks()), sorted(sealed["TestSeeds"]))
            reserved = set(family.DEV_SEEDS) | set(family.VALIDATION_SEEDS)
            for seeds in sealed["TestSeeds"].values():
                self.assertEqual(family.TEST_SIZE, len(set(seeds)))
                self.assertFalse(reserved & set(seeds), "test inputs never overlap dev/validation")
            with self.assertRaisesRegex(ValueError, "registered hash"):
                family.open_seal(record["path"], "0" * 64)
            with self.assertRaises(FileExistsError):
                family.seal(directory)
            second = family.seal(Path(directory) / "second")
            self.assertNotEqual(sealed["TestSeeds"], family.open_seal(second["path"], second["sha256"])["TestSeeds"],
                                "each seal draws fresh test seeds")

    def test_test_inputs_require_the_opened_seal(self):
        with self.assertRaisesRegex(ValueError, "opened seal"):
            family.panel("unused", family.tasks()[0], "test")
        with self.assertRaises(ValueError):
            family.panel("unused", family.tasks()[0], "final")


class ScoringTests(unittest.TestCase):
    def test_speedup_is_the_median_interleaved_ratio_and_an_invalid_candidate_has_none(self):
        reference = iter([{"status": "valid", "duration_seconds": d} for d in (2.0, 2.2, 1.8, 2.0, 2.1)])
        candidate = iter([{"status": "valid", "duration_seconds": d} for d in (1.0, 1.0, 1.0, 1.0, 1.0)])
        result = family.speedup(lambda: next(reference), lambda: next(candidate))
        self.assertEqual(2.0, result["speedup"])
        fast_wrong = family.speedup(lambda: {"status": "valid", "duration_seconds": 2.0},
                                    lambda: {"status": "invalid", "duration_seconds": 0.001})
        self.assertIsNone(fast_wrong["speedup"])
        with self.assertRaises(RuntimeError):
            family.speedup(lambda: {"status": "invalid"}, lambda: {"status": "valid", "duration_seconds": 1.0})

    def test_eligibility_uses_dev_evidence_only(self):
        self.assertTrue(family.eligible([{"input_partition": "dev", "speedup": 1.2}]))
        self.assertFalse(family.eligible([{"input_partition": "dev", "speedup": 1.01}, {"input_partition": "dev", "speedup": None}]))
        with self.assertRaisesRegex(ValueError, "dev-partition"):
            family.eligible([{"input_partition": "test", "speedup": 3.0}])


class MutantTests(unittest.TestCase):
    def test_the_oracle_rejects_every_seeded_mutant_on_every_task(self):
        upstream = os.environ.get("EVOLUTION_CORRECTNESS_UPSTREAM")
        if not upstream:
            self.fail("Set EVOLUTION_CORRECTNESS_UPSTREAM to pinned AlgoTune for this required gate")
        results = [family.mutant_suite(upstream, task) for task in family.tasks()]
        self.assertEqual(11, len(results))
        self.assertEqual([], [(r["task"], r["accepted"]) for r in results if r["false_accepts"]])
        self.assertTrue(all(r["mutants"] >= 3 for r in results))
        floats = [r for r in results if r["kinds"].get("tolerance_edge")]
        self.assertGreaterEqual(len(floats), 7, "tolerance-edge mutants reached every float-output task")


if __name__ == "__main__":
    unittest.main()