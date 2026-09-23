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


class AnalyticTests(unittest.TestCase):
    def setUp(self):
        self.problems = {p["id"]: p for p in am.b_problems(published())}

    def test_every_published_b_construction_reproduces_its_published_value(self):
        self.assertEqual(16, len(self.problems))
        self.assertEqual([], [r for r in am.self_test(self.problems.values()) if not r["matches"]])

    def test_constraint_violations_are_rejected_not_scored(self):
        p = self.problems
        h1 = np.array(p["c1-autocorrelation"]["construction"], dtype=float)
        negative = h1.copy(); negative[3] = -0.5
        self.assertIsNone(am.verify(p["c1-autocorrelation"], negative))
        erdos = np.array(p["c5-erdos-overlap"]["construction"], dtype=float)
        over = erdos.copy(); over[0] = 1.5
        shifted = erdos.copy(); shifted[0] = min(1.0, shifted[0] + 0.01)
        self.assertIsNone(am.verify(p["c5-erdos-overlap"], over))
        if shifted[0] != erdos[0]:
            self.assertIsNone(am.verify(p["c5-erdos-overlap"], shifted), "sum must stay exactly n/2")
        u = np.array(p["c6-sums-differences-2003"]["construction"])
        self.assertIsNone(am.verify(p["c6-sums-differences-2003"], u + 1), "U must contain 0")
        self.assertIsNone(am.verify(p["c6-sums-differences-2003"], np.append(u, u[5])), "U is a set")
        self.assertIsNone(am.verify(p["c6-sums-differences-2003"], u.astype(float)), "integers only")
        circles = np.array(p["circles-square-26"]["construction"], dtype=float)
        grown = circles.copy(); grown[0, 2] *= 1.05
        outside = circles.copy(); outside[1, 0] = 1.0
        self.assertIsNone(am.verify(p["circles-square-26"], grown), "overlap or overflow")
        self.assertIsNone(am.verify(p["circles-square-26"], outside))
        rectangle = np.array(p["circles-rectangle-21"]["construction"], dtype=float)
        stretched = rectangle.copy(); stretched[0, 0] += 0.2
        self.assertIsNone(am.verify(p["circles-rectangle-21"], stretched), "perimeter or overlap")
        triangle = np.array(p["heilbronn-triangle-11"]["construction"], dtype=float)
        escaped = triangle.copy(); escaped[0] = [10.0, 10.0]
        self.assertIsNone(am.verify(p["heilbronn-triangle-11"], escaped), "points stay inside the triangle")
        ratio = np.array(p["maxmin-ratio-2d-16"]["construction"], dtype=float)
        collapsed = ratio.copy(); collapsed[1] = collapsed[0]
        self.assertIsNone(am.verify(p["maxmin-ratio-2d-16"], collapsed))
        centers = np.array(p["kissing-11"]["construction"])
        close = centers.copy(); close[1] = close[0] // 2 + close[1] // 2
        self.assertIsNone(am.verify(p["kissing-11"], close), "a too-close pair breaks the kissing lemma")
        self.assertIsNone(am.verify(p["kissing-11"], centers[:, :10]), "dimension is part of the problem")

if __name__ == "__main__":
    unittest.main()