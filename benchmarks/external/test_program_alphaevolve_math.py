"""V1-04a contracts. Needs EVOLUTION_ALPHAEVOLVE_RESULTS: google-deepmind/alphaevolve_results at SOURCE_COMMIT."""
import os
from pathlib import Path
import unittest

import numpy as np

import alphaevolve_math as am

NOTEBOOK_SHA256 = "2cce2543e48c89aa3e91614272a698a0147dd2548ea11cf92f1292b7435d38ff"


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
        self.assertEqual(18, len(self.problems))  # 16 + the two hexagon packings
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

class FamilyTests(unittest.TestCase):
    def setUp(self):
        root = os.environ.get("EVOLUTION_ALPHAEVOLVE_RESULTS")
        if not root:
            raise RuntimeError("Set EVOLUTION_ALPHAEVOLVE_RESULTS to the pinned alphaevolve_results checkout")
        self.problems = am.family(Path(root) / "mathematical_results.ipynb", NOTEBOOK_SHA256)

    def test_every_published_construction_verifies(self):
        self.assertEqual(36, len(self.problems))
        self.assertEqual([], [r["id"] for r in am.self_test(self.problems) if not r["matches"]])
        rows = am.manifest(self.problems)
        self.assertEqual(36, len(rows))
        self.assertEqual({"independent"}, {r["verifier"] for r in rows})
        self.assertTrue(all(r["citation"].startswith("https://github.com/google-deepmind/alphaevolve_results/blob/")
                            and r["direction"] in ("minimize", "maximize") for r in rows))
        self.assertTrue(all("construction" not in r for r in rows), "the manifest never carries a construction")

    def test_b4_reproduces_the_prior_state_of_the_art_too(self):
        # Goncalves et al. (2017) coefficients: the notebook says they round to 0.3523.
        self.assertEqual(0.3523, round(am.hermite_bound([-113 / 100, 1 / 25, 1 / 3240]), 4))
        self.assertIsNone(am.laguerre_bound([1.0, 1.0] + list(range(3, 13))), "roots must be distinct")

    def test_contamination_screens(self):
        by_id = {p["id"]: p for p in self.problems}
        heights = by_id["c1-autocorrelation"]["construction"]
        self.assertEqual(["c1-autocorrelation"],
                         am.prompt_contamination(self.problems, "heights: " + " ".join(map(str, heights[100:120]))))
        self.assertEqual([], am.prompt_contamination(self.problems, "Minimise max(f*f)/(int f)^2 with 600 steps."))
        circles = np.array(by_id["circles-square-26"]["construction"])
        self.assertTrue(am.output_contamination(by_id["circles-square-26"], circles[::-1]))
        self.assertFalse(am.output_contamination(by_id["circles-square-26"], circles + 1e-3))
        a, b, c = by_id["tensor-333-Z"]["construction"]
        order = np.arange(a.shape[1])[::-1]
        self.assertTrue(am.output_contamination(by_id["tensor-333-Z"], (a[:, order], b[:, order], c[:, order])))

if __name__ == "__main__":
    unittest.main()