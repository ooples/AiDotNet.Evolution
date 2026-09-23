"""V1-04c kernel family: GPU (CuPy) and CPU (NumPy) row kernels with an FP64 host oracle.

A candidate supplies only `kernel(x, *params)`. TRUSTED code appended after it (so it wins
any name clash) builds every input inside the sandbox from a seed with a closed form the
host reproduces exactly, runs the kernel, and returns:
- VERIFY problems: the full output of a small shape, compared elementwise with the host's
  FP64 reference;
- TIMED problems: `repeats` fresh-seed runs of a large shape, each reduced to seeded random
  projections the host recomputes from its own reference. Only digests cross the wire, so
  the sandbox's request-to-response time measures the kernels, not serialization.
A fast wrong kernel fails either check, and then has no speedup (US-03).

Residual risk, declared: a candidate that monkeypatches the array library could forge the
digests. LLM candidates are screened by the full-output check on the verify shape, which
the same forgery would also have to pass.
"""
from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path
import secrets
import statistics

import numpy as np

FAMILY = "kernels"
DEV_SEEDS = tuple(range(3000, 3004))
VALIDATION_SEEDS = tuple(range(4000, 4004))
TEST_SIZE = 4
PROJECTIONS = 4
RTOL, ATOL = 2e-3, 2e-4  # float32 kernels against an FP64 reference

# task -> (parameter builders, fp64 reference, initial program body)
TASKS = {
    "softmax": dict(params=(), description="Row-wise softmax of x (rows x cols, float32).",
                    initial="def kernel(x):\n    m = xp.max(x, axis=1, keepdims=True)\n    e = xp.exp(x - m)\n"
                            "    return e / xp.sum(e, axis=1, keepdims=True)\n"),
    "layernorm": dict(params=("gamma", "beta"), description="Row-wise layer norm, eps=1e-5, with gamma and beta (cols).",
                      initial="def kernel(x, gamma, beta):\n    mu = xp.mean(x, axis=1, keepdims=True)\n"
                              "    var = xp.mean((x - mu) ** 2, axis=1, keepdims=True)\n"
                              "    return (x - mu) / xp.sqrt(var + 1e-5) * gamma + beta\n"),
    "rmsnorm": dict(params=("gamma",), description="Row-wise RMS norm, eps=1e-6, times gamma (cols).",
                    initial="def kernel(x, gamma):\n    return x / xp.sqrt(xp.mean(x * x, axis=1, keepdims=True) + 1e-6) * gamma\n"),
    "bias_gelu": dict(params=("bias",), description="Exact GELU (erf form) of x + bias (bias per column).",
                      initial="def kernel(x, bias):\n    z = x + bias\n"
                              "    return 0.5 * z * (1 + erf(z / math.sqrt(2.0)))\n"),
}
SHAPES = {"verify": (64, 96), "timed": (4096, 1024)}


def inputs(xp, task, seed, rows, cols):
    """Closed-form inputs; identical on host (numpy) and sandbox (cupy/numpy) up to float32 rounding."""
    i = xp.arange(rows, dtype=xp.float64)[:, None]
    j = xp.arange(cols, dtype=xp.float64)[None, :]
    x = (3.0 * xp.sin(0.37 * i + 0.11 * j + 0.001 * seed) + 0.5 * xp.cos(0.013 * i * j + seed % 7)).astype(xp.float32)
    col = xp.arange(cols, dtype=xp.float64)
    params = {"gamma": (1.0 + 0.1 * xp.sin(0.05 * col + seed)).astype(xp.float32),
              "beta": (0.1 * xp.cos(0.03 * col + seed)).astype(xp.float32),
              "bias": (0.2 * xp.sin(0.07 * col - seed)).astype(xp.float32)}
    return x, [params[name] for name in TASKS[task]["params"]]


def weights(xp, seed, size):
    k = xp.arange(PROJECTIONS, dtype=xp.float64)[:, None]
    n = xp.arange(size, dtype=xp.float64)[None, :]
    return xp.cos(0.7071 * (k + 1) * n + 0.5 * seed + k)


def reference(task, x, params):
    """FP64 host oracle, independent of any candidate or of the initial program's code."""
    from scipy.special import erf
    x = x.astype(np.float64)
    params = [p.astype(np.float64) for p in params]
    if task == "softmax":
        e = np.exp(x - x.max(1, keepdims=True))
        return e / e.sum(1, keepdims=True)
    if task == "layernorm":
        mu = x.mean(1, keepdims=True)
        return (x - mu) / np.sqrt(((x - mu) ** 2).mean(1, keepdims=True) + 1e-5) * params[0] + params[1]
    if task == "rmsnorm":
        return x / np.sqrt((x * x).mean(1, keepdims=True) + 1e-6) * params[0]
    if task == "bias_gelu":
        z = x + params[0]
        return 0.5 * z * (1 + erf(z / math.sqrt(2.0)))
    raise ValueError(task)


def trusted_suffix(task, backend):
    xp = "cupy" if backend == "gpu" else "numpy"
    return f'''

# ---- trusted harness (appended after the candidate; defines Solver last) ----
import math as _math
import numpy as _np
import {xp} as _xp
_TASK, _PROJECTIONS = {task!r}, {PROJECTIONS}
{_source(inputs).replace("def inputs(", "def _inputs(")}
{_source(weights).replace("def weights(", "def _weights(")}
TASKS = {json.dumps({t: dict(params=list(v["params"])) for t, v in TASKS.items()})}

class Solver:
    def solve(self, problem):
        rows, cols = problem["shape"]
        outputs = []
        for offset in range(problem["repeats"]):
            seed = problem["seed"] + offset
            x, params = _inputs(_xp, _TASK, seed, rows, cols)
            y = _xp.asarray(kernel(x, *params))
            if y.shape != (rows, cols):
                return {{"error": "shape"}}
            if problem["mode"] == "verify":
                outputs.append(_xp.asnumpy(y).tolist() if _xp.__name__ == "cupy" else y.tolist())
            else:
                digest = _weights(_xp, seed, rows * cols) @ y.astype(_xp.float64).reshape(-1)
                outputs.append([float(v) for v in (digest.get() if _xp.__name__ == "cupy" else digest)])
        if _xp.__name__ == "cupy":
            _xp.cuda.Device().synchronize()
        return {{"outputs": outputs}}
'''


def _source(function):
    import inspect
    return inspect.getsource(function).replace("PROJECTIONS", "_PROJECTIONS").replace("TASKS[", "TASKS[")


def program(task, backend, candidate):
    """What the sandbox runs: the candidate's `kernel`, then the trusted Solver."""
    header = "import math\nimport numpy as np\n" + ("import cupy as xp\nfrom cupyx.scipy.special import erf\n"
                                                    if backend == "gpu" else "import numpy as xp\nfrom scipy.special import erf\n")
    return header + candidate + trusted_suffix(task, backend)


def initial(task):
    return TASKS[task]["initial"]


def problems(task, seeds, *, mode, repeats=1):
    shape = SHAPES["verify" if mode == "verify" else "timed"]
    return [dict(mode=mode, seed=int(seed), shape=list(shape), repeats=repeats) for seed in seeds]


def validate(task, problem_list, outputs):
    """True only if every output matches the FP64 reference (full values, or digests)."""
    if not isinstance(outputs, list) or len(outputs) != len(problem_list):
        return False
    for problem, output in zip(problem_list, outputs):
        if not isinstance(output, dict) or "outputs" not in output or len(output["outputs"]) != problem["repeats"]:
            return False
        rows, cols = problem["shape"]
        for offset, value in enumerate(output["outputs"]):
            seed = problem["seed"] + offset
            x, params = inputs(np, task, seed, rows, cols)
            expected = reference(task, x, params)
            if problem["mode"] == "verify":
                got = np.asarray(value, dtype=np.float64)
                if got.shape != expected.shape or not np.all(np.isfinite(got)) or \
                        not np.allclose(got, expected, rtol=RTOL, atol=ATOL):
                    return False
            else:
                want = weights(np, seed, rows * cols) @ expected.reshape(-1)
                got = np.asarray(value, dtype=np.float64)
                scale = np.sqrt(rows * cols) * (np.abs(expected).max() + 1.0)
                if got.shape != want.shape or not np.all(np.isfinite(got)) or np.any(np.abs(got - want) > 1e-4 * scale):
                    return False
    return True


def seal(directory):
    reserved = set(DEV_SEEDS) | set(VALIDATION_SEEDS)
    test = {}
    for task in sorted(TASKS):
        seeds = []
        while len(seeds) < TEST_SIZE:
            candidate = secrets.randbits(31)
            if candidate not in reserved and candidate not in seeds:
                seeds.append(candidate)
        test[task] = seeds
    body = json.dumps(dict(Family=FAMILY, Schema="evolution-sealed-test-partition-v1", TestSeeds=test,
                           DevSeeds=list(DEV_SEEDS), ValidationSeeds=list(VALIDATION_SEEDS)),
                      sort_keys=True, separators=(",", ":")).encode()
    path = Path(directory) / f"{FAMILY}-test-partition.json"
    Path(directory).mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(body)
    return dict(path=str(path), sha256=hashlib.sha256(body).hexdigest())


def open_seal(path, expected_sha256):
    body = Path(path).read_bytes()
    if hashlib.sha256(body).hexdigest() != expected_sha256:
        raise ValueError("Sealed test partition does not match its registered hash")
    return json.loads(body)


def speedup(reference_timer, candidate_timer, samples=5):
    ratios = []
    for _ in range(samples):
        base, cand = reference_timer(), candidate_timer()
        if base["status"] != "valid":
            raise RuntimeError("The initial program failed its own oracle")
        if cand["status"] != "valid":
            return dict(speedup=None, ratios=ratios)
        ratios.append(base["duration_seconds"] / cand["duration_seconds"])
    return dict(speedup=statistics.median(ratios), ratios=ratios)