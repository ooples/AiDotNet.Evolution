import copy
import itertools
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import portfolio_study as study
from ablation import digest, load, write_new


class PortfolioStudyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name); self.plan_path = self.root / "plan.json"
        binary = self.root / "runner.dll"; binary.write_bytes(b"runner")
        (self.root / "AiDotNet.Evolution.dll").write_bytes(b"core")
        study.register(binary, self.plan_path, count=2, budget=32)
        self.plan = load(self.plan_path)

    def rows(self):
        result = []
        for family, method, seed in itertools.product(study.TASKS["development"], study.METHODS, self.plan["DevelopmentSeeds"]):
            result.append(dict(Family=family, Task=study.TASKS["development"][family], Method=method, Seed=seed, InitialHash="paired",
                Quality=0.5, Status="completed", ProposalCalls=0, ObjectiveCalls=8, Credits=[], Observations=[{}] * 8,
                Resources=dict(Spent=dict(cost_units=8), Reserved={}, Unknown=0, MaximumViolated=False, Receipts=[])))
        return result

    def write_result(self, rows):
        request_path = self.root / "development.request.json"
        request = dict(Partition="development", Budget=32, Seeds=self.plan["DevelopmentSeeds"])
        request_path.write_text(json.dumps(request))
        (self.root / "development.json").write_text(json.dumps(dict(Protocol="portfolio-study-v1", Request=request, Rows=rows,
            RequestHash=digest(request_path), BenchmarkHash=self.plan["BenchmarkHash"], CoreHash=self.plan["CoreHash"])))

    def test_complete_paired_schedule_verifies(self):
        self.write_result(self.rows())
        self.assertEqual(36, len(study.verify(self.plan, self.root, "development")))

    def test_missing_duplicate_unpaired_free_refinement_and_invalid_rewards_are_refused(self):
        for corruption in ("missing", "duplicate", "pair", "cost", "nonfinite", "task", "notification"):
            with self.subTest(corruption=corruption):
                rows = self.rows()
                if corruption == "missing": rows.pop()
                elif corruption == "duplicate": rows.append(copy.deepcopy(rows[0]))
                elif corruption == "pair": rows[0]["InitialHash"] = "changed"
                elif corruption == "cost": rows[0]["ObjectiveCalls"] = 9
                elif corruption == "nonfinite": rows[0]["Quality"] = float("nan")
                elif corruption == "task": rows[0]["Task"] = "consumed-ablation-task"
                else: rows[-1]["Credits"] = [dict(Generation=1), dict(Generation=1)]
                self.write_result(rows)
                with self.assertRaises(ValueError): study.verify(self.plan, self.root, "development")

    def test_criteria_and_confirmation_seeds_cannot_be_changed(self):
        for key, value in (("Alpha", 0.5), ("MinimumGain", -0.1), ("ConfirmationSeeds", self.plan["DevelopmentSeeds"])):
            changed = copy.deepcopy(self.plan); changed[key] = value
            with self.assertRaises(ValueError): study.validate(changed)

    def test_best_static_is_selected_from_development_not_adaptive_winners(self):
        rows = self.rows()
        for row in rows:
            row["Quality"] = 1 if row["Method"].startswith("adaptive") else 0.8 if row["Method"] == "crossover" else 0.3
        self.assertEqual(dict.fromkeys(study.TASKS["development"], "crossover"), study.best_static(rows))

    def test_interrupted_registration_is_consumed_even_in_another_directory(self):
        with patch("portfolio_study.subprocess.run", side_effect=RuntimeError("interrupted")) as run:
            with self.assertRaises(RuntimeError): study.execute(self.plan_path, self.root / "first", "development")
            with self.assertRaises(FileExistsError): study.execute(self.plan_path, self.root / "second", "development")
            self.assertEqual(1, run.call_count)


if __name__ == "__main__": unittest.main()
