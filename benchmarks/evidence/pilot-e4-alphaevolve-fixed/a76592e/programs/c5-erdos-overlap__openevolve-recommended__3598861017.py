import time
import numpy as np


def _val(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _jac_rows(h, ks):
    n = len(h)
    g = 1 - h
    k = ks[:, None]
    p = np.arange(n)[None, :]
    i1 = p - k
    i2 = p + k
    m1 = (i1 >= 0) & (i1 < n)
    m2 = (i2 >= 0) & (i2 < n)
    A = np.where(m1, g[np.clip(i1, 0, n - 1)], 0.0)
    A -= np.where(m2, h[np.clip(i2, 0, n - 1)], 0.0)
    return A


def _fix_sum(h):
    n = len(h)
    h = np.clip(h, 0, 1)
    for _ in range(100):
        r = n / 2 - h.sum()
        if abs(r) < 1e-12:
            break
        free = (h < 1) if r > 0 else (h > 0)
        cnt = free.sum()
        if cnt == 0:
            break
        h[free] += r / cnt
        h = np.clip(h, 0, 1)
    return h


def _optimize(h, t0, tlimit, delta, linprog):
    n = len(h)
    best = _val(h)
    allk = np.arange(-(n - 1), n)
    while delta > 1e-8:
        rem = tlimit - (time.time() - t0)
        if rem < 0.8:
            break
        c0 = np.correlate(h, 1 - h, mode="full")
        M0 = c0.max()
        sel = c0 >= M0 - 2.5 * delta * n
        ks = allk[sel]
        A = _jac_rows(h, ks)
        cs = c0[sel]
        A_ub = np.hstack([A, -np.ones((A.shape[0], 1))])
        b_ub = A @ h - cs
        A_eq = np.zeros((1, n + 1))
        A_eq[0, :n] = 1
        b_eq = [n / 2]
        lo = np.maximum(0, h - delta)
        hi = np.minimum(1, h + delta)
        bounds = list(zip(lo, hi)) + [(None, None)]
        cost = np.zeros(n + 1)
        cost[-1] = 1
        try:
            res = linprog(cost, A_ub=A_ub, b_ub=b_ub, A_eq=A_eq,
                          b_eq=b_eq, bounds=bounds, method="highs",
                          options={"time_limit": float(min(6.0, rem))})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = _fix_sum(res.x[:n].copy())
        v = _val(hn)
        if v < best - 1e-13:
            best = v
            h = hn
            delta = min(delta * 1.3, 0.5)
        else:
            delta *= 0.5
    return h


def _ok(h):
    return abs(h.sum() - len(h) / 2) < 1e-9 * len(h)


def _resample(h, m):
    n = len(h)
    idx = np.minimum((np.arange(m) * n) // m, n - 1)
    return _fix_sum(h[idx].astype(float))


def construct():
    try:
        from scipy.optimize import linprog
        t0 = time.time()
        n = 80
        rng = np.random.default_rng(1)
        h = _fix_sum(rng.random(n))
        h = _optimize(h, t0, 10, 0.2, linprog)
        h = _fix_sum(h.copy())
        best_h = h.copy()
        best_v = _val(h)
        stages = ((120, 22, 0.03), (160, 36, 0.015), (240, 52, 0.008))
        for m, tl, d in stages:
            if time.time() - t0 > tl - 5:
                continue
            h2 = _resample(best_h, m)
            h2 = _optimize(h2, t0, tl, d, linprog)
            h2 = _fix_sum(h2.copy())
            if _ok(h2) and _val(h2) < best_v:
                best_h = h2
                best_v = _val(h2)
        h = best_h
        if _ok(h) and _val(h) < 0.5:
            return [float(x) for x in h]
    except Exception:
        pass
    return [0.5] * 16