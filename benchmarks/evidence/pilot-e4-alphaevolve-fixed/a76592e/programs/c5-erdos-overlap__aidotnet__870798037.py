import time
import numpy as np
from scipy.optimize import linprog


def _val(h):
    n = len(h)
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _jac(h):
    n = len(h)
    b = 1 - h
    K = np.arange(2 * n - 1)[:, None]
    J = np.arange(n)[None, :]
    m1 = J - K + n - 1
    ok1 = (m1 >= 0) & (m1 < n)
    A = np.where(ok1, b[np.clip(m1, 0, n - 1)], 0.0)
    i2 = J + K - (n - 1)
    ok2 = (i2 >= 0) & (i2 < n)
    B = np.where(ok2, h[np.clip(i2, 0, n - 1)], 0.0)
    return A - B


def _fix(h, target):
    h = np.clip(np.array(h, dtype=float), 0, 1)
    for _ in range(300):
        r = target - h.sum()
        if abs(r) < 1e-12:
            break
        free = h < 1 if r > 0 else h > 0
        if not free.any():
            break
        h[free] += r / free.sum()
        h = np.clip(h, 0, 1)
    return h


def _refine(h, deadline, delta):
    n = len(h)
    target = n / 2.0
    best = _val(h)
    cost = np.zeros(n + 1)
    cost[-1] = 1
    Aeq = np.hstack([np.ones((1, n)), np.zeros((1, 1))])
    last = 0.0
    while delta > 1e-8 and time.time() + last * 1.3 < deadline:
        t = time.time()
        c = np.correlate(h, 1 - h, mode="full")
        Aub = np.hstack([_jac(h), -np.ones((len(c), 1))])
        lo = np.maximum(-delta, -h)
        hi = np.minimum(delta, 1 - h)
        bounds = list(zip(lo, hi)) + [(None, None)]
        try:
            res = linprog(cost, A_ub=Aub, b_ub=-c, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs")
        except Exception:
            break
        last = time.time() - t
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = _fix(h + res.x[:n], target)
        v = _val(hn)
        if v < best - 1e-13:
            h, best = hn, v
            delta = min(delta * 1.3, 0.2)
        else:
            delta *= 0.5
    return h, best


def _up(h, m):
    n = len(h)
    x = (np.arange(n) + 0.5) / n
    y = (np.arange(m) + 0.5) / m
    return np.interp(y, x, h)


def construct():
    t0 = time.time()
    total = 52.0
    n = 40
    best_h, best_v = None, 1e9
    try:
        x = (np.arange(n) + 0.5) / n
        for seed in range(4):
            rng = np.random.default_rng(seed + 1)
            h = 0.5 + 0.25 * np.sin(np.pi * x) - 0.25 * (2 / np.pi) \
                + 0.06 * rng.standard_normal(n)
            h = _fix(h, n / 2.0)
            h, v = _refine(h, time.time() + 1.5, 0.1)
            if v < best_v:
                best_h, best_v = h.copy(), v
        sizes = [80, 160, 320, 560]
        deadlines = [7, 18, 35, total]
        deltas = [0.03, 0.015, 0.008, 0.004]
        for m, dl, d in zip(sizes, deadlines, deltas):
            if time.time() - t0 > total - 1:
                break
            h = _fix(_up(best_h, m), m / 2.0)
            h, v = _refine(h, t0 + dl, d)
            h = _fix(h, m / 2.0)
            v = _val(h)
            if v < best_v:
                best_h, best_v = h.copy(), v
    except Exception:
        pass
    if best_h is None:
        return [0.5] * 16
    best_h = _fix(best_h, len(best_h) / 2.0)
    return [float(v) for v in best_h]