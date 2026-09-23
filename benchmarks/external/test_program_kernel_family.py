"""V1-04c contracts. The GPU sandbox test needs EVOLUTION_GPU_SANDBOX_IMAGE (Dockerfile.gpu)."""
import os
from pathlib import Path
import tempfile
import unittest

import kernel_family as k

WRONG = {
    "softmax": ["def kernel(x):\n    return xp.exp(x) / xp.sum(xp.exp(x))\n",           # global, not per row
                "def kernel(x):\n    return x * 0 + 1.0 / x.shape[1]\n"],                   # uniform guess
    "layernorm": ["def kernel(x, gamma, beta):\n    return (x - xp.mean(x, axis=1, keepdims=True)) * gamma + beta\n",
                  "def kernel(x, gamma, beta):\n    return x\n"],
    "rmsnorm": ["def kernel(x, gamma):\n    return x * gamma\n",
                "def kernel(x, gamma):\n    return x / xp.sqrt(xp.mean(x * x) + 1e-6) * gamma\n"],  # whole-matrix mean
    "bias_gelu": ["def kernel(x, bias):\n    z = x + bias\n    return 0.5 * z * (1 + xp.tanh(0.7978845608 * (z + 0.044715 * z ** 3)))\n",
                  "def kernel(x, bias):\n    return xp.maximum(x + bias, 0)\n"],
}


def run_host(task, source, problems):
    namespace = {}
    exec(k.program(task, "cpu", source), namespace)
    return [namespace["Solver"]().solve(p) for p in problems]


class OracleTests(unittest.TestCase):
    def problems(self, task):
        return k.problems(task, k.DEV_SEEDS[:2], mode="verify") + k.problems(task, k.DEV_SEEDS[:1], mode="timed", repeats=2)

    def test_initial_programs_pass_and_wrong_kernels_fail(self):
        for task in k.TASKS:
            problems = self.problems(task)
            self.assertTrue(k.validate(task, problems, run_host(task, k.initial(task), problems)), task)
            for source in WRONG[task]:
                with self.subTest(task=task, source=source):
                    self.assertFalse(k.validate(task, problems, run_host(task, source, problems)))

    def test_timed_digests_catch_a_wrong_kernel_that_the_verify_shape_would_miss(self):
        # Correct on small shapes, wrong on the timed shape: only the digest can catch it.
        sneaky = ("def kernel(x):\n    if x.shape[0] > 1000:\n        return x * 0 + 1.0 / x.shape[1]\n"
                  "    m = xp.max(x, axis=1, keepdims=True)\n    e = xp.exp(x - m)\n    return e / xp.sum(e, axis=1, keepdims=True)\n")
        verify = k.problems("softmax", k.DEV_SEEDS[:1], mode="verify")
        timed = k.problems("softmax", k.DEV_SEEDS[:1], mode="timed")
        self.assertTrue(k.validate("softmax", verify, run_host("softmax", sneaky, verify)))
        self.assertFalse(k.validate("softmax", timed, run_host("softmax", sneaky, timed)))

    def test_malformed_outputs_are_rejected(self):
        problems = k.problems("rmsnorm", k.DEV_SEEDS[:1], mode="verify")
        for outputs in ([], [{}], [{"outputs": []}], [{"error": "shape"}], None):
            self.assertFalse(k.validate("rmsnorm", problems, outputs))

    def test_seal(self):
        with tempfile.TemporaryDirectory() as directory:
            record = k.seal(directory)
            sealed = k.open_seal(record["path"], record["sha256"])
            self.assertEqual(sorted(k.TASKS), sorted(sealed["TestSeeds"]))
            with self.assertRaises(ValueError):
                k.open_seal(record["path"], "0" * 64)


if __name__ == "__main__":
    unittest.main()