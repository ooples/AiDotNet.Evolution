import time
import numpy as np


def _obj(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    return 2 * len(h) * np.max(np.convolve(h, h)) / s ** 2


def _refine(h, deadline, delta0):
    from scipy.optimize import linprog
    n = len(h)
    m = 2 * n - 1
    idx = np.arange(m)[:, None] - np.arange(n)[None, :]
    valid = (idx >= 0) & (idx < n)
    idx = np.clip(idx, 0, n - 1)
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    h = h.copy()
    cur_v = _obj(h)
    delta = delta0
    while time.time() < deadline - 1.2 and delta > 1e-7:
        c = np.convolve(h, h)
        S = np.abs(h).sum()
        rows = np.where(c >= c.max() - 4 * delta * S)[0]
        M = np.where(valid[rows], h[idx[rows]], 0.0)
        A = np.hstack([2 * M, -np.ones((len(rows), 1))])
        bounds = [(-delta, delta)] * n + [(None, None)]
        remaining = max(1.0, deadline - time.time() - 0.5)
        try:
            res = linprog(cost, A_ub=A, b_ub=-c[rows], A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": remaining})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = h + res.x[:n]
        v = _obj(hn)
        if v < cur_v:
            h, cur_v = hn, v
            delta = min(delta * 1.3, 0.3)
        else:
            delta *= 0.5
    return h, cur_v


def construct():
    t0 = time.time()
    rng = np.random.default_rng(1)
    n = 40
    best = np.ones(n)
    best_v = _obj(best)
    try:
        h = np.ones(n) + 0.2 * rng.random(n)
        h *= n / h.sum()
        stages = [(40, 4), (80, 9), (160, 20), (320, 54)]
        for k, (size, tend) in enumerate(stages):
            if k > 0:
                h = np.interp(np.linspace(0, n - 1, size), np.arange(n), h)
                n = size
                h *= n / h.sum()
            h, _ = _refine(h, t0 + tend, 0.2 if k == 0 else 0.05)
            h = h * (n / h.sum())
            v = _obj(h)
            if v < best_v or k == len(stages) - 1:
                if v < best_v or len(best) != n:
                    best_v, best = v, h.copy()
            if time.time() - t0 > 55:
                break
    except Exception:
        pass
    return [float(x) for x in best]