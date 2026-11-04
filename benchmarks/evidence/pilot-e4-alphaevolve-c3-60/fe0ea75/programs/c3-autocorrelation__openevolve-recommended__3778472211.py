import time
import numpy as np


def _value(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    n = len(h)
    return 2.0 * n * np.max(np.convolve(h, h)) / s ** 2


def _optimize(h, deadline, r0):
    from scipy.optimize import linprog
    n = len(h)
    h = h / h.sum()
    cur = _value(h)
    best, bestv = h.copy(), cur
    r = r0 / n
    cost = np.zeros(n + 1)
    cost[n] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    ar = np.arange(n)
    while r > 1e-10:
        now = time.time()
        if now >= deadline - 0.3:
            break
        c = np.convolve(h, h)
        margin = 2.0 * r * np.abs(h).sum() + r * r * n
        rows = np.nonzero(c >= c.max() - 1.5 * margin)[0]
        idx = rows[:, None] - ar[None, :]
        valid = (idx >= 0) & (idx < n)
        A = np.zeros((len(rows), n + 1))
        A[:, :n] = np.where(valid, 2 * h[np.clip(idx, 0, n - 1)], 0.0)
        A[:, n] = -1.0
        bounds = [(-r, r)] * n + [(None, None)]
        tl = max(0.5, min(8.0, deadline - now))
        try:
            res = linprog(cost, A_ub=A, b_ub=-c[rows], A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": tl})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        hn = h + res.x[:n]
        s = hn.sum()
        if abs(s) < 1e-12:
            r *= 0.5
            continue
        hn = hn / s
        v = _value(hn)
        if v < cur:
            h, cur = hn, v
            r *= 1.3
            if v < bestv:
                best, bestv = hn.copy(), v
        else:
            r *= 0.5
    return best, bestv


def _upsample(h, n2, rng, noise):
    n = len(h)
    x1 = (np.arange(n) + 0.5) / n
    x2 = (np.arange(n2) + 0.5) / n2
    g = np.interp(x2, x1, h)
    g = g / g.sum()
    g = g + rng.normal(0, noise / n2, n2)
    return g / g.sum()


def construct():
    t0 = time.time()
    best = np.ones(16)
    bestv = _value(best)
    try:
        rng = np.random.default_rng(1)
        n0 = 50
        cands = []
        for k in range(4):
            h = np.ones(n0) / n0 + rng.normal(0, 0.3 / n0, n0)
            h = h / h.sum()
            hh, v = _optimize(h, t0 + 3.5 * (k + 1), 0.5)
            cands.append((v, hh))
        cands.sort(key=lambda t: t[0])
        v, cur = cands[0]
        if v < bestv:
            best, bestv = cur.copy(), v
        stages = ((100, 20, 0.05, 0.03), (200, 31, 0.03, 0.02),
                  (400, 42, 0.02, 0.01), (800, 52, 0.01, 0.005))
        for n2, dl, r0, nz in stages:
            if time.time() > t0 + dl - 3:
                continue
            cand = _upsample(best, n2, rng, nz)
            cand, v = _optimize(cand, t0 + dl, r0)
            if v < bestv:
                best, bestv = cand.copy(), v
        if not np.isfinite(bestv) or bestv > 2.0:
            best = np.ones(16)
    except Exception:
        pass
    return [float(x) for x in best]