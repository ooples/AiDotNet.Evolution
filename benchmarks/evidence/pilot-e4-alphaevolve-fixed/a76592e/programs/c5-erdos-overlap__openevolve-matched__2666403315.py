import time
import numpy as np
from scipy.optimize import linprog


def _val(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _fix_sum(h):
    n = len(h)
    h = np.clip(np.array(h, dtype=float), 0, 1)
    for _ in range(200):
        diff = n / 2 - h.sum()
        if abs(diff) < 1e-13:
            break
        free = (h < 1) if diff > 0 else (h > 0)
        if not free.any():
            break
        h[free] += diff / free.sum()
        h = np.clip(h, 0, 1)
    return h


_cache = {}


def _idx(n):
    if n not in _cache:
        k = np.arange(2 * n - 1)[:, None]
        j = np.arange(n)[None, :]
        i1 = j - k + n - 1
        i2 = j + k - (n - 1)
        v1 = (i1 >= 0) & (i1 < n)
        v2 = (i2 >= 0) & (i2 < n)
        _cache[n] = (np.clip(i1, 0, n - 1), v1, np.clip(i2, 0, n - 1), v2)
    return _cache[n]


def _slp(h, deadline, r):
    n = len(h)
    i1, v1, i2, v2 = _idx(n)
    m = 2 * n - 1
    best = h.copy()
    bv = _val(h)
    h = best.copy()
    cost = np.zeros(n + 1)
    cost[-1] = 1
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1
    while time.time() < deadline - 0.5 and r > 1e-8:
        g = 1 - h
        c = np.correlate(h, g, mode="full")
        J = np.where(v1, g[i1], 0.0) - np.where(v2, h[i2], 0.0)
        A = np.hstack([J, -np.ones((m, 1))])
        lo = np.maximum(-h, -r)
        hi = np.minimum(1 - h, r)
        bounds = list(zip(lo, hi)) + [(None, None)]
        tl = max(1.0, min(8.0, deadline - time.time() - 0.3))
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": tl})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        hn = _fix_sum(h + res.x[:n])
        v = _val(hn)
        if v < bv - 1e-13:
            bv = v
            best = hn.copy()
            h = hn
            r = min(r * 1.3, 0.3)
        else:
            r *= 0.5
    return best


def _resample(h, newn):
    n0 = len(h)
    x = (np.arange(newn) + 0.5) / newn * n0 - 0.5
    return _fix_sum(np.interp(x, np.arange(n0), h))


def construct():
    t0 = time.time()
    rng = np.random.default_rng(0)
    h = _fix_sum(0.5 + 0.1 * rng.standard_normal(100))
    best = h
    try:
        h1 = _slp(h, t0 + 9, 0.1)
        best = min([h, h1], key=_val)
        for newn, end, r in ((200, 25, 0.03), (300, 54, 0.02)):
            if time.time() > t0 + end - 6:
                break
            try:
                cand = _slp(_resample(best, newn), t0 + end, r)
            except Exception:
                continue
            # compare in terms of value (scale-invariant)
            if _val(cand) < _val(best):
                best = cand
    except Exception:
        pass
    best = _fix_sum(np.array(best, dtype=float))
    if _val(best) > 0.5 or abs(best.sum() - len(best) / 2) > 1e-9 * len(best):
        return [0.5] * 16
    return [float(x) for x in best]