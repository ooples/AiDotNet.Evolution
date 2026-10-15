"""V1-40 (#121): required N from the pilot's measured variance, not from an anticipated effect.

The question `design.plan_design` answers is a different one, asked before anything runs. These
tests pin the one a pilot answers, and the three readings that matter: a noisy endpoint needs more
seeds than a quiet one, an endpoint with no spread is reported as degenerate rather than as needing
one seed, and the two estimators are reported side by side so a disagreement is visible.
"""
import math
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

import campaign_registration as registration  # noqa: E402
import power_analysis as power  # noqa: E402

TASK = "family"


def spec(seeds=6):
    return {
        "Campaign": "power-fixture",
        "Budget": {"ModelTokens": 1000, "ModelCalls": 10, "WallSeconds": 60},
        "Models": ["sonnet"],
        "SeedCount": seeds,
        "BootstrapSamples": 2000,
        "BootstrapSeed": 7,
        "FamilywiseAlpha": 0.05,
        "Families": [{"Family": TASK, "Configs": ["recommended"],
                      "Endpoint": {"Metric": "nmse", "Direction": "minimize", "FailureValue": 1e6}}],
        "TestPartitionHashes": {TASK: "0" * 64},
        "Published": None,
    }


def frozen(seeds=6):
    declared = spec(seeds)
    registration.validate(declared)
    return {"Schema": registration.SCHEMA, "Spec": declared, "Seeds": list(range(1, seeds + 1)),
            "Arms": [registration.OURS, "openevolve-recommended"], "RegistrationSha256": "r" * 64}


def runs(values):
    """One paired cell per seed: ours takes the value, theirs is fixed, so the difference is known."""
    rows = []
    for seed, ours in enumerate(values, start=1):
        rows.append(dict(Family=TASK, Arm=registration.OURS, Seed=seed, Status="completed", Value=ours,
                         ModelCalls=1, ModelTokens=10))
        rows.append(dict(Family=TASK, Arm="openevolve-recommended", Seed=seed, Status="completed", Value=1.0,
                         ModelCalls=1, ModelTokens=10))
    return rows


class PowerAnalysisTests(unittest.TestCase):
    def analyse(self, values, half_width, seeds=6):
        record = frozen(seeds)
        rows = runs(values)
        report = registration.analyze(record, rows)
        return power.analyse(record, rows, report, half_width=half_width)

    def test_a_noisier_endpoint_needs_more_seeds_than_a_quiet_one(self):
        quiet = self.analyse([0.50, 0.52, 0.48, 0.51, 0.49, 0.50], 0.02)
        noisy = self.analyse([0.10, 0.90, 0.20, 0.80, 0.30, 0.70], 0.02)
        self.assertLess(quiet["RequiredN"], noisy["RequiredN"],
                        "variance is the whole input; a noisy endpoint cannot need fewer seeds")
        self.assertLess(quiet["Endpoints"][0]["StandardDeviation"], noisy["Endpoints"][0]["StandardDeviation"])

    def test_halving_the_target_width_roughly_quadruples_the_seeds(self):
        # The 1/sqrt(n) scaling is the only assumption the scaled estimator makes, so it is worth
        # pinning: a reader who sees required N double for a halved width should know it is wrong.
        wide = self.analyse([0.10, 0.90, 0.20, 0.80, 0.30, 0.70], 0.10)["Endpoints"][0]["ScaledRequiredN"]
        narrow = self.analyse([0.10, 0.90, 0.20, 0.80, 0.30, 0.70], 0.05)["Endpoints"][0]["ScaledRequiredN"]
        self.assertAlmostEqual(4.0, narrow / wide, delta=0.35, msg="required N scales with 1/width^2")

    def test_an_endpoint_with_no_spread_is_degenerate_rather_than_cheap(self):
        # Every paired difference identical: the arithmetic would say one seed suffices, which is
        # how a campaign ends up sized from an endpoint that was repeated rather than measured.
        analysis = self.analyse([0.5] * 6, 0.01)
        row = analysis["Endpoints"][0]
        self.assertTrue(row["Degenerate"])
        self.assertEqual(power.MINIMUM_SEEDS, row["RequiredN"])
        self.assertEqual(0.0, row["StandardDeviation"])
        self.assertIn("DEGENERATE", "\n".join(power.lines(analysis)))

    def test_both_estimators_are_reported_and_the_larger_one_is_taken(self):
        row = self.analyse([0.10, 0.90, 0.20, 0.80, 0.30, 0.70], 0.02)["Endpoints"][0]
        self.assertEqual(max(row["ScaledRequiredN"], row["NormalRequiredN"]), row["RequiredN"],
                         "taking the smaller one is choosing the estimator that flattered the pilot")
        self.assertGreaterEqual(row["Agreement"], 1.0)
        self.assertIsNotNone(row["Agreement"])

    def test_a_pilot_already_inside_the_target_says_so(self):
        row = self.analyse([0.50, 0.50, 0.50, 0.51, 0.49, 0.50], 1.0)["Endpoints"][0]
        self.assertTrue(row["PilotAlreadyMeetsTarget"])
        self.assertLessEqual(row["ObservedHalfWidth"], row["TargetHalfWidth"])

    def test_the_campaign_is_sized_by_its_widest_endpoint(self):
        analysis = self.analyse([0.10, 0.90, 0.20, 0.80, 0.30, 0.70], 0.02)
        self.assertEqual(max(row["RequiredN"] for row in analysis["Endpoints"]), analysis["RequiredN"])
        self.assertIn("campaign N =", "\n".join(power.lines(analysis)))

    def test_an_impossible_target_is_refused_rather_than_truncated(self):
        # The cap exists so a campaign that cannot be run is not silently reported as runnable.
        with self.assertRaises(ValueError):
            self.analyse([0.10, 0.90, 0.20, 0.80, 0.30, 0.70], 1e-6)

    def test_a_confidence_other_than_the_reports_own_level_is_refused(self):
        # The scaled estimator can only scale the interval `analyze` produced, which is 95%. Taking
        # a 90% z for the normal estimator while scaling a 95% interval would make RequiredN the
        # larger of two numbers measured at different levels.
        record = frozen()
        rows_ = runs([0.1, 0.9, 0.2, 0.8, 0.3, 0.7])
        report = registration.analyze(record, rows_)
        with self.assertRaisesRegex(ValueError, "0.95"):
            power.analyse(record, rows_, report, half_width=0.05, confidence=0.9)

    def test_the_variance_is_reported_not_only_its_square_root(self):
        row = self.analyse([0.10, 0.90, 0.20, 0.80, 0.30, 0.70], 0.05)["Endpoints"][0]
        self.assertAlmostEqual(row["StandardDeviation"] ** 2, row["Variance"], places=10)
        self.assertIn("var=", chr(10).join(
            power.lines({"Endpoints": [row], "RequiredN": row["RequiredN"]})))

    def test_a_non_positive_target_width_is_refused(self):
        for width in (0.0, -1.0, math.inf):
            with self.subTest(width=width), self.assertRaises(ValueError):
                self.analyse([0.1, 0.9, 0.2, 0.8, 0.3, 0.7], width)


if __name__ == "__main__":
    unittest.main()
