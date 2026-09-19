from pathlib import Path
import unittest

from scipy_baseline import Bridge
from ribs_baseline import run_one

DLL = Path(__file__).resolve().parents[1] / "AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll"


class RibsBaselineTests(unittest.TestCase):
    def test_real_cma_me_updates_and_partial_batches_share_exact_evaluation_work(self):
        for task in ("rastrigin", "knapsack", "stochastic-regression", "robust-design"):
            with self.subTest(task=task):
                bridge = Bridge(DLL, task, 37, 25, instance_seed=42)
                try:
                    manifest = bridge.receive()
                finally:
                    bridge.close()
                row = run_one(DLL, task, 42, 37, 25, manifest["InitialPopulationHash"])
                self.assertEqual("completed", row["status"], row["error"])
                self.assertEqual(25, row["independent_evaluator_calls"])
                self.assertEqual(2, row["state"]["full_batch_updates"])
                self.assertEqual(1, row["state"]["partial_batch_measurements"])
                self.assertGreater(row["state"]["occupied_cells"], 0)
                self.assertFalse(row["unknown_work"])


if __name__ == "__main__":
    unittest.main()
