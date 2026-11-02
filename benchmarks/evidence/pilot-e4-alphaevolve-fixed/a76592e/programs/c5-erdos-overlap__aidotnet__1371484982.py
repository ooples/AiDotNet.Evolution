import time
import numpy as np
from scipy.optimize import linprog


def _c(h):
    return np.correlate(h, 1 - h, mode="full")


def _jac(h):
    n = len(h)
    g = 1 - h
    D = (np.arange(2 * n - 1) - (n - 1))[:, None]
    P = np.arange(n)[None, :]
    i1 = P - D
    v1 = (i1 >= 0) & (i1 < n)
    J = np.where(v1, g[np.clip(i1, 0, n - 1)], 0.0)
    i2 = P + D
    v2 = (i2 >= 0) & (i2 < n)
    J -= np.where(v2, h[np.clip(i2, 0, n - 1)], 0.0)
    return J


def _fix(h, n):
    h = np.clip(h, 0, 1)
    for _ in range(100):
        diff = n / 2 - h.sum()
        if abs(diff) < 1e-13:
            break
        free = (h < 1) if diff > 0 else (h > 0)
        if not free.any():
            break
        h[free] += diff / free.sum()
        h = np.clip(h, 0, 1)
    return h


def _refine(best, deadline, r):
    n = len(best)
    bv = _c(best).max()
    cost = np.zeros(n + 1)
    cost[-1] = 1
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1
    while time.time() < deadline and r > 1e-9:
        c = _c(best)
        J = _jac(best)
        lo = np.maximum(-r, -best)
        hi = np.minimum(r, 1 - best)
        A = np.hstack([J, -np.ones((2 * n - 1, 1))])
        bounds = list(zip(lo, hi)) + [(None, None)]
        rem = max(1.0, deadline - time.time())
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": rem})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        cand = _fix(best + res.x[:n], n)
        v = _c(cand).max()
        if v < bv - 1e-13:
            best, bv = cand, v
            r = min(r * 1.3, 0.3)
        else:
            r *= 0.5
    return best, bv


def construct():
    t0 = time.time()
    n = 160
    rng = np.random.default_rng(1)
    h = _fix(0.5 + rng.uniform(-0.3, 0.3, n), n)
    best, bv = _refine(h, t0 + 14, 0.1)
    final, fv = best, 2 * bv / n
    try:
        h2 = _fix(np.repeat(best, 2).astype(float), 2 * n)
        b2, v2 = _refine(h2, t0 + 50, 0.03)
        if 2 * v2 / (2 * n) < fv:
            final, fv = b2, 2 * v2 / (2 * n)
    except Exception:
        pass
    m = len(final)
    if abs(final.sum() - m / 2) > 1e-9 * m or fv > 0.5:
        return [0.5] * 16
    return [float(x) for x in final]