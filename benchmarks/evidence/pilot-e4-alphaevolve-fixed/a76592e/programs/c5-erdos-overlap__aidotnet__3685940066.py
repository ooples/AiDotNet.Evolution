import time
import numpy as np


def _obj(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _fix_sum(h):
    n = len(h)
    h = np.clip(np.asarray(h, dtype=float), 0, 1)
    for _ in range(200):
        r = n / 2.0 - h.sum()
        if abs(r) < 1e-13:
            break
        room = (1 - h) if r > 0 else h
        s = room.sum()
        if s <= 0:
            break
        h = h + np.sign(r) * min(abs(r), s) * room / s
        h = np.clip(h, 0, 1)
    return h


def _jac(h):
    n = len(h)
    m = 2 * n - 1
    g = 1 - h
    K = np.arange(m)[:, None]
    J = np.arange(n)[None, :]
    i1 = J - K + n - 1
    v1 = (i1 >= 0) & (i1 < n)
    i2 = J + K - n + 1
    v2 = (i2 >= 0) & (i2 < n)
    return (np.where(v1, g[np.clip(i1, 0, n - 1)], 0.0)
            - np.where(v2, h[np.clip(i2, 0, n - 1)], 0.0))


def _slp(h, deadline, tr0):
    from scipy.optimize import linprog
    n = len(h)
    cur = _obj(h)
    tr = tr0
    m = 2 * n - 1
    c = np.zeros(n + 1)
    c[-1] = 1.0
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1.0
    while time.time() < deadline and tr > 1e-9:
        g = 1 - h
        A = _jac(h)
        C = np.correlate(h, g, mode="full")
        A_ub = np.hstack([A, -np.ones((m, 1))])
        lo = np.maximum(-tr, -h)
        hi = np.minimum(tr, 1 - h)
        bounds = [(lo[i], hi[i]) for i in range(n)] + [(None, None)]
        tl = max(0.5, deadline - time.time())
        try:
            res = linprog(c, A_ub=A_ub, b_ub=-C, A_eq=A_eq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": tl})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            tr *= 0.5
            continue
        new = _fix_sum(h + res.x[:n])
        v = _obj(new)
        if v < cur - 1e-15:
            h, cur = new, v
            tr = min(tr * 1.3, 0.2)
        else:
            tr *= 0.5
    return h, cur


def construct():
    t0 = time.time()
    best = _fix_sum(np.full(50, 0.5))
    bestv = _obj(best)
    try:
        cand = None
        candv = 1e9
        for seed in range(5):
            now = time.time() - t0
            if now > 8:
                break
            rng = np.random.default_rng(seed)
            h = _fix_sum(0.5 + 0.15 * rng.standard_normal(50))
            h, v = _slp(h, t0 + min(now + 2.0, 8.0), 0.1)
            if v < candv:
                cand, candv = h.copy(), v
        if cand is not None and candv < bestv:
            best, bestv = cand.copy(), candv
        h = cand if cand is not None else best
        for n, tend in [(100, 18.0), (200, 38.0), (400, 53.0)]:
            h = _fix_sum(np.repeat(h, 2))
            h, v = _slp(h, t0 + tend, 0.03)
            if v < bestv:
                best, bestv = h.copy(), v
    except Exception:
        pass
    best = _fix_sum(np.array(best, dtype=float))
    if _obj(best) > 0.5 or abs(best.sum() - len(best) / 2) > 1e-9 * len(best):
        return [0.5] * 16
    return [float(x) for x in best]