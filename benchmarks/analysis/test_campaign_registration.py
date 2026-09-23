import json
from pathlib import Path
import tempfile
import unittest

import campaign_registration as cr


def spec(**overrides):
    value = dict(Campaign="unit", Budget=dict(ModelTokens=1000, ModelCalls=10, WallSeconds=60), Models=["haiku", "opus"],
                 SeedCount=6, BootstrapSamples=2000, BootstrapSeed=11, FamilywiseAlpha=0.05,
                 Families=[dict(Family="algotune", Configs=["recommended", "matched"],
                                Endpoint=dict(Metric="speedup", Direction="maximize", FailureValue=0.0)),
                           dict(Family="sr", Configs=["recommended"],
                                Endpoint=dict(Metric="nmse", Direction="minimize", FailureValue=1e6))],
                 TestPartitionHashes={"algotune": "a" * 64, "sr": "b" * 64},
                 Published=dict(Family="algotune", Problems=["p1", "p2", "p3"]))
    value.update(overrides)
    return value


def runs(registration, ours=2.0, theirs=1.0, sr_ours=0.1, sr_theirs=0.5, fail=None):
    rows = []
    for seed_index, seed in enumerate(registration["Seeds"]):
        jitter = seed_index * 0.01
        for arm, value in (("aidotnet", ours + jitter), ("openevolve-recommended", theirs), ("openevolve-matched", theirs)):
            rows.append(dict(Family="algotune", Arm=arm, Seed=seed, Status="completed", Value=value))
        for arm, value in (("aidotnet", sr_ours + jitter / 10), ("openevolve-recommended", sr_theirs)):
            rows.append(dict(Family="sr", Arm=arm, Seed=seed, Status="completed", Value=value))
    if fail is not None:
        rows[fail].update(Status="failed", Value=None)
    return rows


class RegistrationTests(unittest.TestCase):
    def setUp(self):
        self.directory = Path(tempfile.mkdtemp())
        self.registration = cr.register(spec(), self.directory / "reg")
        self.path = self.directory / "reg" / "registration.json"

    def test_registration_is_frozen_by_hash_and_draws_its_own_seeds(self):
        loaded = cr.load_registration(self.path)
        self.assertEqual(self.registration["RegistrationSha256"], loaded["RegistrationSha256"])
        self.assertEqual(6, len(set(loaded["Seeds"])))
        tampered = json.loads(self.path.read_text(encoding="utf-8"))
        tampered["Spec"]["FamilywiseAlpha"] = 0.2
        self.path.write_text(json.dumps(tampered), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "changed after freezing"):
            cr.load_registration(self.path)
        with self.assertRaises(FileExistsError):
            cr.register(spec(), self.directory / "reg")

    def test_incomplete_specs_are_refused(self):
        for bad in (spec(TestPartitionHashes={"algotune": "a" * 64}), spec(FamilywiseAlpha=1),
                    spec(Families=[dict(Family="x", Configs=["matched", "recommended"],
                                        Endpoint=dict(Metric="m", Direction="maximize", FailureValue=0))]),
                    spec(Published=dict(Family="nope", Problems=["p"])), dict(spec(), Extra=1)):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                cr.validate(bad)

    def test_report_regenerates_byte_for_byte_and_carries_the_hash(self):
        raw, report = self.directory / "raw.json", self.directory / "report.json"
        raw.write_text(json.dumps(runs(self.registration)), encoding="utf-8")
        report.write_bytes(cr.render(cr.analyze(cr.load_registration(self.path), json.loads(raw.read_text()))))
        self.assertEqual(0, cr.check(self.path, raw, report))
        self.assertTrue(cr.lint(report, self.path))
        edited = json.loads(report.read_text())
        edited["OpenEvolveGate"] = not edited["OpenEvolveGate"]
        report.write_bytes(cr.render(edited))
        self.assertNotEqual(0, cr.check(self.path, raw, report))
        edited["RegistrationSha256"] = "0" * 64
        report.write_bytes(cr.render(edited))
        self.assertFalse(cr.lint(report, self.path))

    def test_superiority_in_every_endpoint_passes_and_direction_is_respected(self):
        report = cr.analyze(self.registration, runs(self.registration))
        self.assertEqual(3, len(report["Endpoints"]))
        self.assertTrue(all(e["Superior"] for e in report["Endpoints"]))
        self.assertTrue(report["OpenEvolveGate"])
        sr = [e for e in report["Endpoints"] if e["Family"] == "sr"][0]
        self.assertGreater(sr["MeanDifference"], 0)  # lower NMSE for ours is positive

    def test_a_tie_or_one_losing_endpoint_fails_the_gate(self):
        tie = cr.analyze(self.registration, runs(self.registration, sr_ours=0.5, sr_theirs=0.5))
        tied = [e for e in tie["Endpoints"] if e["Family"] == "sr"][0]
        self.assertFalse(tied["Superior"])
        self.assertFalse(tie["OpenEvolveGate"])

    def test_an_exact_tie_on_every_seed_is_not_superiority(self):
        rows = runs(self.registration)
        for row in rows:
            if row["Family"] == "sr":
                row["Value"] = 0.25
        endpoint = [e for e in cr.analyze(self.registration, rows)["Endpoints"] if e["Family"] == "sr"][0]
        self.assertEqual((0.0, 1.0, False), (endpoint["MeanDifference"], endpoint["OneSidedP"], endpoint["Superior"]))
    def test_failed_runs_take_the_registered_failure_value(self):
        # Index 0 is our first algotune run: failure scores FailureValue 0.0 against their 1.0.
        report = cr.analyze(self.registration, runs(self.registration, fail=0))
        endpoint = report["Endpoints"][0]
        self.assertEqual(1, endpoint["Failures"]["aidotnet"])
        self.assertLess(endpoint["MeanDifference"], 1.0)

    def test_missing_extra_and_duplicate_runs_are_refused(self):
        rows = runs(self.registration)
        for bad in (rows[1:], rows + [dict(rows[0])], rows + [dict(rows[0], Seed=12345)],
                    [dict(rows[0], Status="running")] + rows[1:]):
            with self.assertRaises(ValueError):
                cr.analyze(self.registration, bad)

    def test_holm_matches_a_hand_computed_example(self):
        # p = [0.01, 0.04, 0.03, 0.005], m = 4: sorted 0.005*4=0.02, 0.01*3=0.03, 0.03*2=0.06, 0.04*1 -> max(0.06, 0.04)
        adjusted = cr.holm([0.01, 0.04, 0.03, 0.005], 0.05)
        self.assertEqual([0.03, 0.06, 0.06, 0.02], [round(a, 10) for a, _ in adjusted])
        self.assertEqual([True, False, False, True], [r for _, r in adjusted])

    def test_published_rule_is_a_strict_majority_of_declared_problems(self):
        results = {"p1": dict(Ours=2, Published=1, Direction="maximize"),
                   "p2": dict(Ours=1, Published=1, Direction="maximize"),
                   "p3": dict(Ours=5, Published=3, Direction="minimize")}
        gate = cr.published_gate(self.registration, results)
        self.assertEqual((2, ["p1", "p2"], True), (gate["MatchedOrBeaten"], gate["Met"], gate["Gate"]))
        results["p1"]["Ours"] = 0
        self.assertFalse(cr.published_gate(self.registration, results)["Gate"])
        with self.assertRaises(ValueError):
            cr.published_gate(self.registration, {"p1": results["p1"]})


if __name__ == "__main__":
    unittest.main()