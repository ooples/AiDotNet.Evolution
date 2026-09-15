import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import numpy as np

from algotune_worker import finite_solution, fingerprint, normalized_source
from protocol import METHODS, PARTITIONS, catalog, configuration, digest, freeze, load_plan, materialize, prepare, read_json, write_new


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="aidotnet-suite-test-")
        self.root = Path(self.temporary.name)
        self.directory = self.root / "plan"

    def tearDown(self):
        self.temporary.cleanup()

    def plan(self, **kwargs):
        return prepare(self.directory, source_revision="a" * 40, instances=1, replicates=2, budget=16, **kwargs)

    def selection(self, plan, **changes):
        report = {"plan_hash": plan["plan_hash"], "partition": "selection", "status": "completed", "runtime_contract": {"test": "fixture"}}
        report.update(changes)
        path = self.root / "selection.json"
        write_new(path, report)
        return path

    def config(self):
        path = self.root / "configuration.json"
        write_new(path, {"schema": "aidotnet-suite-configuration-v1", "numeric_methods": list(METHODS[:3]), "program_solver": "upstream-reference"})
        return path

    def test_catalog_has_predeclared_subset_and_disjoint_families(self):
        value = catalog()
        self.assertEqual(11, len(value["algotune"]))
        self.assertEqual(9, len(value["numeric"]))
        all_tasks = value["numeric"] + value["algotune"]
        for left in PARTITIONS:
            for right in PARTITIONS:
                if left != right:
                    self.assertTrue({t["family"] for t in all_tasks if t["partition"] == left}.isdisjoint(
                        {t["family"] for t in all_tasks if t["partition"] == right}))
        features = {feature for task in value["numeric"] for feature in task["features"]}
        self.assertTrue({"deceptive", "multimodal", "constrained", "noisy", "expensive"} <= features)

    def test_rejects_relabeling_one_family_as_an_independent_partition(self):
        value = catalog()
        value["numeric"][3]["family"] = value["numeric"][0]["family"].upper()
        path = self.root / "invalid-catalog.json"
        write_new(path, value)
        with self.assertRaises(ValueError):
            catalog(path)

    def test_private_final_root_is_not_in_public_plan(self):
        plan = self.plan()
        private = read_json(self.directory / "final-private.json")
        self.assertNotIn(private["root"], json.dumps(plan))
        self.assertEqual(plan["commitments"]["final"], hashlib.sha256(bytes.fromhex(private["root"])).hexdigest())

    def test_development_instances_replay_without_accessing_final(self):
        self.plan()
        self.assertEqual(materialize(self.directory, "development"), materialize(self.directory, "development"))
        development = materialize(self.directory, "development")[2]
        selection = materialize(self.directory, "selection")[2]
        self.assertTrue({entry["seed"] for entry in development["numeric"]}.isdisjoint({entry["seed"] for entry in selection["numeric"]}))
        self.assertFalse((self.directory / "final-consumed.json").exists())

    def test_final_requires_selection_then_consumes_exactly_once(self):
        plan = self.plan()
        with self.assertRaises(FileNotFoundError):
            materialize(self.directory, "final", claim_final=True)
        selected = freeze(self.directory, self.config(), self.selection(plan))
        with self.assertRaises(ValueError):
            materialize(self.directory, "final")
        _, config_hash, panels, config = materialize(self.directory, "final", claim_final=True)
        self.assertEqual(selected["configuration_hash"], config_hash)
        self.assertEqual(list(METHODS[:3]), config["numeric_methods"])
        self.assertEqual(3, len(panels["numeric"]))
        self.assertTrue((self.directory / "final-consumed.json").exists())
        with self.assertRaises(FileExistsError):
            materialize(self.directory, "final", claim_final=True)

    def test_failed_selection_cannot_freeze(self):
        plan = self.plan()
        with self.assertRaises(ValueError):
            freeze(self.directory, self.config(), self.selection(plan, status="incomplete"))
        self.assertFalse((self.directory / "frozen.json").exists())

    def test_wrong_partition_cannot_freeze(self):
        plan = self.plan()
        with self.assertRaises(ValueError):
            freeze(self.directory, self.config(), self.selection(plan, partition="development"))

    def test_another_plans_report_cannot_freeze(self):
        plan = self.plan()
        with self.assertRaises(ValueError):
            freeze(self.directory, self.config(), self.selection(plan, plan_hash="b" * 64))

    def test_configuration_cannot_silently_introduce_ignored_knobs_or_drop_controls(self):
        valid = {"schema": "aidotnet-suite-configuration-v1", "numeric_methods": list(METHODS), "program_solver": "upstream-reference"}
        self.assertEqual(valid, configuration(valid))
        for mutation in ({**valid, "model": "unimplemented"}, {**valid, "numeric_methods": ["invented"]},
                         {**valid, "numeric_methods": list(METHODS[2:])}, {**valid, "program_solver": "arbitrary-code"}):
            with self.assertRaises(ValueError):
                configuration(mutation)

    def test_smoke_mode_is_explicit_and_does_not_claim_sealed_final(self):
        plan = self.plan(smoke=True)
        self.assertEqual("contract-smoke", plan["mode"])
        materialize(self.directory, "final")
        self.assertFalse((self.directory / "final-consumed.json").exists())

    def test_tampered_root_or_plan_is_rejected(self):
        self.plan()
        private_path = self.directory / "development-private.json"
        private = read_json(private_path)
        private["root"] = "0" * 64
        private_path.write_text(json.dumps(private), encoding="utf-8")
        with self.assertRaises(ValueError):
            materialize(self.directory, "development")
        plan_path = self.directory / "plan.json"
        plan = read_json(plan_path)
        plan["budget"] += 1
        plan_path.write_text(json.dumps(plan), encoding="utf-8")
        with self.assertRaises(ValueError):
            load_plan(self.directory)

    def test_plans_and_results_are_never_overwritten(self):
        plan = self.plan()
        with self.assertRaises(FileExistsError):
            self.plan()
        with self.assertRaises(FileExistsError):
            write_new(self.directory / "plan.json", plan)

    def test_duplicate_and_nonfinite_json_is_rejected(self):
        for index, text in enumerate(('{"a":1,"a":2}', '{"a":NaN}')):
            path = self.root / str(index)
            path.write_text(text, encoding="utf-8")
            with self.assertRaises(ValueError):
                read_json(path)

    def test_budget_validation_precedes_directory_creation(self):
        with self.assertRaises(ValueError):
            prepare(self.directory, source_revision="a" * 40, budget=7)
        self.assertFalse(self.directory.exists())

    def test_source_pins_allow_git_line_endings_but_not_code_changes(self):
        task = {"id": "fixture"}
        path = self.root / "AlgoTuneTasks" / "fixture" / "fixture.py"
        path.parent.mkdir(parents=True)
        source = b"value = 1\n"
        task["blob"] = hashlib.sha1(b"blob " + str(len(source)).encode() + b"\0" + source).hexdigest()
        path.write_bytes(source.replace(b"\n", b"\r\n"))
        self.assertEqual(source, normalized_source(self.root, task))
        path.write_bytes(b"value = 2\n")
        with self.assertRaises(ValueError):
            normalized_source(self.root, task)

    def test_problem_fingerprints_preserve_structure_and_normalize_array_endianness(self):
        value = {"binary": b"abc", "tuple": (np.array([1.0, 2.0]), None), 7: [1, 2]}
        self.assertEqual(fingerprint(value), fingerprint(copy.deepcopy(value)))
        self.assertEqual(fingerprint(np.array([1.0], dtype="<f8")), fingerprint(np.array([1.0], dtype=">f8")))
        self.assertNotEqual(fingerprint([1, 2]), fingerprint((1, 2)))
        with self.assertRaises(ValueError):
            fingerprint(np.array([{}], dtype=object))

    def test_nonfinite_outputs_cannot_exploit_an_upstream_tolerance_check(self):
        self.assertFalse(finite_solution(np.array([float("nan")])))
        self.assertFalse(finite_solution({"solution": [1.0, float("inf")]}))
        self.assertTrue(finite_solution({"distance_matrix": [[0.0, None], [None, 0.0]]}))


if __name__ == "__main__":
    unittest.main()
