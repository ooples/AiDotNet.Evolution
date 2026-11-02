import time
import numpy as np


def _obj(h):
    n = len(h)
    s = h.sum()
    return 2.0 * n * np.max(np.convolve(h, h)) / (s * s)


def _refine(h, deadline, r0):
    from scipy.optimize import linprog
    n = len(h)
    h = h * (n / h.sum())
    best = h.copy()
    bestv = _obj(h)
    K = np.arange(2 * n - 1)[:, None] - np.arange(n)[None, :]
    mask = (K >= 0) & (K < n)
    Kc = np.clip(K, 0, n - 1)
    c = np.zeros(n + 1)
    c[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    r = r0
    while time.time() < deadline - 0.5 and r > 1e-8:
        conv = np.convolve(h, h)
        A = 2.0 * np.where(mask, h[Kc], 0.0)
        Aub = np.hstack([A, -np.ones((2 * n - 1, 1))])
        bounds = [(-r, r)] * n + [(None, None)]
        remaining = max(0.5, deadline - time.time() - 0.3)
        try:
            res = linprog(c, A_ub=Aub, b_ub=-conv, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": remaining})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        hn = h + res.x[:n]
        if hn.sum() <= 0:
            r *= 0.5
            continue
        hn = hn * (n / hn.sum())
        v = _obj(hn)
        if v < bestv - 1e-13:
            h = hn
            best = hn.copy()
            bestv = v
            r = min(r * 1.3, 0.5)
        else:
            r *= 0.5
    return best, bestv


def construct():
    t0 = time.time()
    rng = np.random.default_rng(0)
    n = 200
    h = np.ones(n) + 0.3 * rng.random(n)
    h *= n / h.sum()
    best = h.copy()
    bestv = _obj(best)
    try:
        cur, v = _refine(h, t0 + 28, 0.2)
        if v < bestv:
            best, bestv = cur.copy(), v
        if time.time() < t0 + 36:
            up = np.repeat(best, 2)
            up, v = _refine(up, t0 + 52, 0.03)
            if v < bestv - 1e-13:
                best, bestv = up.copy(), v
    except Exception:
        pass
    return [float(x) for x in best]