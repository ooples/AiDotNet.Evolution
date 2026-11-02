import time
import numpy as np


def _score(h):
    s = h.sum()
    if s == 0:
        return float("inf")
    return 2 * len(h) * abs(np.convolve(h, h).max()) / s ** 2


def _convmat(h):
    n = len(h)
    A = np.zeros((2 * n - 1, n))
    for j in range(n):
        A[j:j + n, j] = h
    return A


def construct():
    t0 = time.time()
    n = 200
    best = np.ones(n)
    try:
        from scipy.optimize import linprog

        rng = np.random.default_rng(1)
        h = 1.0 + 0.3 * rng.random(n)
        h *= n / h.sum()
        best = h.copy()
        fbest = _score(best)
        delta = 0.2
        cost = np.zeros(n + 1)
        cost[-1] = 1
        A_eq = np.zeros((1, n + 1))
        A_eq[0, :n] = 1
        while time.time() - t0 < 46 and delta > 1e-7:
            c = np.convolve(h, h)
            rows = np.where(c >= 0.6 * c.max())[0]
            A = _convmat(h)[rows]
            A_ub = np.hstack([2 * A, -np.ones((len(rows), 1))])
            bounds = [(-delta, delta)] * n + [(None, None)]
            tl = max(1.0, min(8.0, 52 - (time.time() - t0)))
            try:
                res = linprog(cost, A_ub=A_ub, b_ub=-c[rows], A_eq=A_eq,
                              b_eq=[0], bounds=bounds, method="highs",
                              options={"time_limit": tl})
            except Exception:
                break
            if res.status != 0 or res.x is None:
                delta *= 0.5
                continue
            hn = h + res.x[:n]
            if hn.sum() <= 0:
                delta *= 0.5
                continue
            hn *= n / hn.sum()
            f = _score(hn)
            if f < fbest - 1e-13:
                fbest = f
                best = hn.copy()
                h = hn
                delta = min(delta * 1.3, 0.5)
            else:
                delta *= 0.5
        if _score(best) > 2.0:
            best = np.ones(n)
    except Exception:
        best = np.ones(16)
    return [float(x) for x in best]