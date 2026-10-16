"""V1-04a (#124): the AlphaEvolve headroom bundle, checked against the family it is measured on.

The 20,000-iteration run takes minutes, so this guards the cheap half: every published value in the
bundle has to equal the manifest the verifiers reproduce, every gap has to be re-derivable from the
two numbers it summarises, and the scope has to account for all 36 transcribed problems. A bundle
that drifted from the manifest would compare the floor against a target nobody published.

Three of the checks here exist because the first version of the harness got them wrong, and each one
was a way of reporting more than had been measured:

* **the partition** named 13 exclusions against 36 problems, so 18 tensor problems and both c4
  problems were missing from the inventory - and one of those two turned out to be in scope;
* **c3 and c3'** were repaired to non-negative, which is exactly the constraint their verifier drops,
  and all three autocorrelation problems then returned c1's value to the last bit;
* **c4-laguerre** is excluded because its verifier assumes an admissible construction, and a random
  vector of twelve positive reals beats the published figure. That exclusion is a claim about
  upstream, so it is measured here rather than asserted - the day it gains an admissibility check,
  this test fails and the problem comes back into scope.
"""
import json
import random
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
BUNDLE = ROOT / "benchmarks/evidence/alphaevolve-headroom/77756eb/headroom.json"
MANIFEST = ROOT / "benchmarks/evidence/alphaevolve-math/manifest.json"
sys.path.insert(0, str(HERE))

import alphaevolve_headroom as headroom
import alphaevolve_math as math_family


class AlphaEvolveHeadroomEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.report = json.loads(BUNDLE.read_text(encoding="utf-8"))
        self.manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.published = {row["id"]: row["published_value"] for row in self.manifest["rows"]}

    def test_every_published_value_is_the_manifests(self):
        for row in self.report["Rows"]:
            with self.subTest(problem=row["Problem"]):
                self.assertIn(row["Problem"], self.published)
                self.assertEqual(self.published[row["Problem"]], row["Published"])

    def test_every_gap_is_derived_from_the_numbers_it_summarises(self):
        for row in self.report["Rows"]:
            with self.subTest(problem=row["Problem"]):
                published, floor = row["Published"], row["FloorBest"]
                expected = ((floor - published) if row["Direction"] == "minimize"
                            else (published - floor)) / abs(published)
                self.assertAlmostEqual(expected, row["GapFraction"], places=12)
                # Positive always means the floor is behind, whichever way the problem points.
                self.assertEqual(row["FloorReachesPublished"], not row["GapFraction"] > 0)

    def test_the_scope_accounts_for_every_transcribed_problem(self):
        self.assertEqual(self.manifest["problems"], self.report["Transcribed"])
        self.assertEqual(len(self.report["Rows"]) + len(self.report["OutOfScope"]),
                         self.manifest["problems"],
                         "a problem in neither list is one dropped from the inventory")
        named = {row["Problem"] for row in self.report["Rows"]}
        named |= {row["Problem"] for row in self.report["OutOfScope"]}
        self.assertEqual({row["id"] for row in self.manifest["rows"]}, named)
        for row in self.report["OutOfScope"]:
            with self.subTest(problem=row["Problem"]):
                self.assertGreater(len(row["Reason"]), 40, "an exclusion with no reason is a silent drop")

    def test_the_partition_is_derived_and_refuses_to_be_partial(self):
        # The guard against the way this went wrong: a hand-kept list that stopped covering.
        self.assertEqual(sorted(row["Problem"] for row in self.report["OutOfScope"]),
                         sorted(row["Problem"] for row in headroom.out_of_scope(MANIFEST)))
        trimmed = {"problems": self.manifest["problems"], "rows": self.manifest["rows"][:4]}
        shortened = HERE / "__partial-manifest.json"
        shortened.write_text(json.dumps(trimmed), encoding="utf-8")
        try:
            with self.assertRaises(ValueError) as caught:
                headroom.out_of_scope(shortened)
            self.assertIn("silently dropped", str(caught.exception))
        finally:
            shortened.unlink()

    def test_the_signed_problems_are_not_repaired_back_onto_c1(self):
        # c3 and c3' drop c1's non-negativity. Repairing it back made all three one problem.
        # This candidate also separates c3 from c3-prime, which most do not: they differ only when
        # the most negative autocorrelation outweighs the largest positive one, and at the floor's
        # best candidate it does not - so the two share a value against two different targets.
        signed = [1.0, -1.0, 0.25, 0.1]
        for name in ("c3-autocorrelation", "c3prime-autocorrelation"):
            with self.subTest(problem=name):
                repaired = headroom.IN_SCOPE[name]["repair"](signed)
                self.assertTrue(any(value < 0 for value in repaired),
                                "the repair clipped the sign changes that define this problem")
        self.assertIsNone(math_family.c1(signed), "c1 must refuse a signed candidate")
        self.assertIsNotNone(math_family.c3(signed), "c3 must accept one")
        self.assertNotEqual(math_family.c3(signed), math_family.c3(signed, True),
                            "the chosen candidate separates c3 from c3-prime, so neither is the other")

    def test_every_repair_produces_something_its_own_verifier_accepts(self):
        # A floor that cannot enter the feasible set measures nothing, and c5 could not at first.
        generator = random.Random(4242)
        for name, spec in headroom.IN_SCOPE.items():
            with self.subTest(problem=name):
                length = spec["lengths"][0]
                candidate = spec["repair"]([generator.uniform(0.2, 1.0) for _ in range(length)])
                self.assertIsNotNone(candidate, "the repair refused every candidate")
                self.assertIsNotNone(spec["objective"](candidate),
                                     "the repair produced something the verifier will not score")

    def test_the_laguerre_exclusion_is_measured_and_still_true(self):
        reason = next(row["Reason"] for row in self.report["OutOfScope"]
                      if row["Problem"] == "c4-uncertainty-laguerre")
        self.assertIn("admissible", reason)
        # Three seeds rather than one: the bound is None for a candidate whose double roots do not
        # divide out exactly, which is a property of the vector and not of admissibility, so a
        # single seed could go None for a reason that has nothing to do with the exclusion. Each of
        # these scores today and each beats the published figure; the claim is that SOME random
        # twelve positive reals do, and `laguerre_bound` takes them anyway.
        scored = []
        for seed in (1, 8, 10):
            generator = random.Random(seed)
            value = math_family.laguerre_bound(sorted(generator.uniform(0.1, 20.0) for _ in range(12)))
            if value is not None:
                scored.append(value)
        self.assertTrue(scored, "no probe behind the exclusion scores at all any more")
        self.assertLess(min(scored), self.published["c4-uncertainty-laguerre"],
                        "laguerre_bound now refuses an inadmissible candidate - the exclusion is "
                        "stale and the problem belongs back in scope")

    def test_the_floor_is_monotone_in_its_budget(self):
        # The published gap is only a lower bound on the room above the floor if running the floor
        # longer cannot do worse. The schedule is absolute so a short run is a prefix of a long one.
        spec = headroom.IN_SCOPE["c5-erdos-overlap"]
        values = [headroom.descend(spec["objective"], spec["repair"], spec["direction"], 12, 3, budget)
                  for budget in (250, 500, 1000, 2000)]
        self.assertTrue(all(value is not None for value in values))
        for shorter, longer in zip(values, values[1:]):
            self.assertLessEqual(longer, shorter, "a longer run of the floor returned a worse candidate")

    def test_a_capped_problem_says_that_it_was_capped(self):
        for row in self.report["Rows"]:
            with self.subTest(problem=row["Problem"]):
                self.assertEqual(row["Capped"], row["Iterations"] < self.report["RequestedIterations"])
                self.assertGreater(row["Iterations"], 0)
        capped = [row["Problem"] for row in self.report["Rows"] if row["Capped"]]
        self.assertEqual(["c4-uncertainty-hermite"], capped,
                         "only the exact-arithmetic verifier is capped; a new cap needs stating")

    def test_the_bundle_names_the_notebook_its_targets_came_from(self):
        self.assertEqual(math_family.SOURCE_COMMIT, self.report["SourceCommit"])
        self.assertEqual(math_family.SOURCE_REPOSITORY, self.report["Source"])
        self.assertEqual(self.manifest["source"].split("/blob/")[1].split("/")[0],
                         self.report["SourceCommit"])


if __name__ == "__main__":
    unittest.main()