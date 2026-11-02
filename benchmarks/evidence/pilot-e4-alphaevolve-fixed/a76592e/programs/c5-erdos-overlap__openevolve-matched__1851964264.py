import time
import numpy as np


def _val(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _fix_sum(h):
    n = len(h)
    h = np.clip(h, 0, 1)
    for _ in range(100):
        r = n / 2.0 - h.sum()
        if abs(r) < 1e-13:
            break
        room = (1 - h) if r > 0 else h
        tot = room.sum()
        if tot <= 0:
            break
        h = np.clip(h + np.sign(r) * room * min(1.0, abs(r) / tot), 0, 1)
    return h


def _optimize(h, deadline, tr, linprog):
    n = len(h)
    L = np.arange(-(n - 1), n)
    m = np.arange(n)
    i1 = m[None, :] - L[:, None]
    i2 = m[None, :] + L[:, None]
    v1 = (i1 >= 0) & (i1 < n)
    v2 = (i2 >= 0) & (i2 < n)
    i1c = np.clip(i1, 0, n - 1)
    i2c = np.clip(i2, 0, n - 1)
    cost = np.zeros(n + 1)
    cost[-1] = 1
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1
    cur = _val(h)
    dur = 0.0
    while tr > 1e-8:
        now = time.time()
        if now + 1.5 * dur > deadline:
            break
        g = 1 - h
        J = np.where(v1, g[i1c], 0.0) - np.where(v2, h[i2c], 0.0)
        c = np.correlate(h, g, mode="full")
        A = np.hstack([J, -np.ones((2 * n - 1, 1))])
        lo = np.maximum(-tr, -h)
        hi = np.minimum(tr, 1 - h)
        bounds = np.column_stack([lo, hi]).tolist() + [(None, None)]
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq,
                          b_eq=[n / 2.0 - h.sum()],
                          bounds=bounds, method="highs")
        except Exception:
            break
        dur = max(dur * 0.5, time.time() - now)
        if res.status != 0:
            tr *= 0.5
            continue
        d = res.x[:n]
        hn = _fix_sum(h + d)
        nv = _val(hn)
        if nv >= cur - 1e-13:
            h2 = _fix_sum(h + 0.5 * d)
            v2_ = _val(h2)
            if v2_ < nv:
                hn, nv = h2, v2_
        if nv < cur - 1e-13:
            h, cur = hn, nv
            tr = min(tr * 1.3, 0.3)
        else:
            tr *= 0.5
    return h


def construct():
    t0 = time.time()
    n = 100
    rng = np.random.default_rng(0)
    best = _fix_sum(0.5 + 0.1 * rng.standard_normal(n))
    bestv = _val(best)
    try:
        from scipy.optimize import linprog
        seed = 0
        while time.time() - t0 < 10.0:
            start = _fix_sum(0.5 + (0.1 + 0.05 * (seed % 3)) *
                             rng.standard_normal(n))
            end = min(t0 + 10.0, time.time() + 4.0)
            h = _optimize(start, end, 0.1, linprog)
            v = _val(h)
            if v < bestv:
                best, bestv = h.copy(), v
            seed += 1
        h = best
        for end, tr in ((24.0, 0.03), (54.0, 0.02)):
            h = _fix_sum(np.repeat(h, 2))
            h = _optimize(h, t0 + end, tr, linprog)
            v = _val(h)
            if v <= bestv + 1e-12:
                best, bestv = h.copy(), v
    except Exception:
        pass
    best = _fix_sum(np.asarray(best, dtype=float))
    if abs(best.sum() - len(best) / 2) > 1e-9 * len(best) or _val(best) > 0.5:
        return [0.5] * 16
    return [float(x) for x in best]