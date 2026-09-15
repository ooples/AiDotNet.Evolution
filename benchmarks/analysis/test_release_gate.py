"""Synthetic gate contracts only; passing fixtures never establish competitive results."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
import subprocess

from release_gate import assess, digest, load, release, validate_protocol, verify_bundle


def protocol(count=1000, endpoint="runtime"):
    return dict(schema="evolution-release-protocol-v1", endpoint=endpoint, benchmark="test-contract",
                model="test-only-no-model", model_snapshot="not-applicable", candidate_revision="a"*40, competitor_revision="b"*40,
                hardware="synthetic-no-hardware", cost_metric="test-work",
                correctness_oracle="test-oracle", registration_review="fixture-not-release-approval",
                pilot_sha256=digest(b"{}"), competitor="OpenEvolve", calibrated_on_pilot=True,
                seeds=list(range(count)), tasks=[dict(id="a", family="first", definition_sha256="c"*64, maximum_regression=.1),
                                               dict(id="b", family="second", definition_sha256="d"*64, maximum_regression=.1)],
                alpha=.05, minimum_ratio=1.1 if endpoint == "runtime" else 1.25,
                log_ratio_bounds=[-.5, .5], failure_rule="retain-fallback-infrastructure-blocks",
                search_cost_cap=100, runtime_statistic="median", runtime_samples=3, target_definition="same-test-target")


def observations(p, ratio=1.2):
    rows = []
    for task in p["tasks"]:
        for seed in p["seeds"]:
            sides = {name: dict(status="winner", search_cost=100, program_sha256="a"*64, correct=True,
                                receipt="work", correctness_receipt="oracle", runtime_ms=10 if name == "candidate" else 10*ratio,
                                target_reached=True) for name in ("candidate", "competitor")}
            if p["endpoint"] == "target-cost":
                sides["candidate"]["search_cost"] = 100/ratio
            rows.append(dict(task=task["id"], seed=seed, initial_program_sha256="a"*64, **sides))
    return rows


def bundle(root):
    p = protocol(2)
    rows = observations(p)
    artifacts = {}
    def write(name, value, raw=False):
        data = value if raw else json.dumps(value, allow_nan=False).encode()
        (root/name).write_bytes(data)
        artifacts[name] = digest(data)
    write("pilot.json", b"{}", True)
    write("source.py", b"# synthetic fixture; never executed", True)
    for index, row in enumerate(rows):
        row.update(initial_program="source.py", initial_program_sha256=artifacts["source.py"])
        for system in ("candidate", "competitor"):
            side = row[system]
            side.update(program="source.py", program_sha256=artifacts["source.py"],
                        receipt=f"work-{index}-{system}.json", correctness_receipt=f"oracle-{index}-{system}.json")
            write(side["receipt"], dict(task=row["task"], seed=row["seed"], system=system, cost_components={"all-stages": 100},
                program_sha256=side["program_sha256"], search_cost=100, cost_metric=p["cost_metric"],
                measurement_phase="confirmation", runtime_samples_ms=[side["runtime_ms"]]*3))
            write(side["correctness_receipt"], dict(program_sha256=side["program_sha256"], correct=True,
                oracle=p["correctness_oracle"], phase="confirmation"))
    write("protocol.json", p)
    write("observations.json", rows)
    (root/"manifest.json").write_text(json.dumps(dict(schema="evolution-release-bundle-v1", artifacts=artifacts)))
    return artifacts["protocol.json"]


class GateTests(unittest.TestCase):
    def test_prior_git_registration_and_same_commit_rejection(self):
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)
            root = repo/"release"
            evidence = root/"evidence"
            evidence.mkdir(parents=True)
            identity = bundle(evidence)
            registrations = root/"registrations"
            registrations.mkdir()
            (registrations/"fixture.json").write_bytes((evidence/"protocol.json").read_bytes())
            def git(*args):
                return subprocess.check_output(["git", "-C", str(repo), *args], stderr=subprocess.STDOUT, text=True).strip()
            git("init")
            git("config", "user.name", "Gate fixture")
            git("config", "user.email", "fixture@example.invalid")
            git("config", "core.autocrlf", "false")
            git("add", "release/registrations/fixture.json")
            git("commit", "-m", "Freeze synthetic fixture")
            registration = git("rev-parse", "HEAD")
            claim = dict(schema="evolution-release-claim-v1", claim="declared-task-set", bundle="evidence",
                         protocol_sha256=identity, registration_commit=registration,
                         registered_protocol_path="release/registrations/fixture.json")
            (root/"claim.json").write_text(json.dumps(claim))
            with self.assertRaises(ValueError):
                release(root)
            git("add", "release")
            git("commit", "-m", "Record synthetic observations")
            self.assertFalse(release(root)["eligible"])
            claim["protocol_sha256"] = "0"*64
            (root/"claim.json").write_text(json.dumps(claim))
            with self.assertRaises(ValueError):
                release(root)

    def test_workflow_wiring_preserves_gates(self):
        root = Path(__file__).resolve().parents[2]
        release_workflow = (root/".github/workflows/automated-release.yml").read_text()
        self.assertIn("python3 benchmarks/analysis/release_gate.py --release release", release_workflow)
        self.assertIn("needs: [validate, legacy]", release_workflow)
        workflow = (root/".github/workflows/release-evidence.yml").read_text()
        self.assertIn("github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'", workflow)
        self.assertIn("-Seeds 8 -Budget 128", workflow)
        self.assertNotIn("id-token: write", workflow)
        self.assertIn("contents: read", workflow)

    def test_release_directory_does_not_unignore_private_keys(self):
        root = Path(__file__).resolve().parents[2]
        result = subprocess.run(["git", "-C", str(root), "check-ignore", "--no-index", "release/private_key.pem"], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0)
        self.assertIn("private_key.pem", result.stdout)

    def test_supported_runtime_fixture(self):
        p = protocol()
        result = assess(p, observations(p))
        self.assertTrue(result["eligible"])
        self.assertGreater(result["overall"]["lower"], 1)
        self.assertEqual(result["observations_retained"], 2000)

    def test_twenty_percent_cost_reduction_fixture(self):
        p = protocol(endpoint="target-cost")
        self.assertTrue(assess(p, observations(p, 1.3))["eligible"])
        self.assertFalse(assess(p, observations(p, 1.24))["eligible"])

    def test_insufficient_power_remains_visible(self):
        p = protocol(2)
        result = assess(p, observations(p))
        self.assertFalse(result["eligible"])
        self.assertEqual(len(result["families"]), 2)
        self.assertEqual(result["observations_retained"], 4)

    def test_regressing_family_blocks_aggregate(self):
        p = protocol()
        rows = observations(p, 1.5)
        for row in rows:
            if row["task"] == "b":
                row["competitor"]["runtime_ms"] = 8
        result = assess(p, rows)
        self.assertFalse(result["eligible"])
        self.assertTrue(result["families"][0]["eligible"])
        self.assertFalse(result["families"][1]["eligible"])

    def test_fallback_and_infrastructure(self):
        p = protocol()
        rows = observations(p)
        rows[0]["candidate"]["status"] = "fallback"
        self.assertTrue(assess(p, rows)["eligible"])
        rows[0]["candidate"]["program_sha256"] = "b"*64
        with self.assertRaises(ValueError):
            assess(p, rows)
        rows[0]["candidate"]["status"] = "infrastructure-failure"
        result = assess(p, rows)
        self.assertFalse(result["eligible"])
        self.assertEqual(len(result["blocked"]), 1)

    def test_unfair_costs_and_invalid_winners(self):
        p = protocol(2)
        for field, value in [("search_cost", 99), ("search_cost", 101), ("search_cost", True),
                             ("runtime_ms", float("nan")), ("runtime_ms", 0), ("runtime_ms", 1),
                             ("correct", False), ("status", "unstarted")]:
            rows = observations(p)
            rows[0]["candidate"][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                assess(p, rows)

    def test_missing_duplicate_and_extra_pairs(self):
        p = protocol(2)
        rows = observations(p)
        for changed in (rows[:-1], rows+[rows[0]], [rows[0]]*len(rows)):
            with self.assertRaises(ValueError):
                assess(p, changed)

    def test_unfrozen_or_uncalibrated_protocol(self):
        for field, value in [("endpoint", "best-after-looking"), ("minimum_ratio", 1.01), ("alpha", .1),
                             ("calibrated_on_pilot", False), ("seeds", [1, 1]), ("model", ""),
                             ("log_ratio_bounds", [0, 1]), ("runtime_samples", 1)]:
            p = protocol(2)
            p[field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                validate_protocol(p)

    def test_target_not_reached_is_not_free_success(self):
        p = protocol(2, "target-cost")
        rows = observations(p, 1.3)
        rows[0]["candidate"]["status"] = "target-not-reached"
        self.assertFalse(assess(p, rows)["eligible"])

    def test_complete_bundle_integrity(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            identity = bundle(root)
            self.assertFalse(verify_bundle(root, identity)["eligible"])
            with self.assertRaises(ValueError):
                verify_bundle(root, "0"*64)
            (root/"source.py").write_text("altered")
            with self.assertRaises(ValueError):
                verify_bundle(root, identity)

    def test_rehashed_corrupt_receipts_are_rejected(self):
        for field, value in [("search_cost", 99), ("system", "other"), ("measurement_phase", "search"),
                             ("runtime_samples_ms", [1, 2, 3]), ("cost_components", {"partial": 1})]:
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                identity = bundle(root)
                path = root/"work-0-candidate.json"
                changed = load(path)
                changed[field] = value
                path.write_text(json.dumps(changed))
                manifest = load(root/"manifest.json")
                manifest["artifacts"][path.name] = digest(path.read_bytes())
                (root/"manifest.json").write_text(json.dumps(manifest))
                with self.subTest(field=field), self.assertRaises(ValueError):
                    verify_bundle(root, identity)

    def test_duplicate_json_and_path_escape(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            identity = bundle(root)
            (root/"manifest.json").write_text('{"schema":"x","schema":"y"}')
            with self.assertRaises(ValueError):
                verify_bundle(root, identity)
            identity = bundle(root)
            manifest = load(root/"manifest.json")
            manifest["artifacts"]["../outside"] = "a"*64
            (root/"manifest.json").write_text(json.dumps(manifest))
            with self.assertRaises((ValueError, FileNotFoundError)):
                verify_bundle(root, identity)

    def test_no_claim_is_not_superiority(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            claim = dict(schema="evolution-release-claim-v1", claim="none", reason="No qualifying campaign")
            (root/"claim.json").write_text(json.dumps(claim))
            self.assertEqual(release(root)["claim"], "none")
            claim["bundle"] = "hidden"
            (root/"claim.json").write_text(json.dumps(claim))
            with self.assertRaises(ValueError):
                release(root)


if __name__ == "__main__":
    unittest.main()
