import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from design import digest
from program_diagnostics import fingerprint
from selection_study import execute, schedule, validate
from program_tasks import TASKS


def registration():
    return dict(schema="selection-development-v1",claim="none",schedule=schedule(),iterations=4,
                timing_samples=3,codex=None,model_calls_maximum=0,tasks={t:{} for t in TASKS},artifacts={},
                image="fixture",upstream="fixture",openevolve="fixture",dll="fixture",model="fixture")


class SelectionStudyTests(unittest.TestCase):
    def test_fixed_schedule_balances_profile_order_and_pairs_instances(self):
        blocks = schedule()
        self.assertEqual(["uniform","best","best","uniform"],[b["profile"] for b in blocks])
        for first,second in ((blocks[0],blocks[1]),(blocks[2],blocks[3])):
            self.assertEqual(first["seed"],second["seed"])
            self.assertEqual(first["tasks"],second["tasks"])
            self.assertEqual(first["diagnostic_instance_seed"],second["diagnostic_instance_seed"])
            self.assertNotEqual(first["search_instance_seed"],first["diagnostic_instance_seed"])

    def test_budget_or_schedule_extension_refused(self):
        for key,value in (("iterations",8),("model_calls_maximum",1000),("schedule",schedule()*2)):
            plan = registration()
            plan[key] = value
            with self.assertRaises(ValueError):
                validate(plan)

    def test_failed_dispatch_retains_all_cells_costs_and_consumption(self):
        plan = registration()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "registration.json").write_text(json.dumps(plan))
            with patch("selection_study.pilot",side_effect=RuntimeError("fixture failure")) as dispatch:
                result = execute(root,digest(plan))
                self.assertEqual("failed",result["status"])
                self.assertEqual(72,len(result["rows"]))
                self.assertTrue(all(r["status"] == "missing" for r in result["rows"]))
                self.assertEqual(1,dispatch.call_count)
                with self.assertRaises(FileExistsError):
                    execute(root,digest(plan))
                self.assertEqual(1,dispatch.call_count)

    def test_unbound_plan_refused_before_dispatch(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "registration.json").write_text(json.dumps(registration()))
            with patch("selection_study.pilot") as dispatch, self.assertRaises(ValueError):
                execute(root,"wrong")
            dispatch.assert_not_called()

    def test_structural_diagnostic_is_not_exact_source_identity(self):
        a,b = fingerprint("x=1\n"),fingerprint("# different source\nx = 1\n")
        self.assertNotEqual(a[0],b[0])
        self.assertEqual(a[1],b[1])
        self.assertNotEqual(a[1],fingerprint("x=2\n")[1])

    def test_fingerprint_never_executes_code_and_bounds_inputs(self):
        self.assertIsNotNone(fingerprint("raise RuntimeError('must not run')")[1])
        self.assertIsNone(fingerprint("def invalid(")[1])
        with self.assertRaises(ValueError):
            fingerprint("x"*65537)
