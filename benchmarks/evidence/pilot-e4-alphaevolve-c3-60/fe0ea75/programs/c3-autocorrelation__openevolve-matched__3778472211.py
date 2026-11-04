import time
import numpy as np


def _val(h):
    c = np.convolve(h, h)
    return 2 * len(h) * abs(c.max()) / (h.sum() ** 2)


def _refine(best, r, deadline, rmin=1e-7):
    from scipy.optimize import linprog
    from scipy.sparse import hstack, csr_matrix
    n = len(best)
    m = 2 * n - 1
    bv = _val(best)
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    A_eq = np.hstack([np.ones((1, n)), np.zeros((1, 1))])
    ii = np.arange(n)
    kk = np.arange(m)
    while r > rmin:
        rem = deadline - time.time()
        if rem < 0.5:
            break
        h = best
        c = np.convolve(h, h)
        bound = 2 * r * np.abs(h).sum() + r * r * n
        sel = kk[c >= c.max() - 2.2 * bound]
        if len(sel) < 1:
            sel = kk
        D = sel[:, None] - ii[None, :]
        mask = (D >= 0) & (D < n)
        A = np.where(mask, 2 * h[np.clip(D, 0, n - 1)], 0.0)
        A_ub = hstack([csr_matrix(A), csr_matrix(-np.ones((len(sel), 1)))]).tocsr()
        try:
            res = linprog(cost, A_ub=A_ub, b_ub=-c[sel], A_eq=A_eq, b_eq=[0.0],
                          bounds=[(-r, r)] * n + [(None, None)],
                          method="highs",
                          options={"time_limit": max(rem, 0.5)})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        cand = h + res.x[:n]
        s = cand.sum()
        if abs(s) < 1e-9:
            r *= 0.5
            continue
        cand = cand * (n / s)
        v = _val(cand)
        if v < bv - 1e-12:
            best, bv = cand, v
            r = min(r * 1.2, 0.3)
        else:
            r *= 0.5
    return best, bv


def construct():
    t0 = time.time()
    total = 53.0
    n = 60
    rng = np.random.default_rng(0)
    best = np.ones(n) + 0.05 * rng.standard_normal(n)
    best = best * (n / best.sum())
    gbest, gv = best.copy(), _val(best)
    try:
        stages = [(60, 5.0, 0.2), (120, 8.0, 0.1), (200, 10.0, 0.05),
                  (300, 11.0, 0.04), (400, 1e9, 0.03)]
        for i, (sz, dur, r0) in enumerate(stages):
            if i > 0:
                prev = best
                pos = (np.arange(sz) * len(prev) / sz).astype(int)
                best = prev[pos]
                best = best * (sz / best.sum())
            deadline = min(t0 + total, time.time() + dur)
            best, bv = _refine(best, r0, deadline)
            if bv < gv or len(best) != len(gbest):
                gbest, gv = best.copy(), bv
            if time.time() > t0 + total - 1:
                break
        while time.time() < t0 + total - 2:
            nb, nv = _refine(gbest.copy(), 0.005, t0 + total)
            if nv < gv - 1e-10:
                gbest, gv = nb.copy(), nv
            else:
                break
    except Exception:
        pass
    if gv >= 2.0 or not np.all(np.isfinite(gbest)):
        return [1.0] * 16
    return [float(x) for x in gbest]