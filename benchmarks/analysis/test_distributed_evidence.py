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
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = ROOT / "benchmarks" / "evidence" / "distributed-v1-61" / "818b642"
VERIFIER = ROOT / "benchmarks" / "EvolutionDistributed" / "verify.py"

# The story's declared target is >= 87.5% of linear scaling on four workers. The verifier states the
# same thing as a ratio, and this is the only place the two forms are tied together.
DECLARED_EFFICIENCY = 0.875
WORKERS = 4
# A state hash is a SHA-256 digest. Comparing two empty strings succeeds, so the shape is checked
# before the equality - review caught that an emptied pair would have passed every check here.
HASH = re.compile(r"^[0-9a-f]{64}$")


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
        for name, row in (("single", self.single), ("multi", self.multi), ("tp1", self.tp1), ("tp4", self.tp4)):
            with self.subTest(row=name):
                self.assertRegex(row["stateHash"], HASH, name + " carries no usable state hash")
        self.assertEqual(self.single["stateHash"], self.multi["stateHash"])
        self.assertEqual(self.tp1["stateHash"], self.tp4["stateHash"],
                         "one and four workers must reach the same state")
        # Every recorded field, not a sample of them: an edited `four_workers` used to leave all of
        # these tests passing.
        across_hosts = self.checks["state_hash_matches_single_host"]
        self.assertEqual({"passed": True, "single": self.single["stateHash"], "multi": self.multi["stateHash"]},
                         across_hosts)
        across_workers = self.checks["state_hash_independent_of_worker_count"]
        self.assertEqual({"passed": True, "one_worker": self.tp1["stateHash"], "four_workers": self.tp4["stateHash"]},
                         across_workers)

    def test_every_check_the_summary_records_is_marked_passed(self):
        # The `passed` flags are the verdict verify.py exited on; a check whose fields were edited to
        # agree with each other could still carry passed=false, and this is what refuses that.
        for name, check in self.checks.items():
            with self.subTest(check=name):
                self.assertTrue(check["passed"], name + " is recorded as failing")

    def test_exactly_once_is_re_derived_from_the_coordinators_accepted_set(self):
        asked = self.multi["evaluationsAsked"]
        committed = [entry["evaluationId"] for entry in self.multi["committed"]]
        self.assertEqual(asked, self.multi["resultsDelivered"])
        self.assertEqual(0, self.multi["resultsMissing"])
        self.assertEqual(asked, len(committed), "every asked evaluation must carry a commit")
        self.assertEqual(len(committed), len(set(committed)), "no evaluation may be committed twice")
        recorded = self.checks["exactly_once"]
        self.assertEqual({"passed": True, "asked": asked, "delivered": asked, "missing": 0,
                          "evaluations_with_an_accepted_commit": asked,
                          "evaluations_accepted_more_than_once": []}, recorded,
                         "the worker-side counts verify.py recorded must match the coordinator's set")

    def test_the_killed_workers_lease_was_re_issued_to_another_worker(self):
        events = self.verify.events(EVIDENCE / "multi-w1.log")
        claims = [e for e in events if e.get("event") == "claim"]
        self.assertTrue(claims, "the killed worker's log must show the lease it held")
        last = claims[-1]["identity"]
        # The re-issue only means anything if the killed worker never committed that lease itself -
        # otherwise a second commit under a different lease would read as a re-issue. verify.py
        # rejects that case and this did not, until review pointed it out.
        committed_by_killed = {e["identity"]["leaseId"] for e in events
                               if str(e.get("event", "")).startswith("commit")}
        self.assertNotIn(last["leaseId"], committed_by_killed,
                         "the killed worker committed the lease it is supposed to have lost")
        final = next((c for c in self.multi["committed"]
                      if str(c["evaluationId"]) == str(last["evaluationId"])), None)
        self.assertIsNotNone(final, "the claimed evaluation must still have been committed")
        self.assertNotEqual(last["leaseId"], final["leaseId"], "a re-issue means a new lease")
        self.assertNotEqual("worker:w1", final["provenance"], "and a different worker")
        self.assertEqual({"passed": True, "killed_worker_last_claim": last, "final_commit": final},
                         self.checks["killed_worker_lease_reissued"],
                         "every field of the recorded check, not only the claim")

    def test_the_scaling_claim_is_computed_from_the_two_throughput_runs(self):
        # The ratio means nothing unless the two runs are the one- and four-worker runs they claim to
        # be, with no local worker quietly helping either of them (review caught that gap).
        self.assertEqual(1, self.tp1["workersAwaited"], "tp1 must be the single remote worker run")
        self.assertEqual(WORKERS, self.tp4["workersAwaited"], "tp4 must be the four remote worker run")
        for name, row in (("tp1", self.tp1), ("tp4", self.tp4)):
            with self.subTest(row=name):
                self.assertEqual(0, row["localWorkers"], name + " ran a local worker, so it is not remote-only")
        ratio = self.tp4["evaluationsPerSecond"] / self.tp1["evaluationsPerSecond"]
        recorded = self.checks["four_worker_scaling"]
        self.assertEqual({"passed", "one_worker_per_second", "four_workers_per_second", "ratio", "target", "efficiency"},
                         set(recorded), "the recorded check changed shape")
        self.assertTrue(recorded["passed"])
        self.assertEqual(self.tp1["evaluationsPerSecond"], recorded["one_worker_per_second"])
        self.assertEqual(self.tp4["evaluationsPerSecond"], recorded["four_workers_per_second"])
        self.assertAlmostEqual(ratio, recorded["ratio"], places=9,
                               msg="the recorded ratio must be the one the rows produce")
        self.assertEqual(self.verify.SCALING_TARGET, recorded["target"], "the recorded gate is the verifier's")
        self.assertAlmostEqual(ratio / WORKERS, recorded["efficiency"], places=9)
        self.assertGreaterEqual(ratio, self.verify.SCALING_TARGET)
        self.assertGreaterEqual(ratio / WORKERS, DECLARED_EFFICIENCY,
                                "the story declares >= 87.5% of linear scaling")

    def test_the_verifiers_floor_cannot_drift_below_the_declared_target(self):
        # Lowering SCALING_TARGET would let a slower run pass the gate while the story kept citing
        # 87.5%, so the two forms of the same number are tied together here.
        self.assertGreaterEqual(self.verify.SCALING_TARGET / WORKERS, DECLARED_EFFICIENCY)


if __name__ == "__main__":
    unittest.main()
