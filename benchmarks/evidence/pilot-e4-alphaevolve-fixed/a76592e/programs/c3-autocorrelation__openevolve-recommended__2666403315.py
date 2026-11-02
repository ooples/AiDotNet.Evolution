import time
import numpy as np


def _val(h):
    s = h.sum()
    if abs(s) < 1e-12 or not np.all(np.isfinite(h)):
        return float("inf")
    return 2 * len(h) * np.max(np.convolve(h, h)) / s ** 2


def _refine(h, deadline, linprog, delta0=0.5):
    n = len(h)
    m = 2 * n - 1
    idx = np.arange(m)[:, None] - np.arange(n)[None, :]
    valid = (idx >= 0) & (idx < n)
    idc = np.clip(idx, 0, n - 1)
    cur = _val(h)
    delta = delta0 / n
    cost = np.zeros(n + 1)
    cost[-1] = 1
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1
    while time.time() < deadline and delta > 1e-8 / n:
        T = np.where(valid, h[idc], 0.0)
        c = np.convolve(h, h)
        A = np.hstack([2 * T, -np.ones((m, 1))])
        bounds = [(-delta, delta)] * n + [(None, None)]
        remaining = max(0.2, deadline - time.time())
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq, b_eq=[0],
                          bounds=bounds, method="highs",
                          options={"time_limit": float(remaining)})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = h + res.x[:n]
        hs = hn.sum()
        if abs(hs) < 1e-12:
            delta *= 0.5
            continue
        hn = hn / hs
        v = _val(hn)
        if v < cur - 1e-12:
            h, cur = hn, v
            delta *= 1.2
        else:
            delta *= 0.5
    return h, cur


def construct():
    best = np.ones(16)
    try:
        from scipy.optimize import linprog
        t0 = time.time()
        rng = np.random.default_rng(1)
        n = 40
        bestv = _val(best)
        cand_h, cand_v = None, float("inf")
        for k in range(8):
            h = (1 + 0.5 * rng.random(n)) / n
            h /= h.sum()
            h, v = _refine(h, time.time() + 1.2, linprog, 0.5)
            if v < cand_v:
                cand_h, cand_v = h.copy(), v
        h = cand_h
        if cand_v < bestv:
            bestv = cand_v
            best = cand_h.copy()
        for sz, end in [(80, 12.0), (160, 34.0), (320, 54.0)]:
            h = np.repeat(h, sz // len(h))
            h = h * (1 + 1e-3 * rng.standard_normal(len(h)))
            h = h / h.sum()
            if time.time() - t0 >= end - 1:
                continue
            h, v = _refine(h, t0 + end, linprog, 0.2)
            if np.all(np.isfinite(h)) and v <= bestv:
                bestv = v
                best = h.copy()
            else:
                h = best.copy()
                if len(h) != sz:
                    h = np.repeat(h, sz // len(h))
                    h = h / h.sum()
    except Exception:
        pass
    return [float(x) for x in best]