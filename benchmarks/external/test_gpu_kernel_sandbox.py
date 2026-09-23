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
        box = WarmDockerSandbox(os.environ["EVOLUTION_GPU_SANDBOX_IMAGE"], Path(tempfile.mkdtemp()) / "e",
                                seconds=60, memory_mib=1024, gpus=True)
        for task in k.TASKS:
            problems = k.problems(task, k.DEV_SEEDS[:1], mode="verify") + k.problems(task, k.DEV_SEEDS[:1], mode="timed", repeats=3)
            evaluate = k.kernel_evaluator(box, task, "gpu", problems, identity=f"kernels-{task}")

            valid = evaluate(k.initial(task))
            self.assertEqual("valid", valid["status"], task)
            self.assertLess(valid["duration_seconds"], valid["host_seconds"], "kernel-only time excludes the harness")
            self.assertEqual("invalid", evaluate(WRONG[task][0])["status"], task)


if __name__ == "__main__":
    unittest.main()
