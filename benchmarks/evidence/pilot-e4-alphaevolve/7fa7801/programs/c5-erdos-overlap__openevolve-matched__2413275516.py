import time
import numpy as np


def _project(x, target):
    lo, hi = x.min() - 1.0, x.max()
    for _ in range(100):
        mid = 0.5 * (lo + hi)
        if np.clip(x - mid, 0, 1).sum() > target:
            lo = mid
        else:
            hi = mid
    return np.clip(x - 0.5 * (lo + hi), 0, 1)


def _value(h):
    n = len(h)
    return 2.0 * np.correlate(h, 1 - h, mode="full").max() / n


def _jac(h):
    n = len(h)
    g = 1.0 - h
    P = np.arange(n)[None, :]
    K = np.arange(-(n - 1), n)[:, None]
    j = P - K
    q = P + K
    a = np.where((j >= 0) & (j < n), g[np.clip(j, 0, n - 1)], 0.0)
    b = np.where((q >= 0) & (q < n), h[np.clip(q, 0, n - 1)], 0.0)
    return a - b


def _fix_sum(h, target):
    h = np.clip(h, 0, 1).copy()
    for _ in range(100):
        diff = target - h.sum()
        if abs(diff) < 1e-12:
            break
        free = (h > 1e-12) & (h < 1 - 1e-12)
        if not free.any():
            free = (h < 1) if diff > 0 else (h > 0)
            if not free.any():
                break
        h[free] += diff / free.sum()
        h = np.clip(h, 0, 1)
    return h


def _adam(n, target, t0, limit):
    rng = np.random.default_rng(0)
    best_h = np.full(n, 0.5)
    best_v = _value(best_h)
    steps = 1500
    for _ in range(100):
        if time.time() - t0 > limit:
            break
        h = _project(0.5 + 0.3 * rng.standard_normal(n), target)
        m = np.zeros(n)
        v = np.zeros(n)
        for it in range(1, steps + 1):
            g = 1 - h
            c = np.correlate(h, g, mode="full")
            beta = 20.0 + 3000.0 * it / steps
            w = np.exp(beta * (c - c.max()))
            w /= w.sum()
            t1 = np.convolve(w, g)[n - 1:2 * n - 1]
            t2 = np.convolve(w[::-1], h)[n - 1:2 * n - 1]
            grad = t1 - t2
            m = 0.9 * m + 0.1 * grad
            v = 0.999 * v + 0.001 * grad * grad
            lr = 0.02 * (1 - it / steps) + 0.001
            x = h - lr * (m / (1 - 0.9 ** it)) / (
                np.sqrt(v / (1 - 0.999 ** it)) + 1e-12)
            h = _project(x, target)
            if it % 50 == 0 or it == steps:
                val = _value(h)
                if val < best_v:
                    best_v, best_h = val, h.copy()
    return best_h, best_v


def _slp(best, target, t0, limit, delta):
    from scipy.optimize import linprog
    n = len(best)
    bestv = _value(best)
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1.0
    while time.time() - t0 < limit and delta > 1e-9:
        try:
            c = np.correlate(best, 1.0 - best, mode="full")
            A_ub = np.hstack([_jac(best), -np.ones((2 * n - 1, 1))])
            lo = np.maximum(-delta, -best)
            hi = np.minimum(delta, 1.0 - best)
            bounds = list(zip(lo, hi)) + [(None, None)]
            res = linprog(cost, A_ub=A_ub, b_ub=-c, A_eq=A_eq, b_eq=[0.0],
                          bounds=bounds, method="highs")
            if res.status != 0:
                delta *= 0.5
                continue
            cand = _fix_sum(np.clip(best + res.x[:n], 0.0, 1.0), target)
            val = _value(cand)
            if val < bestv - 1e-13 and abs(cand.sum() - target) < 1e-9:
                best, bestv = cand, val
            else:
                delta *= 0.5
        except Exception:
            break
    return best, bestv


def construct():
    t0 = time.time()
    n = 100
    best, bestv = _adam(n, n / 2.0, t0, 9)
    best = _fix_sum(best, n / 2.0)
    try:
        best, bestv = _slp(best, n / 2.0, t0, 22, 0.02)
        up = np.repeat(best, 2)
        up_t = len(up) / 2.0
        up = _fix_sum(up, up_t)
        up2, upv = _slp(up, up_t, t0, 50, 0.01)
        if upv <= bestv and abs(up2.sum() - up_t) < 1e-9:
            best, bestv = up2, upv
    except Exception:
        pass
    h = _fix_sum(best, len(best) / 2.0)
    return [float(a) for a in h]