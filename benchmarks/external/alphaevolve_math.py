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
        if cell["cell_type"] == "markdown":
            # A heading may follow other text in a cell; the last heading line names the section.
            for line in source.splitlines():
                if line.lstrip().startswith("#"):
                    heading = line.lstrip("# ").strip()
        elif cell["cell_type"] == "code" and re.match(r"#\s*@title [^\n]*Data", source.lstrip()):
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


# ------------------------------------------------ analysis and geometry (sections B.1-B.13)
TOL = 1e-9  # declared geometric slack for containment/disjointness; far below every published margin


def _heights(value, nonnegative=True, upper=None):
    h = np.asarray(value, dtype=np.float64)
    if h.ndim != 1 or h.size < 2 or not np.all(np.isfinite(h)) or (nonnegative and np.any(h < 0)):
        return None
    if upper is not None and np.any(h > upper):
        return None
    return h


def c1(value):  # max (f*f) / (int f)^2 over a step function on [-1/4, 1/4]
    h = _heights(value)
    return None if h is None or h.sum() <= 0 else 2 * h.size * np.convolve(h, h).max() / h.sum() ** 2


def c2(value):  # ||f*f||_2^2 / (||f*f||_1 ||f*f||_inf), f*f piecewise linear through its knots
    h = _heights(value)
    if h is None or h.sum() <= 0:
        return None
    g = np.convolve(h, h)
    y = np.concatenate(([0.0], g, [0.0]))
    width = 1.0 / (g.size + 1)
    l2 = (width / 3) * np.sum(y[:-1] ** 2 + y[:-1] * y[1:] + y[1:] ** 2)
    return l2 / ((np.abs(g).sum() / (g.size + 1)) * np.abs(g).max())


def c3(value, absolute_inside=False):  # f may change sign
    h = _heights(value, nonnegative=False)
    if h is None or h.sum() == 0:
        return None
    g = np.convolve(h, h)
    peak = np.abs(g).max() if absolute_inside else abs(g.max())
    return 2 * h.size * peak / h.sum() ** 2


def c5(value):  # Erdos minimum overlap: 0 <= f <= 1 with sum exactly n/2
    h = _heights(value, upper=1.0)
    if h is None or abs(h.sum() - h.size / 2) > 1e-9 * h.size:
        return None
    return 2 * np.correlate(h, 1 - h, mode="full").max() / h.size


def c6(value):  # 1 + log(|U-U|/|U+U|) / log(2 max U + 1), U of integers with min 0
    u = np.asarray(value)
    if u.ndim != 1 or u.size < 2 or u.dtype.kind not in "iu" or u.min() != 0 or np.unique(u).size != u.size:
        return None
    top = int(u.max())
    if 2 * top + 1 <= 1 << 26:
        indicator = np.zeros(top + 1)
        indicator[u] = 1
        size = 1 << int(np.ceil(np.log2(2 * top + 1)))
        spectrum = np.fft.rfft(indicator, size)
        sums = np.fft.irfft(spectrum * spectrum, size)[:2 * top + 1]
        diffs = np.fft.irfft(spectrum * np.conj(spectrum), size)
        plus = int(np.count_nonzero(np.round(sums) > 0))
        minus = int(np.count_nonzero(np.round(diffs) > 0))  # cyclic: covers -top..top once each
    else:
        # Too wide for an FFT in memory: mark supports directly, one bitmap at a time.
        # Symmetry halves the work: a+b = b+a, and |U-U| is symmetric about 0.
        u = np.sort(u)
        seen = np.zeros(2 * top + 1, dtype=bool)
        for index, a in enumerate(u):
            seen[a + u[index:]] = True
        plus = int(np.count_nonzero(seen))
        seen[:] = False
        for index, a in enumerate(u):
            seen[u[index:] - a] = True  # nonnegative differences only
        minus = 2 * int(np.count_nonzero(seen)) - 1
        seen = None
    if minus > 2 * top + 1:
        return None
    return 1 + np.log(minus / plus) / np.log(2 * top + 1)


def _pairwise(points):
    diff = points[:, None, :] - points[None, :, :]
    return np.sqrt((diff ** 2).sum(-1))[np.triu_indices(len(points), 1)]


def distance_ratio_squared(value, dimension, count):
    x = np.asarray(value, dtype=np.float64)
    if x.shape != (count, dimension) or not np.all(np.isfinite(x)):
        return None
    d = _pairwise(x)
    return None if d.min() <= 0 else (d.max() / d.min()) ** 2


def _areas(points):
    import itertools
    return [abs((b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])) / 2
            for a, b, c in itertools.combinations(points, 3)]


def _hull_area(points):
    pts = sorted(map(tuple, points))
    def half(seq):
        out = []
        for p in seq:
            while len(out) >= 2 and ((out[-1][0] - out[-2][0]) * (p[1] - out[-2][1]) -
                                     (out[-1][1] - out[-2][1]) * (p[0] - out[-2][0])) <= 0:
                out.pop()
            out.append(p)
        return out
    hull = half(pts)[:-1] + half(reversed(pts))[:-1]
    return abs(sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(hull, hull[1:] + hull[:1]))) / 2


def heilbronn_triangle(points, a, b, c, count):
    x = np.asarray(points, dtype=np.float64)
    tri = np.asarray([a, b, c], dtype=np.float64)
    if x.shape != (count, 2) or tri.shape != (3, 2):
        return None
    area = _areas(tri)[0]
    matrix = np.array([[tri[0][0] - tri[2][0], tri[1][0] - tri[2][0]], [tri[0][1] - tri[2][1], tri[1][1] - tri[2][1]]])
    weights = np.linalg.solve(matrix, (x - tri[2]).T)
    barycentric = np.vstack([weights, 1 - weights.sum(0)])
    if area <= 0 or np.any(barycentric < -TOL):
        return None
    return min(_areas(x)) / area


def heilbronn_convex(points, count):
    x = np.asarray(points, dtype=np.float64)
    if x.shape != (count, 2):
        return None
    hull = _hull_area(x)
    return None if hull <= 0 else min(_areas(x)) / hull


def kissing(centers, dimension):
    x = np.asarray(centers)
    if x.ndim != 2 or x.shape[1] != dimension or x.dtype.kind not in "iu":
        return None
    # Exact Python integers: squared norms here reach ~1e26, far past int64.
    rows = [[int(v) for v in row] for row in x]
    norms = [sum(v * v for v in row) for row in rows]
    if min(norms) <= 0:
        return None
    # Lemma: nonzero points whose minimum squared distance is at least the maximum squared
    # norm are, once normalised, pairwise >= 60 degrees apart: a kissing configuration.
    largest = max(norms)
    for i, a in enumerate(rows):
        for b in rows[i + 1:]:
            if sum((p - q) * (p - q) for p, q in zip(a, b)) < largest:
                return None
    return len(rows)


def circles(value, count, box=None, perimeter=None):
    c = np.asarray(value, dtype=np.float64)
    if c.shape != (count, 3) or np.any(c[:, 2] <= 0):
        return None
    x, y, r = c.T
    if box is not None:
        if np.any(x - r < -TOL) or np.any(x + r > box + TOL) or np.any(y - r < -TOL) or np.any(y + r > box + TOL):
            return None
    else:
        width, height = (x + r).max() - (x - r).min(), (y + r).max() - (y - r).min()
        if 2 * (width + height) > perimeter + TOL:
            return None
    gaps = _pairwise(c[:, :2]) - (r[:, None] + r[None, :])[np.triu_indices(count, 1)]
    return None if gaps.min() < -TOL else float(r.sum())


# (section, variable, id, direction, published, printed decimals, verifier(value, namespace))
B_PROBLEMS = [
    ("B.1", "step_function_heights_1", "c1-autocorrelation", "minimize", 1.5053, 4, lambda v, ns: c1(v)),
    ("B.2", "heights_sequence_2", "c2-autocorrelation", "maximize", 0.8962, 4, lambda v, ns: c2(v)),
    ("B.3", "height_sequence_3", "c3-autocorrelation", "minimize", 1.4557, 4, lambda v, ns: c3(v)),
    ("B.3", "height_sequence_4", "c3prime-autocorrelation", "minimize", 1.4688, 4, lambda v, ns: c3(v, True)),
    ("B.5", "best_sequence", "c5-erdos-overlap", "minimize", 0.380924, 6, lambda v, ns: c5(v)),
    ("B.6", "solution_1", "c6-sums-differences-2003", "maximize", 1.1479, 4, lambda v, ns: c6(v)),
    ("B.6", "solution_2", "c6-sums-differences-54265", "maximize", 1.1584, 4, lambda v, ns: c6(v)),
    ("B.8", "construction_1", "maxmin-ratio-2d-16", "minimize", 12.889266112, 9, lambda v, ns: distance_ratio_squared(v, 2, 16)),
    ("B.8", "construction_2", "maxmin-ratio-3d-14", "minimize", 4.165849767, 9, lambda v, ns: distance_ratio_squared(v, 3, 14)),
    ("B.9", "found_points", "heilbronn-triangle-11", "maximize", 0.0365, 4,
     lambda v, ns: heilbronn_triangle(v, ns["a"], ns["b"], ns["c"], 11)),
    ("B.10", "construction_1", "heilbronn-convex-13", "maximize", 0.0309, 4, lambda v, ns: heilbronn_convex(v, 13)),
    ("B.10", "construction_2", "heilbronn-convex-14", "maximize", 0.0278, 4, lambda v, ns: heilbronn_convex(v, 14)),
    ("B.11", "sphere_centers", "kissing-11", "maximize", 593, 0, lambda v, ns: kissing(v, 11)),
    ("B.12", "construction_1", "circles-square-26", "maximize", 2.635, 3, lambda v, ns: circles(v, 26, box=1.0)),
    ("B.12", "construction_2", "circles-square-32", "maximize", 2.937, 3, lambda v, ns: circles(v, 32, box=1.0)),
    ("B.13", "circles", "circles-rectangle-21", "maximize", 2.365, 3, lambda v, ns: circles(v, 21, perimeter=4.0)),
]


def b_problems(cells):
    problems = []
    for section, variable, ident, direction, value, decimals, check in B_PROBLEMS:
        found = [ns for heading, ns in cells if heading and heading.startswith(section + ".") or
                 heading and heading.startswith(section + " ")]
        found = [ns for ns in found if variable in ns]
        if len(found) != 1:
            raise ValueError(f"{ident}: expected one data cell holding {variable} under {section}, found {len(found)}")
        namespace = found[0]
        problems.append(dict(id=ident, section=section, kind="analytic", direction=direction, published_value=value,
                             published_decimals=decimals, proven_optimal=False, citation=CITATION,
                             construction_variable=variable, construction=namespace[variable],
                             _check=check, _namespace=namespace))
    return problems


def verify(problem, construction):
    """Objective value of a candidate for `problem`, or None when any constraint fails."""
    if problem["kind"] == "tensor":
        q = problem["parameters"]
        return verify_tensor(construction, q["n"], q["m"], q["p"], q["ring"])
    if problem["kind"] == "analytic":
        try:
            return problem["_check"](construction, problem["_namespace"])
        except (ValueError, TypeError, IndexError, np.linalg.LinAlgError):
            return None
    raise ValueError(f"No verifier for {problem['kind']}")


def matches_published(problem, value):
    """Reproduces the published figure to its printed precision (or beats it)."""
    if value is None:
        return False
    if problem["kind"] == "tensor":
        return value == problem["published_value"]
    half = 0.5 * 10 ** -problem["published_decimals"]
    target = problem["published_value"]
    return value <= target + half if problem["direction"] == "minimize" else value >= target - half


def self_test(problems):
    """Every verifier must reproduce the published value from the published construction."""
    rows = []
    for pr in problems:
        value = verify(pr, pr["construction"])
        rows.append(dict(id=pr["id"], published=pr["published_value"], verified=value, matches=matches_published(pr, value)))
    return rows