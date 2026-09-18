"""Public deterministic fixtures, not evidence of competitive performance."""
import math
import unittest

from program_controls import candidate_hash
from warm_confirmation import assess, confirm, policy
from warm_study_design import digest


def observations(selected):
    return {role:dict(status="valid", samples=samples,
                      sample_ids=[f"{role}-{i}" for i in range(len(samples))])
            for role,samples in (("original",[1.] * len(selected)),("selected",selected))}


class ConfirmationTests(unittest.TestCase):
    def test_consistent_gain_confirms_under_declared_family(self):
        result = assess(observations([.49,.50,.51,.50]*4),policy(24))
        self.assertTrue(result["confirmed"])
        self.assertLess(result["upper_log_ratio"],0)

    def test_lucky_search_does_not_override_fresh_regression(self):
        self.assertFalse(assess(observations([1.01,1.02,1.03,1.02]*4),policy(1))["confirmed"])

    def test_ties_and_exact_rounded_ratios_are_unresolved(self):
        for value in (1.,.5):
            result = assess(observations([value]*16),policy(1))
            self.assertFalse(result["confirmed"])
            self.assertEqual("unresolved-timing-resolution",result["reason"])

    def test_outlier_uncertainty_prevents_promotion(self):
        self.assertFalse(assess(observations([.8]*15+[10.]),policy(1))["confirmed"])

    def test_invalid_correctness_never_confirms(self):
        values = observations([.49,.50,.51,.50]*4)
        values["selected"]["status"] = "invalid"
        self.assertFalse(assess(values,policy(1))["confirmed"])

    def test_bad_measurements_and_duplicate_ids_are_rejected(self):
        for bad in (0,-1,True,float("nan"),float("inf"),None):
            values = observations([.49,.50,.51,.50]*4)
            values["selected"]["samples"][0] = bad
            with self.subTest(value=bad), self.assertRaises(ValueError):
                assess(values,policy(1))
        values = observations([.49,.50,.51,.50]*4)
        values["selected"]["sample_ids"][0] = values["original"]["sample_ids"][0]
        with self.assertRaises(ValueError):
            assess(values,policy(1))
        values = observations([.49,.50,.51,.50]*4)
        values["selected"]["samples"].pop()
        with self.assertRaises(ValueError):
            assess(values,policy(1))

    def test_policy_bounds_and_multiple_challenges(self):
        self.assertEqual(.05/72,policy(24)["alpha"])
        for tracks,pairs in ((0,16),(True,16),(1,2),(1,65),(1,True)):
            with self.assertRaises(ValueError):
                policy(tracks,pairs)
        for key,value in (("alpha",math.nan),("alpha",0),("minimum_relative_gain",1),("pairs",True)):
            frozen = dict(policy(1),**{key:value})
            with self.assertRaises(ValueError):
                assess(observations([.5]*16),frozen)

    def test_interleaving_is_fixed_fresh_and_fully_charged(self):
        class Evaluator:
            samples, phase, cases = 1,"confirmation",[1]
            manifest = dict(input_sha256="inputs")
            def __init__(self):
                self.calls = []
            def __call__(self,code,*,owner):
                self.calls.append((code,owner))
                index = len(self.calls)
                return dict(candidate_hash=candidate_hash(code),unknown_work=False,phase=self.phase,
                            sample_ids=[str(index)],input_sha256="inputs",evaluator_sha256=digest(self.manifest),
                            duration_seconds=(1 if code == "original" else .5)+index*.0001,status="valid",
                            work_units=1,budget_operation_ids=[index],resources=[{}],startup_samples=[.1])
        evaluator = Evaluator()
        values,evidence = confirm("original","selected",evaluator,policy(1),seed=3,owner="track")
        self.assertEqual(32,len(evaluator.calls))
        self.assertEqual(32,sum(v["work_units"] for v in values.values()))
        self.assertTrue(evidence["confirmed"])
        self.assertEqual([r for order in evidence["orders"] for r in order],[c[0] for c in evaluator.calls])
        other = Evaluator()
        self.assertEqual(evidence,confirm("original","selected",other,policy(1),seed=3,owner="track")[1])
        evaluator.samples = 3
        with self.assertRaises(ValueError):
            confirm("original","selected",evaluator,policy(1),seed=3,owner="track")


if __name__ == "__main__":
    unittest.main()
