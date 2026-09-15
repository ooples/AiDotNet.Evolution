import copy
import hashlib
import itertools
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import ablation


class AblationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.binary = self.root / "runner.dll"
        self.binary.write_bytes(b"runner")
        (self.root / "AiDotNet.Evolution.dll").write_bytes(b"core")
        self.plan_path = self.root / "plan.json"
        self.plan = ablation.register(self.binary, self.plan_path, seeds=2, budget=16)

    def result(self):
        request = dict(Protocol=ablation.PROTOCOL, Partition="development", Budget=16,
                       Seeds=self.plan["DevelopmentSeeds"], Configurations=ablation.matrix())
        tasks = dict(numeric="rippled-quadratic", program="symbolic-quadratic-cosine", kernel="blocked-matrix-product")
        rows = []
        for family, seed, config in itertools.product(ablation.FAMILIES, request["Seeds"], request["Configurations"]):
            observation = dict(Genome="a" * 64, Quality=0.5, TimingsMilliseconds=[1, 1, 1, 1] if family == "kernel" else [],
                               PrimitiveCases=dict(numeric=4, program=2048, kernel=5 * 24 ** 3)[family])
            rows.append(dict(Family=family, Task=tasks[family], Seed=seed, Configuration=config["Name"], InitialHash="a" * 64,
                             Status="completed", Calls=16, Proposals=16, Quality=0.5, Diversity=0.25, Seconds=1, Success=False,
                             Resources=dict(Spent=dict(cost_units=16, proposal_calls=8), Reserved={}, Unknown=0, MaximumViolated=False),
                             Observations=[copy.deepcopy(observation) for _ in range(16)]))
        return dict(Request=request, BenchmarkHash=self.plan["BenchmarkHash"], CoreHash=self.plan["CoreHash"], Rows=rows,
                    RequestHash=hashlib.sha256(json.dumps(request, indent=2).encode()).hexdigest())

    def test_full_matrix_verifies(self):
        self.assertEqual(114, len(ablation.verify(self.plan, self.result(), "development", ablation.matrix())))

    def test_request_hash_uses_actual_bytes_including_windows_newlines(self):
        result = self.result()
        raw = json.dumps(result["Request"], indent=2).replace("\n", "\r\n").encode()
        result["RequestHash"] = hashlib.sha256(raw).hexdigest()
        self.assertEqual(114, len(ablation.verify(self.plan, result, "development", ablation.matrix(), result["RequestHash"])))
        with self.assertRaises(ValueError):
            ablation.verify(self.plan, result, "development", ablation.matrix(), "0" * 64)

    def test_adversarial_receipts_are_rejected(self):
        for corruption in ("missing", "duplicate", "pair", "cost", "cap", "quality", "timing", "partition", "success", "proposal", "work", "raw"):
            with self.subTest(corruption=corruption):
                result = self.result()
                row = result["Rows"][0]
                if corruption == "missing": result["Rows"].pop()
                elif corruption == "duplicate": result["Rows"].append(copy.deepcopy(row))
                elif corruption == "pair": row["InitialHash"] = "b" * 64
                elif corruption == "cost": row["Resources"]["Unknown"] = 1
                elif corruption == "cap": row["Calls"] = 17
                elif corruption == "quality": row["Quality"] = float("nan")
                elif corruption == "timing": result["Rows"][-1]["Observations"][0]["TimingsMilliseconds"] = [1, 1, 1]
                elif corruption == "partition": row["Task"] = "coupled-absolute"
                elif corruption == "success": row["Success"] = True
                elif corruption == "proposal": row["Resources"]["Spent"]["proposal_calls"] = 7
                elif corruption == "work": row["Observations"][0]["PrimitiveCases"] = 0
                else: row["Observations"].pop()
                with self.assertRaises(ValueError):
                    ablation.verify(self.plan, result, "development", ablation.matrix())

    def test_failures_remain_zero_in_denominator(self):
        result = self.result()
        row = result["Rows"][0]
        row.update(Status="failed", Quality=0, Diversity=0, Success=False)
        self.assertEqual(114, len(ablation.verify(self.plan, result, "development", ablation.matrix())))
        self.assertEqual(0, ablation.endpoint(row))
        row["Quality"] = 0.1
        with self.assertRaises(ValueError):
            ablation.verify(self.plan, result, "development", ablation.matrix())

    def test_analysis_criteria_cannot_be_retuned(self):
        for key, value in (("Alpha", 0.9), ("MinimumGain", -1), ("QualityWeight", 1), ("ConfirmationSeeds", [17001, 17002])):
            with self.subTest(key=key):
                plan = copy.deepcopy(self.plan)
                plan[key] = value
                with self.assertRaises(ValueError): ablation.validate_plan(plan)

    def test_binary_change_prevents_execution(self):
        self.binary.write_bytes(b"changed")
        with patch("ablation.subprocess.run") as run:
            with self.assertRaises(ValueError): ablation.execute(self.plan_path, self.root / "run", "development")
            run.assert_not_called()

    def test_interrupted_execution_cannot_be_retried_in_another_directory(self):
        with patch("ablation.subprocess.run", side_effect=RuntimeError("interrupted")) as run:
            with self.assertRaises(RuntimeError): ablation.execute(self.plan_path, self.root / "run", "development")
            with self.assertRaises(FileExistsError): ablation.execute(self.plan_path, self.root / "retry", "development")
            self.assertEqual(1, run.call_count)

    def test_plan_and_results_refuse_overwrites(self):
        with self.assertRaises(FileExistsError): ablation.register(self.binary, self.plan_path, seeds=2, budget=16)

    def test_duplicate_json_is_rejected(self):
        path = self.root / "duplicate.json"
        path.write_text('{"a": 1, "a": 2}')
        with self.assertRaises(ValueError): ablation.load(path)


if __name__ == "__main__":
    unittest.main()
