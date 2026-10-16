"""V1-61 (#174): the committed distributed-evaluation evidence, re-derived rather than trusted.

`benchmarks/EvolutionDistributed/verify.py` writes `summary.json` during a container run, and
`distributed-evaluation.yml` runs it - but only when `benchmarks/EvolutionDistributed/**` changes,
and never against the bundle that is committed. So the numbers the story cites had nothing standing
behind them between runs: an edited `summary.json`, or raw rows that stopped supporting it, would
both pass every gate in the repository.

This re-derives each claim from the committed raw rows and requires the committed summary to agree.
One deliberate difference from the verifier: `exactly_once` is re-derived from the coordinator's
`committed` array rather than from the three worker logs, because the bundle commits only the killed
worker's log. That makes the coordinator's accepted set the instrument here, and the counts it
produces are asserted against the ones `verify.py` recorded from the worker side - if the two ever
disagreed, this test is what would say so.
"""
import importlib.util
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "benchmarks" / "evidence" / "distributed-v1-61" / "818b642"
VERIFIER = ROOT / "benchmarks" / "EvolutionDistributed" / "verify.py"

# The story's declared target is >= 87.5% of linear scaling on four workers. The verifier states the
# same thing as a ratio, and this is the only place the two forms are tied together.
DECLARED_EFFICIENCY = 0.875
WORKERS = 4


def load_verifier():
    spec = importlib.util.spec_from_file_location("v1_61_verify", VERIFIER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def rows(name):
    return json.loads((EVIDENCE / name).read_text(encoding="utf-8"))


class CommittedEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.verify = load_verifier()
        cls.summary = rows("summary.json")
        cls.checks = cls.summary["checks"]
        cls.single, cls.multi = rows("single.json"), rows("multi.json")
        cls.tp1, cls.tp4 = rows("tp1.json"), rows("tp4.json")

    def test_the_bundle_holds_every_row_its_claims_are_derived_from(self):
        for name in ("summary.json", "single.json", "multi.json", "tp1.json", "tp4.json",
                     "multi-w1.log", "multi-coordinator.log", "README.md"):
            with self.subTest(name=name):
                self.assertTrue((EVIDENCE / name).is_file(), name + " is missing from the bundle")
        self.assertTrue(self.summary["passed"], "the committed summary must record a passing run")

    def test_the_multi_host_state_hash_is_re_derived_not_quoted(self):
        # The whole point of the story: more hosts must not change the answer.
        self.assertEqual(self.single["stateHash"], self.multi["stateHash"])
        self.assertEqual(self.tp1["stateHash"], self.tp4["stateHash"],
                         "one and four workers must reach the same state")
        recorded = self.checks["state_hash_matches_single_host"]
        self.assertEqual(self.single["stateHash"], recorded["single"])
        self.assertEqual(self.multi["stateHash"], recorded["multi"])

    def test_exactly_once_is_re_derived_from_the_coordinators_accepted_set(self):
        asked = self.multi["evaluationsAsked"]
        committed = [entry["evaluationId"] for entry in self.multi["committed"]]
        self.assertEqual(asked, self.multi["resultsDelivered"])
        self.assertEqual(0, self.multi["resultsMissing"])
        self.assertEqual(asked, len(committed), "every asked evaluation must carry a commit")
        self.assertEqual(len(committed), len(set(committed)), "no evaluation may be committed twice")
        recorded = self.checks["exactly_once"]
        self.assertEqual([asked, asked, 0, asked],
                         [recorded["asked"], recorded["delivered"], recorded["missing"],
                          recorded["evaluations_with_an_accepted_commit"]],
                         "the worker-side counts verify.py recorded must match the coordinator's set")
        self.assertEqual([], recorded["evaluations_accepted_more_than_once"])

    def test_the_killed_workers_lease_was_re_issued_to_another_worker(self):
        claims = [e for e in self.verify.events(EVIDENCE / "multi-w1.log") if e.get("event") == "claim"]
        self.assertTrue(claims, "the killed worker's log must show the lease it held")
        last = claims[-1]["identity"]
        final = next((c for c in self.multi["committed"]
                      if str(c["evaluationId"]) == str(last["evaluationId"])), None)
        self.assertIsNotNone(final, "the claimed evaluation must still have been committed")
        self.assertNotEqual(last["leaseId"], final["leaseId"], "a re-issue means a new lease")
        self.assertNotEqual("worker:w1", final["provenance"], "and a different worker")
        self.assertEqual(last, self.checks["killed_worker_lease_reissued"]["killed_worker_last_claim"])

    def test_the_scaling_claim_is_computed_from_the_two_throughput_runs(self):
        ratio = self.tp4["evaluationsPerSecond"] / self.tp1["evaluationsPerSecond"]
        self.assertAlmostEqual(ratio, self.checks["four_worker_scaling"]["ratio"], places=9,
                               msg="the recorded ratio must be the one the rows produce")
        self.assertGreaterEqual(ratio, self.verify.SCALING_TARGET)
        self.assertGreaterEqual(ratio / WORKERS, DECLARED_EFFICIENCY,
                                "the story declares >= 87.5% of linear scaling")

    def test_the_verifiers_floor_cannot_drift_below_the_declared_target(self):
        # Lowering SCALING_TARGET would let a slower run pass the gate while the story kept citing
        # 87.5%, so the two forms of the same number are tied together here.
        self.assertGreaterEqual(self.verify.SCALING_TARGET / WORKERS, DECLARED_EFFICIENCY)


if __name__ == "__main__":
    unittest.main()
