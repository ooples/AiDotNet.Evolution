"""US-12 release gate contracts. Every fixture is synthetic and is not campaign evidence."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

import campaign_registration as cr
import release_gate as rg

REPOSITORY = Path(__file__).resolve().parents[2]
SEALS = {"algotune": b'{"Family":"algotune","TestSeeds":{"t1":[11,12]}}', "sr": b'{"Family":"sr","TestSeeds":{"s1":[21]}}'}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def spec(**overrides):
    value = dict(Campaign="release-gate-fixture", Budget=dict(ModelTokens=1000, ModelCalls=10, WallSeconds=60),
                 Models=["haiku", "opus"], SeedCount=6, BootstrapSamples=1000, BootstrapSeed=7, FamilywiseAlpha=0.05,
                 Families=[dict(Family="algotune", Configs=["recommended", "matched"],
                                Endpoint=dict(Metric="speedup", Direction="maximize", FailureValue=0.0)),
                           dict(Family="sr", Configs=["recommended", "matched"],
                                Endpoint=dict(Metric="nmse", Direction="minimize", FailureValue=1e6))],
                 TestPartitionHashes={name: sha(data) for name, data in SEALS.items()},
                 Published=dict(Family="algotune", Problems={"p1": "maximize", "p2": "maximize", "p3": "minimize"}))
    value.update(overrides)
    return value


def rows(registration, ours=2.0, sr_ours=0.1, calls=10, tokens=1000):
    result = []
    for index, seed in enumerate(registration["Seeds"]):
        jitter = index * 0.01
        for family, mine, theirs in (("algotune", ours + jitter, 1.0), ("sr", sr_ours + jitter / 10, 0.5)):
            configs = next(f["Configs"] for f in registration["Spec"]["Families"] if f["Family"] == family)
            for arm in ["aidotnet"] + [f"openevolve-{config}" for config in configs]:
                result.append(dict(Family=family, Arm=arm, Seed=seed, Status="completed",
                                   Value=mine if arm == "aidotnet" else theirs, ModelCalls=calls, ModelTokens=tokens))
    return result


def git(repository, *args):
    subprocess.run(["git", "-C", str(repository), "-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid",
                    "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false", *args], check=True, capture_output=True)


class Fixture:
    """A throwaway repository: commit 1 freezes the registration, commit 2 adds the claim and bundle."""

    def __init__(self, spec_value=None, claim="supported-families", families=("algotune",), raw=None, published=None,
                 mutate_bundle=None, leak_partition=False):
        self.root = Path(tempfile.mkdtemp())
        git(self.root, "init", "-q")
        registrations = self.root / "release" / "registrations"
        registrations.mkdir(parents=True)
        self.registration = cr.register(spec_value or spec(), registrations / "fixture")
        if leak_partition:
            (self.root / "notes.json").write_bytes(SEALS["algotune"])
        git(self.root, "add", "-A")
        git(self.root, "commit", "-q", "-m", "register")
        self.commit = subprocess.check_output(["git", "-C", str(self.root), "rev-parse", "HEAD"], text=True).strip()
        bundle = self.root / "release" / "bundles" / "fixture"
        (bundle / "partitions").mkdir(parents=True)
        registration_bytes = (registrations / "fixture" / "registration.json").read_bytes()
        raw_rows = rows(self.registration) if raw is None else raw(self.registration)
        files = {"registration.json": registration_bytes,
                 "raw.json": json.dumps(raw_rows, indent=1).encode(),
                 "report.json": cr.render(cr.analyze(self.registration, raw_rows)),
                 "pilot.json": b'{"fixture": "pilot"}', "power-analysis.json": b'{"fixture": "power"}',
                 "published.json": json.dumps(published or {"p1": {"Ours": 3, "Published": 2},
                                                            "p2": {"Ours": 3, "Published": 2},
                                                            "p3": {"Ours": 1, "Published": 2}}).encode()}
        for family in self.registration["Spec"]["Families"]:
            files[f"partitions/{family['Family']}.json"] = SEALS[family["Family"]]
        if self.registration["Spec"]["Published"] is None:
            del files["published.json"]
        manifest = {"schema": rg.BUNDLE_SCHEMA, "artifacts": {}}
        for name, data in files.items():
            (bundle / name).write_bytes(data)
            manifest["artifacts"][name] = sha(data)
        (bundle / "manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
        self.claim = {"schema": rg.CLAIM_SCHEMA, "claim": claim, "benchmark": "fixture benchmark",
                      "hardware": "fixture hardware", "registration_commit": self.commit,
                      "registration_path": "release/registrations/fixture/registration.json", "bundle": "bundles/fixture"}
        if claim == "supported-families":
            self.claim["families"] = list(families)
        if mutate_bundle:
            mutate_bundle(self, bundle)
        self.write_claim()
        git(self.root, "add", "-A")
        git(self.root, "commit", "-q", "-m", "release")

    def write_claim(self):
        (self.root / "release" / "claim.json").write_text(json.dumps(self.claim, indent=1), encoding="utf-8")

    def assess(self, version="0.5.0"):
        return rg.release(self.root / "release", self.root, version)

    def close(self):
        shutil.rmtree(self.root, ignore_errors=True)


class ReleaseGateTests(unittest.TestCase):
    def fixture(self, **options):
        fixture = Fixture(**options)
        self.addCleanup(fixture.close)
        return fixture

    def refused(self, message, **options):
        fixture = self.fixture(**options)
        with self.assertRaisesRegex(ValueError, message):
            fixture.assess()

    def test_checked_in_claim_is_an_explicit_no_claim_for_this_version(self):
        version = json.loads((REPOSITORY / ".release-please-manifest.json").read_text(encoding="utf-8"))["."]
        assessment = rg.release(REPOSITORY / "release", REPOSITORY, version)
        self.assertEqual((assessment["claim"], assessment["eligible"]), ("none", True))

    def test_no_claim_cannot_ship_one_point_oh(self):
        root = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, True)
        (root / "claim.json").write_text(json.dumps({"schema": rg.CLAIM_SCHEMA, "claim": "none", "reason": "r"}))
        self.assertTrue(rg.release(root, root, "0.9.9")["eligible"])
        for version in ("1.0.0", "1.0.0-rc.1", "2.3.4"):
            self.assertEqual(rg.release(root, root, version)["status"], "1.x-requires-v1-gate")

    def test_missing_claim_file_is_no_claim_and_blocks_one_point_oh(self):
        root = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, True)
        self.assertTrue(rg.release(root, root, "0.4.0")["eligible"])
        self.assertFalse(rg.release(root, root, "1.0.0")["eligible"])

    def test_no_claim_cannot_smuggle_evidence_fields(self):
        root = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, root, True)
        (root / "claim.json").write_text(json.dumps({"schema": rg.CLAIM_SCHEMA, "claim": "none", "reason": "r", "families": ["x"]}))
        with self.assertRaisesRegex(ValueError, "only its reason"):
            rg.release(root, root, "0.4.0")

    def test_supported_family_claim_names_benchmark_task_set_model_budget_and_hardware(self):
        assessment = self.fixture().assess()
        self.assertTrue(assessment["eligible"], assessment)
        report = assessment["report"]
        self.assertEqual(report["benchmark"], "fixture benchmark")
        self.assertEqual(report["hardware"], "fixture hardware")
        self.assertEqual(report["models"], ["haiku", "opus"])
        self.assertEqual(report["budget"], {"ModelTokens": 1000, "ModelCalls": 10, "WallSeconds": 60})
        self.assertEqual(report["task_set"], {name: sha(data) for name, data in SEALS.items()})
        self.assertIn("raw.json", report["artifacts"])
        speedups = assessment["families"][0]["GeometricMeanSpeedup"]
        self.assertGreater(min(value["GeometricMean"] for value in speedups.values()), 1.9)
        # The criterion names three conditions, so each config reports all three.
        for config, value in speedups.items():
            with self.subTest(config=config):
                self.assertGreater(value["LowerBound95"], 1.0, "a 95% interval above 1.0")
                self.assertGreaterEqual(value["WorstSeedRatio"], rg.MAXIMUM_SEED_REGRESSION)
                self.assertLessEqual(value["WorstSeedRatio"], value["GeometricMean"])

    def test_mixed_outcome_limits_the_claim_and_keeps_the_loss_visible(self):
        fixture = self.fixture(raw=lambda registration: rows(registration, sr_ours=0.9))
        assessment = fixture.assess()
        self.assertTrue(assessment["eligible"])
        sr = next(f for f in assessment["families"] if f["Family"] == "sr")
        self.assertFalse(sr["Supported"])
        self.assertTrue(all(not endpoint["Superior"] for endpoint in sr["Endpoints"]))
        fixture.claim["families"] = ["algotune", "sr"]
        fixture.write_claim()
        self.assertFalse(fixture.assess()["eligible"])

    def test_superior_but_below_geomean_floor_is_not_supported(self):
        fixture = self.fixture(raw=lambda registration: rows(registration, ours=1.05))
        assessment = fixture.assess()
        algotune = assessment["families"][0]
        self.assertTrue(algotune["HolmSuperiorAllConfigs"])
        self.assertFalse(algotune["MeetsSpeedupFloor"])
        self.assertLess(max(value["GeometricMean"] for value in algotune["GeometricMeanSpeedup"].values()),
                        rg.MINIMUM_GEOMEAN_SPEEDUP)
        self.assertFalse(assessment["eligible"])

    def test_a_mean_above_the_floor_whose_interval_straddles_one_is_not_supported(self):
        """The point estimate was the whole gate, and a noisy win passes a point estimate.

        Seeds alternating well above and well below parity average above 1.10 while the paired
        bootstrap's 95% interval reaches under 1.0 - which is the condition #30 names and the gate
        did not have.
        """
        def raw(registration):
            result = rows(registration)
            for row in result:
                if row["Family"] == "algotune" and row["Arm"] == "aidotnet":
                    index = registration["Seeds"].index(row["Seed"])
                    # Review caught the first version of this using 6.0 and 0.26, where 0.26 also
                    # breaks the per-seed limit - so the test passed with the interval check removed,
                    # and isolated nothing. These two straddle parity while staying inside the limit.
                    row["Value"] = 1.29 if index % 2 == 0 else 0.95
            return result

        algotune = self.fixture(raw=raw).assess()["families"][0]
        speedup = algotune["GeometricMeanSpeedup"]["recommended"]
        self.assertGreater(speedup["GeometricMean"], rg.MINIMUM_GEOMEAN_SPEEDUP, "the point estimate passes")
        self.assertGreaterEqual(speedup["WorstSeedRatio"], rg.MAXIMUM_SEED_REGRESSION,
                                "and so does the per-seed limit, so only the interval can reject this")
        self.assertLess(speedup["LowerBound95"], 1.0, "but the interval reaches under parity")
        self.assertFalse(algotune["MeetsSpeedupFloor"])
        self.assertFalse(algotune["Supported"])

    def test_a_mean_above_the_floor_hiding_one_regressed_seed_is_not_supported(self):
        """A geometric mean of 1.10+ can hold while one paired seed runs at half speed.

        The limit's grain is the seed, which is the finest `raw.json` has - a row is one
        (Family, Arm, Seed) with a single aggregated Value. A family whose Value averages several
        tasks can still hide one task below the limit; review raised that, and it needs a task
        dimension in the row schema rather than a different threshold.
        """
        def raw(registration):
            result = rows(registration, ours=4.0)
            for row in result:
                if (row["Family"] == "algotune" and row["Arm"] == "aidotnet"
                        and row["Seed"] == registration["Seeds"][0]):
                    row["Value"] = 0.5
            return result

        algotune = self.fixture(raw=raw).assess()["families"][0]
        speedup = algotune["GeometricMeanSpeedup"]["recommended"]
        self.assertGreater(speedup["GeometricMean"], rg.MINIMUM_GEOMEAN_SPEEDUP)
        self.assertLess(speedup["WorstSeedRatio"], rg.MAXIMUM_SEED_REGRESSION)
        self.assertFalse(algotune["MeetsSpeedupFloor"])
        self.assertFalse(algotune["Supported"])

    def test_failed_run_has_no_speedup(self):
        def raw(registration):
            result = rows(registration)
            result[0].update(Status="failed", Value=None)
            return result
        algotune = self.fixture(raw=raw).assess()["families"][0]
        self.assertIsNone(algotune["GeometricMeanSpeedup"]["recommended"])
        self.assertFalse(algotune["Supported"])

    def test_v1_gate_needs_every_family_and_the_published_majority(self):
        self.assertTrue(self.fixture(claim="v1-gate").assess("1.0.0")["eligible"])
        lost = {"p1": {"Ours": 1, "Published": 2}, "p2": {"Ours": 1, "Published": 2}, "p3": {"Ours": 1, "Published": 2}}
        self.assertFalse(self.fixture(claim="v1-gate", published=lost).assess("1.0.0")["eligible"])
        mixed = self.fixture(claim="v1-gate", raw=lambda registration: rows(registration, sr_ours=0.9))
        self.assertFalse(mixed.assess("1.0.0")["eligible"])
        one_config = spec(Families=[dict(Family="algotune", Configs=["recommended"],
                                         Endpoint=dict(Metric="speedup", Direction="maximize", FailureValue=0.0)),
                                    dict(Family="sr", Configs=["recommended", "matched"],
                                         Endpoint=dict(Metric="nmse", Direction="minimize", FailureValue=1e6))])
        self.assertFalse(self.fixture(claim="v1-gate", spec_value=one_config).assess("1.0.0")["eligible"])

    def test_claim_needs_a_95_percent_familywise_level(self):
        self.refused("familywise alpha of at most 0.05", spec_value=spec(FamilywiseAlpha=0.1))

    def test_family_claim_cannot_ship_one_point_oh(self):
        self.assertEqual(self.fixture().assess("1.0.0")["status"], "1.x-requires-v1-gate")

    def test_spend_beyond_the_shared_budget_or_unreported_is_refused(self):
        self.refused("exceeds the shared budget", raw=lambda registration: rows(registration, tokens=1001))

        def unreported(registration):
            result = rows(registration)
            result[3]["ModelCalls"] = None
            return result
        self.refused("ModelCalls is unreported", raw=unreported)

    def test_report_that_does_not_regenerate_is_refused(self):
        def edit(fixture, bundle):
            report = json.loads((bundle / "report.json").read_text())
            report["OpenEvolveGate"] = True
            report["Endpoints"][0]["Superior"] = True
            data = (json.dumps(report, indent=2, sort_keys=True) + " \n").encode()
            (bundle / "report.json").write_bytes(data)
            manifest = json.loads((bundle / "manifest.json").read_text())
            manifest["artifacts"]["report.json"] = sha(data)
            (bundle / "manifest.json").write_text(json.dumps(manifest))
        self.refused("regenerate byte-for-byte", mutate_bundle=edit)

    def test_altered_artifact_and_unlisted_file_are_refused(self):
        self.refused("digest mismatch", mutate_bundle=lambda f, bundle: (bundle / "pilot.json").write_bytes(b'{"x": 1}'))
        self.refused("manifest differ", mutate_bundle=lambda f, bundle: (bundle / "extra.json").write_bytes(b"{}"))

    def test_missing_power_analysis_is_refused(self):
        def drop(fixture, bundle):
            (bundle / "power-analysis.json").unlink()
            manifest = json.loads((bundle / "manifest.json").read_text())
            del manifest["artifacts"]["power-analysis.json"]
            (bundle / "manifest.json").write_text(json.dumps(manifest))
        self.refused("missing", mutate_bundle=drop)

    def test_partition_that_does_not_match_its_seal_is_refused(self):
        def swap(fixture, bundle):
            data = b'{"Family":"algotune","TestSeeds":{"t1":[99]}}'
            (bundle / "partitions" / "algotune.json").write_bytes(data)
            manifest = json.loads((bundle / "manifest.json").read_text())
            manifest["artifacts"]["partitions/algotune.json"] = sha(data)
            (bundle / "manifest.json").write_text(json.dumps(manifest))
        self.refused("does not match its seal", mutate_bundle=swap)

    def test_bundle_registration_must_be_the_committed_bytes(self):
        def reformat(fixture, bundle):
            data = json.dumps(json.loads((bundle / "registration.json").read_text()), indent=4).encode()
            (bundle / "registration.json").write_bytes(data)
            manifest = json.loads((bundle / "manifest.json").read_text())
            manifest["artifacts"]["registration.json"] = sha(data)
            (bundle / "manifest.json").write_text(json.dumps(manifest))
        self.refused("differs from the committed registration", mutate_bundle=reformat)

    def test_partition_visible_at_registration_is_refused(self):
        self.refused("in the repository when the registration was frozen", leak_partition=True)

    def test_same_commit_registration_is_refused(self):
        fixture = self.fixture()
        fixture.claim["registration_commit"] = subprocess.check_output(
            ["git", "-C", str(fixture.root), "rev-parse", "HEAD"], text=True).strip()
        fixture.write_claim()
        with self.assertRaisesRegex(ValueError, "same commit"):
            fixture.assess()

    def test_registration_edited_after_commit_is_refused(self):
        def edit(fixture, bundle):
            path = fixture.root / "release" / "registrations" / "fixture" / "registration.json"
            path.write_bytes(path.read_bytes().replace(b"release-gate-fixture", b"release-gate-fixturf"))
        self.refused("changed after it was committed", mutate_bundle=edit)

    def test_registration_with_broken_self_hash_is_refused(self):
        self.assertRaisesRegex(ValueError, "changed after freezing", rg.frozen_registration,
                               json.dumps(dict(Schema=cr.SCHEMA, Spec=spec(), Seeds=[1, 2], RegistrationSha256="0" * 64)).encode())

    def test_duplicate_keys_nan_and_path_escapes_are_refused(self):
        self.assertRaisesRegex(ValueError, "Duplicate JSON key", rg.strict_json, b'{"a": 1, "a": 2}', "x")
        self.assertRaisesRegex(ValueError, "Non-finite", rg.strict_json, b'{"a": NaN}', "x")
        for bad in ("../outside", "a/../../b", "/abs", "a\\b", "c:/x", ""):
            self.assertRaises(ValueError, rg.relative_path, bad, "path")

    def test_bundle_outside_the_release_directory_is_refused(self):
        fixture = self.fixture()
        fixture.claim["bundle"] = "../release/bundles/fixture"
        fixture.write_claim()
        with self.assertRaisesRegex(ValueError, "traversal"):
            fixture.assess()


class MainAlwaysWritesAnAssessmentTests(unittest.TestCase):
    """An unexpected exception must still leave a parseable invalid-evidence assessment (#240 review)."""

    def test_unexpected_exception_writes_invalid_evidence_and_exits_non_zero(self):
        import sys
        from unittest import mock

        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "release-evidence.json"
            argv = ["release_gate.py", "--release", temporary, "--version", "0.4.0", "--output", str(output)]
            with mock.patch.object(rg, "release", side_effect=OverflowError("int too large to convert to float")), \
                    mock.patch.object(sys, "argv", argv), mock.patch("builtins.print"):
                with self.assertRaises(SystemExit) as exit_:
                    rg.main()
            self.assertEqual(1, exit_.exception.code)
            assessment = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual("invalid-evidence", assessment["status"])
            self.assertFalse(assessment["eligible"])
            self.assertIn("OverflowError", assessment["error"])

if __name__ == "__main__":
    unittest.main()