import time
import numpy as np


def _project(h, target):
    lo, hi = -2.0, 2.0
    for _ in range(100):
        mid = 0.5 * (lo + hi)
        s = np.clip(h - mid, 0.0, 1.0).sum()
        if s > target:
            lo = mid
        else:
            hi = mid
    return np.clip(h - 0.5 * (lo + hi), 0.0, 1.0)


def _value(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1.0 - h, mode="full")) / n


def _optimize(h, limit, beta0, beta1, lr):
    n = len(h)
    target = n / 2.0
    t0 = time.time()
    h = _project(h, target)
    best_h, best_v = h.copy(), _value(h)
    m = np.zeros(n)
    v2 = np.zeros(n)
    it = 0
    while True:
        el = time.time() - t0
        if el >= limit:
            break
        it += 1
        frac = el / limit
        beta = beta0 * (beta1 / beta0) ** frac
        g = 1.0 - h
        C = np.correlate(h, g, mode="full") * (2.0 / n)
        w = np.exp(beta * (C - C.max()))
        w /= w.sum()
        A = np.correlate(w, g[::-1], mode="valid")
        B = np.correlate(w, h, mode="valid")[::-1]
        grad = A - B
        m = 0.9 * m + 0.1 * grad
        v2 = 0.999 * v2 + 0.001 * grad * grad
        mh = m / (1 - 0.9 ** it)
        vh = v2 / (1 - 0.999 ** it)
        step = lr * (1 - 0.9 * frac)
        h = _project(h - step * mh / (np.sqrt(vh) + 1e-12), target)
        val = _value(h)
        if val < best_v and abs(h.sum() - target) < 1e-9 * n:
            best_v = val
            best_h = h.copy()
    return best_h, best_v


def construct():
    rng = np.random.default_rng(0)
    n = 200
    h0 = 0.5 + 0.2 * rng.standard_normal(n)
    h1, v1 = _optimize(h0, 8.0, 50.0, 1000.0, 0.01)

    h2, v2 = _optimize(np.repeat(h1, 3), 24.0, 300.0, 3000.0, 0.003)

    best, bv = (h2, v2) if v2 <= v1 else (h1, v1)

    h3, v3 = _optimize(best, 10.0, 4000.0, 30000.0, 0.0005)
    if v3 < bv:
        best, bv = h3, v3

    h4, v4 = _optimize(best, 6.0, 20000.0, 100000.0, 0.0002)
    if v4 < bv:
        best, bv = h4, v4

    best = np.clip(best, 0.0, 1.0)
    if abs(best.sum() - len(best) / 2.0) > 1e-9 * len(best):
        best = np.full(len(best), 0.5)
    return [float(x) for x in best]