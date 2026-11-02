import time
import numpy as np


def _val(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _fix_sum(h, target):
    h = np.clip(h, 0.0, 1.0)
    for _ in range(200):
        diff = target - h.sum()
        if abs(diff) < 1e-13:
            break
        room = (1 - h) if diff > 0 else h
        tot = room.sum()
        if tot <= 0:
            break
        h = np.clip(h + diff * room / tot, 0.0, 1.0)
    return h


def _refine(h, deadline, tr):
    from scipy.optimize import linprog
    n = len(h)
    D = 2 * n - 1
    J = np.arange(n)[None, :]
    K = np.arange(D)[:, None] - (n - 1)
    a = J - K
    b = J + K
    va = (a >= 0) & (a < n)
    vb = (b >= 0) & (b < n)
    ac = np.clip(a, 0, n - 1)
    bc = np.clip(b, 0, n - 1)
    cost = np.zeros(n + 1)
    cost[-1] = 1
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1
    cur = _val(h)
    while time.time() < deadline and tr > 1e-10:
        g = 1 - h
        M = np.where(va, g[ac], 0.0) - np.where(vb, h[bc], 0.0)
        c = np.correlate(h, g, mode="full")
        A_ub = np.hstack([M, -np.ones((D, 1))])
        lo = np.maximum(-tr, -h)
        hi = np.minimum(tr, 1 - h)
        bounds = list(zip(lo, hi)) + [(None, None)]
        try:
            res = linprog(cost, A_ub=A_ub, b_ub=-c, A_eq=A_eq, b_eq=[0],
                          bounds=bounds, method="highs",
                          options={"time_limit": max(1.0, deadline - time.time())})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            tr *= 0.5
            continue
        nh = _fix_sum(h + res.x[:n], n / 2.0)
        nv = _val(nh)
        if nv < cur - 1e-14:
            h, cur = nh, nv
            tr = min(tr * 1.5, 0.2)
        else:
            tr *= 0.5
    return h


def construct():
    t0 = time.time()
    try:
        n = 120
        rng = np.random.default_rng(1)
        x = np.linspace(0, 1, n)
        h = 0.5 + 0.3 * (np.exp(-((x - 0.5) ** 2) / 0.08) - 0.5)
        h = h + 0.05 * rng.standard_normal(n)
        h = _fix_sum(h, n / 2.0)
        h = _refine(h, t0 + 36, 0.1)
        best, bestv = h.copy(), _val(h)
        h2 = _refine(best.copy(), t0 + 50, 0.02)
        v2 = _val(h2)
        if v2 < bestv:
            best, bestv = h2, v2
        best = _fix_sum(best, n / 2.0)
        if abs(best.sum() - n / 2.0) < 1e-9 * n and _val(best) < 0.5:
            return [float(v) for v in best]
    except Exception:
        pass
    return [0.5] * 16