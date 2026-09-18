"""Actual isolated candidate outputs must pass the new promotion boundary."""
import os
from pathlib import Path
import tempfile
import unittest

from program_correctness import validator
from program_promotion import promote
from warm_evaluator import WarmEvaluator
from warm_sandbox import WarmDockerSandbox
from warm_study_design import digest


@unittest.skipUnless(os.environ.get("EVOLUTION_SANDBOX_IMAGE"),"Requires actual Docker image")
class WarmCorrectnessTests(unittest.TestCase):
    def test_scalar_spoof_falls_back_only_after_original_passes_all_audits(self):
        parent = os.environ.get("EVOLUTION_SANDBOX_EVIDENCE")
        if parent:
            Path(parent).mkdir(parents=True,exist_ok=True)
        root = Path(tempfile.mkdtemp(prefix="us03-correctness-",dir=parent))
        sandbox = WarmDockerSandbox(os.environ["EVOLUTION_SANDBOX_IMAGE"],root/"sandbox")
        good = "class Solver:\n def solve(self,p): return {'solution':[.5,.5]}\n"
        bad = "class Solver:\n def solve(self,p): return {'solution':.5}\n"
        results, bindings = [], []
        for offset in (0.,1.,2.):
            problem = {"y":[offset,offset]}
            check = validator("unit_simplex_projection",problem,None,contract="mathematical-v1")
            evaluator = WarmEvaluator(sandbox,"Solver",[problem],lambda outputs:len(outputs)==1 and check(outputs[0]),
                                      identity="us03-public-simplex-fixture",samples=1,phase="confirmation")
            results.append({"original":evaluator(good),"selected":evaluator(bad)})
            bindings.append(dict(input_sha256=evaluator.manifest["input_sha256"],evaluator_sha256=digest(evaluator.manifest)))
        decision = promote(good,bad,results[0],[r["selected"] for r in results[1:]],[r["original"] for r in results[1:]],
                           expected=dict(diagnostic=bindings[0],audits=bindings[1:]))
        self.assertTrue(decision["fallback"])
        self.assertTrue(all(r["selected"]["status"] == "invalid" and r["original"]["status"] == "valid" for r in results))
        self.assertEqual(6,len(sandbox.rows))
        self.assertFalse(any(r["unknown_work"] for r in sandbox.rows))


if __name__ == "__main__":
    unittest.main()
