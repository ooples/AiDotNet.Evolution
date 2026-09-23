"""V1-04a contracts. Needs EVOLUTION_ALPHAEVOLVE_RESULTS: google-deepmind/alphaevolve_results at SOURCE_COMMIT."""
import os
from pathlib import Path
import unittest

import numpy as np

import alphaevolve_math as am

NOTEBOOK_SHA256 = "be1e1f31bc3e6c789ece06b3489901981039f0e08248f7f1b5f7d1bd40d9ad51"


def published():
    root = os.environ.get("EVOLUTION_ALPHAEVOLVE_RESULTS")
    if not root:
        raise RuntimeError("Set EVOLUTION_ALPHAEVOLVE_RESULTS to the pinned alphaevolve_results checkout")
    cells, _ = am.data_cells(Path(root) / "mathematical_results.ipynb", NOTEBOOK_SHA256)
    return cells


class TensorTests(unittest.TestCase):
    def test_matmul_tensor_is_the_product_rule(self):
        # <1,1,1> is the scalar product; <2,2,2> has exactly 8 unit entries.
        self.assertEqual(1, int(am.matmul_tensor(1, 1, 1).sum()))
        self.assertEqual(8, int(am.matmul_tensor(2, 2, 2).sum()))
        strassen_naive = tuple(np.eye(4)[:, [0, 0, 1, 1, 2, 2, 3, 3]] for _ in range(3))
        self.assertIsNone(am.verify_tensor(strassen_naive, 2, 2, 2, "Z"))

    def test_every_published_decomposition_verifies_at_its_published_rank(self):
        problems = am.tensor_problems(published())
        self.assertEqual(15, len(problems))
        self.assertEqual([], [r for r in am.self_test(problems) if r["verified"] != r["published"]])

    def test_corrupted_decompositions_are_rejected(self):
        rng = np.random.default_rng(0)
        for problem in am.tensor_problems(published()):
            a, b, c = (np.array(f) for f in problem["construction"])
            scale = am.RINGS[problem["parameters"]["ring"]]
            broken = a.copy()
            broken[rng.integers(broken.shape[0]), rng.integers(broken.shape[1])] += 1.0 / scale
            off_ring = a.copy()
            off_ring[0, 0] += 0.25
            for candidate in ((broken, b, c), (off_ring, b, c), (a[:, :-1], b[:, :-1], c[:, :-1]), (a, b)):
                with self.subTest(problem=problem["id"]):
                    self.assertIsNone(am.verify(problem, candidate))

    def test_only_data_cells_run_and_imports_are_restricted(self):
        with self.assertRaises(ImportError):
            am._safe_import("os")
        self.assertIs(np, am._safe_import("numpy"))


if __name__ == "__main__":
    unittest.main()