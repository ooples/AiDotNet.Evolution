"""V1-04a AlphaEvolve math family: every published construction, with independent verifiers.

Source of truth: google-deepmind/alphaevolve_results, `mathematical_results.ipynb`, pinned
by commit and by file SHA-256. Only its DATA cells are executed (numpy literals, in a
namespace holding nothing but numpy); its verification code is never used, so every
checker here is independent of both DeepMind's notebook and any search evaluator.

Each problem records: id, family section, direction, published value, source citation,
proven-optimal flag, and a verifier that recomputes the objective and every constraint
from a candidate construction. A verifier is accepted only if it reproduces the published
value from the published construction (the self-test below).
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re

import numpy as np

SOURCE_REPOSITORY = "google-deepmind/alphaevolve_results"
SOURCE_COMMIT = "4226acbf237ff9ad10ba7673a2af127a2d8a5971"
CITATION = f"https://github.com/{SOURCE_REPOSITORY}/blob/{SOURCE_COMMIT}/mathematical_results.ipynb"


SAFE_MODULES = ("numpy", "math", "itertools", "fractions")
SAFE_BUILTINS = {name: __builtins__[name] if isinstance(__builtins__, dict) else getattr(__builtins__, name)
                 for name in ("range", "len", "list", "tuple", "dict", "set", "float", "int", "complex", "abs",
                              "sum", "min", "max", "zip", "enumerate", "round", "sorted", "map", "reversed", "print")}


def _safe_import(name, *args, **kwargs):
    if name.split(".")[0] not in SAFE_MODULES:
        raise ImportError(f"Data cells may import only {SAFE_MODULES}, not {name}")
    return __import__(name, *args, **kwargs)


def data_cells(notebook_path, expected_sha256=None):
    """(section heading, namespace) for every '#@title Data' cell, executed with numpy only."""
    raw = Path(notebook_path).read_bytes()
    digest = hashlib.sha256(raw).hexdigest()
    if expected_sha256 is not None and digest != expected_sha256:
        raise ValueError("Published notebook does not match its pinned hash")
    cells, heading, out = json.loads(raw)["cells"], None, []
    for cell in cells:
        source = "".join(cell["source"])
        if cell["cell_type"] == "markdown" and source.lstrip().startswith("#"):
            heading = source.strip().splitlines()[0].lstrip("#").strip()
        elif cell["cell_type"] == "code" and source.lstrip().startswith("#@title Data"):
            namespace = {"np": np, "__builtins__": dict(SAFE_BUILTINS, __import__=_safe_import)}
            try:
                exec(compile(source, f"<{heading}>", "exec"), namespace)  # pinned data, safe names only
            except Exception as error:
                out.append((heading, {"__unparsed__": f"{type(error).__name__}: {str(error)[:200]}"}))
                continue
            out.append((heading, {k: v for k, v in namespace.items()
                                  if k not in ("np", "__builtins__") and not k.startswith("_") and
                                  not callable(v) and type(v).__name__ != "module"}))
    return out, digest


# ---------------------------------------------------------------- tensor decompositions

def matmul_tensor(n, m, p):
    """<n,m,p>: the tensor of C = A B with A n x m, B m x p, as T[a_ij, b_jk, c_ki] = 1."""
    tensor = np.zeros((n * m, m * p, p * n), dtype=np.int64)
    for i in range(n):
        for j in range(m):
            for k in range(p):
                tensor[i * m + j, j * p + k, k * n + i] = 1
    return tensor


RINGS = {"Z": 1, "0.5*Z": 2, "0.5*C": 2}


def verify_tensor(decomposition, n, m, p, ring):
    """Rank of a valid decomposition of <n,m,p> over `ring`, or None. Exact: coefficients are
    scaled to integers (Gaussian integers for C) before the tensor is rebuilt."""
    if ring not in RINGS or not isinstance(decomposition, (tuple, list)) or len(decomposition) != 3:
        return None
    factors = [np.asarray(f) for f in decomposition]
    rank = factors[0].shape[1] if factors[0].ndim == 2 else -1
    shapes = [(n * m, rank), (m * p, rank), (p * n, rank)]
    if rank < 1 or any(f.shape != s for f, s in zip(factors, shapes)):
        return None
    scale = RINGS[ring]
    scaled = []
    for factor in factors:
        values = factor.astype(np.complex128) * scale
        rounded = np.round(values.real) + 1j * np.round(values.imag)
        if not np.all(np.abs(values - rounded) == 0):
            return None  # a coefficient outside the declared ring
        if ring != "0.5*C" and np.any(rounded.imag != 0):
            return None
        scaled.append(rounded)
    rebuilt = np.einsum("ir,jr,kr->ijk", *scaled)
    target = matmul_tensor(n, m, p) * scale ** 3
    return rank if np.array_equal(rebuilt, target) else None


TENSOR_HEADING = re.compile(r"Rank-(\d+) decomposition of <(\d),(\d),(\d)> over (0\.5\*Z|0\.5\*C|Z)")


def tensor_problems(cells):
    problems = []
    for heading, namespace in cells:
        match = TENSOR_HEADING.search(heading or "")
        if not match:
            continue
        rank, n, m, p, ring = int(match.group(1)), *map(int, match.groups()[1:4]), match.group(5)
        (name, decomposition), = namespace.items()
        problems.append(dict(id=f"tensor-{n}{m}{p}-{ring}", section=heading, kind="tensor", direction="minimize",
                             objective="rank", published_value=rank, proven_optimal=False,
                             citation=CITATION, parameters=dict(n=n, m=m, p=p, ring=ring),
                             construction_variable=name, construction=decomposition))
    return problems


def verify(problem, construction):
    """Objective value of a candidate for `problem`, or None when any constraint fails."""
    if problem["kind"] == "tensor":
        q = problem["parameters"]
        return verify_tensor(construction, q["n"], q["m"], q["p"], q["ring"])
    raise ValueError(f"No verifier for {problem['kind']}")


def self_test(problems):
    """Every verifier must reproduce the published value from the published construction."""
    return [dict(id=pr["id"], published=pr["published_value"], verified=verify(pr, pr["construction"])) for pr in problems]