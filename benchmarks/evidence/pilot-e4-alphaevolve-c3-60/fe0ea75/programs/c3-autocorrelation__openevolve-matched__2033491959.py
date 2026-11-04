import time
import numpy as np
from scipy.signal import fftconvolve
from scipy.optimize import minimize


def _score(h):
    h = np.asarray(h, dtype=float)
    n = len(h)
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = np.convolve(h, h) if n < 64 else fftconvolve(h, h)
    return 2.0 * n * abs(c.max()) / s ** 2


def _make_fun(p, n):
    def fun(h):
        s = h.sum()
        if s <= 1e-9:
            return 1e6, np.zeros(n)
        c = np.maximum(fftconvolve(h, h), 1e-300)
        cmax = c.max()
        r = c / cmax
        S = (r ** p).sum()
        obj = np.log(S) / p + np.log(cmax) - 2.0 * np.log(s)
        w = (r ** (p - 1)) / (cmax * S)
        g = 2.0 * fftconvolve(w, h[::-1])[n - 1:2 * n - 1] - 2.0 / s
        return obj, g
    return fun


def _slp(h, t0, limit):
    from scipy.optimize import linprog
    from scipy.linalg import toeplitz
    n = len(h)
    h = h / h.sum()
    best = _score(h)
    r = 0.1 / n
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    A_eq = np.concatenate([np.ones(n), [0.0]])[None, :]
    while time.time() - t0 < limit - 1 and r > 1e-11 / n:
        c = np.convolve(h, h)
        col = np.concatenate([h, np.zeros(n - 1)])
        row = np.zeros(n)
        row[0] = h[0]
        A = toeplitz(col, row)
        margin = 2 * r + r * r * n + 1e-12
        idx = np.where(c >= c.max() - margin)[0]
        A_ub = np.hstack([2 * A[idx], -np.ones((len(idx), 1))])
        bounds = [(-r, r)] * n + [(None, None)]
        tl = max(1.0, min(8.0, limit - (time.time() - t0)))
        try:
            res = linprog(cost, A_ub=A_ub, b_ub=-c[idx], A_eq=A_eq,
                          b_eq=[0.0], bounds=bounds, method="highs",
                          options={"time_limit": tl})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        hn = h + res.x[:n]
        if hn.sum() <= 0:
            r *= 0.5
            continue
        hn = hn / hn.sum()
        v = _score(hn)
        if v < best - 1e-13:
            best = v
            h = hn
            r *= 1.3
        else:
            r *= 0.5
    return h


PS = [4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192]


def _run(x, n, ps, t0, tlimit, maxiter):
    for p in ps:
        if time.time() - t0 > tlimit:
            break
        try:
            res = minimize(_make_fun(p, n), x, jac=True, method="L-BFGS-B",
                           options={"maxiter": maxiter, "maxfun": maxiter + 1000})
            x = res.x
        except Exception:
            break
    return x


def construct():
    t0 = time.time()
    rng = np.random.default_rng(0)
    best = [1.0] * 16
    best_val = _score(best)
    small = None
    small_val = float("inf")

    # multi-start at coarse level
    n0 = 50
    cur = None
    cv = float("inf")
    for seed in range(4):
        x = np.ones(n0) + 0.3 * rng.standard_normal(n0)
        x = _run(x, n0, PS, t0, 10, 4000)
        v = _score(x)
        if np.isfinite(v) and v < cv:
            cv = v
            cur = x / x.sum()

    sizes = [100, 200, 400, 800, 1600]
    for n in sizes:
        if time.time() - t0 > 28:
            break
        x = np.repeat(cur, 2)
        x = x + 0.02 * np.abs(x).mean() * rng.standard_normal(n)
        x = x / x.sum()
        x = _run(x, n, PS[2:], t0, 30, 5000)
        v = _score(x)
        if np.isfinite(v):
            if v < best_val:
                best_val = v
                best = [float(a) for a in x]
            if n <= 1000 and v < small_val:
                small_val = v
                small = np.array(x, dtype=float)
            cur = x / x.sum()

    try:
        h0 = small if small is not None else np.array(best, dtype=float)
        if 64 <= len(h0) <= 1000:
            h1 = _slp(h0, t0, 52.0)
            v = _score(h1)
            if np.isfinite(v) and v < best_val:
                best_val = v
                best = [float(a) for a in h1]
    except Exception:
        pass

    if not np.isfinite(_score(best)):
        best = [1.0] * 16
    return best