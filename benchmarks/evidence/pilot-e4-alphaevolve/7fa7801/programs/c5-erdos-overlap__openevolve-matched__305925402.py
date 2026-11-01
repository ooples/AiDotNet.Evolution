import time
import numpy as np


def _project(h, n):
    target = n / 2.0
    lo, hi = -1.0, 1.0
    for _ in range(60):
        mid = (lo + hi) / 2
        if np.clip(h + mid, 0, 1).sum() > target:
            hi = mid
        else:
            lo = mid
    out = np.clip(h + (lo + hi) / 2, 0, 1)
    r = target - out.sum()
    for _ in range(8):
        if abs(r) < 1e-13:
            break
        free = (out > 0) & (out < 1)
        k = free.sum()
        if k == 0:
            break
        out[free] += r / k
        out = np.clip(out, 0, 1)
        r = target - out.sum()
    return out


def _value(h, n):
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _optimize(h, n, limit, beta0, beta1, lr0, lr1):
    t0 = time.time()
    h = _project(h, n)
    best = h.copy()
    best_v = _value(h, n)
    m = np.zeros(n)
    v = np.zeros(n)
    it = 0
    while True:
        el = time.time() - t0
        if el >= limit:
            break
        frac = el / limit
        beta = beta0 * (beta1 / beta0) ** frac
        lr = lr0 + (lr1 - lr0) * frac
        g = 1 - h
        c = np.correlate(h, g, mode="full")
        w = np.exp(beta * (c - c.max()) / n)
        w /= w.sum()
        t1 = np.correlate(w, g[::-1], mode="valid")
        o = np.correlate(w, h, mode="valid")
        grad = t1 - o[::-1]
        it += 1
        m = 0.9 * m + 0.1 * grad
        v = 0.999 * v + 0.001 * grad * grad
        step = (m / (1 - 0.9 ** it)) / (np.sqrt(v / (1 - 0.999 ** it)) + 1e-12)
        h = _project(h - lr * step, n)
        val = _value(h, n)
        if val < best_v:
            best_v = val
            best = h.copy()
    return best, best_v


def construct():
    rng = np.random.default_rng(0)
    n = 100
    h = 0.5 + 0.05 * rng.standard_normal(n)
    h, _ = _optimize(h, n, 4.0, 50, 2000, 0.02, 0.0005)
    n = 200
    h = np.repeat(h, 2)
    h, _ = _optimize(h, n, 7.0, 300, 3000, 0.006, 0.0003)
    n = 400
    h = np.repeat(h, 2)
    h, bv = _optimize(h, n, 13.0, 500, 5000, 0.004, 0.0002)
    stages = [
        (8.0, 3000, 20000, 0.0008, 0.00005),
        (8.0, 6000, 40000, 0.0004, 0.00003),
        (8.0, 10000, 80000, 0.0002, 0.00001),
    ]
    for lim, b0, b1, l0, l1 in stages:
        h2, v2 = _optimize(h, n, lim, b0, b1, l0, l1)
        if v2 < bv:
            h, bv = h2, v2
    best = _project(h, n)
    if abs(best.sum() - n / 2) > 1e-9 * n or best.min() < 0 or best.max() > 1:
        return [0.5] * 16
    return [float(a) for a in best]