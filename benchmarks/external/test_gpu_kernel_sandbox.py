"""V1-04c GPU sandbox gate: needs EVOLUTION_GPU_SANDBOX_IMAGE (Dockerfile.gpu) and an NVIDIA GPU.

Kept out of the required test_program* gate, whose runners have no GPU; the CPU oracle
contracts stay there. Run it wherever the hardware exists.
"""
import os
from pathlib import Path
import tempfile
import unittest

import kernel_family as k
from test_program_kernel_family import WRONG

@unittest.skipUnless(os.environ.get("EVOLUTION_GPU_SANDBOX_IMAGE"), "Needs the GPU sandbox image and an NVIDIA GPU")
class GpuSandboxTests(unittest.TestCase):
    def test_gpu_kernels_run_isolated_and_are_judged_by_the_host_oracle(self):
        from warm_sandbox import WarmDockerSandbox
        from warm_evaluator import WarmEvaluator
        box = WarmDockerSandbox(os.environ["EVOLUTION_GPU_SANDBOX_IMAGE"], Path(tempfile.mkdtemp()) / "e",
                                seconds=60, memory_mib=1024, gpus=True)
        for task in k.TASKS:
            problems = k.problems(task, k.DEV_SEEDS[:1], mode="verify") + k.problems(task, k.DEV_SEEDS[:1], mode="timed", repeats=3)
            evaluate = lambda source: WarmEvaluator(box, "Solver", problems, lambda out: k.validate(task, problems, out),
                                                    identity=f"kernels-{task}", samples=1)(k.program(task, "gpu", source))
            self.assertEqual("valid", evaluate(k.initial(task))["status"], task)
            self.assertEqual("invalid", evaluate(WRONG[task][0])["status"], task)


if __name__ == "__main__":
    unittest.main()
