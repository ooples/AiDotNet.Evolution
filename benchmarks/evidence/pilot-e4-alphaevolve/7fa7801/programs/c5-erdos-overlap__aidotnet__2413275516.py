import time
import numpy as np


def _project(x, target):
    lo, hi = x.min() - 1.0, x.max()
    for _ in range(60):
        mid = (lo + hi) / 2
        if np.clip(x - mid, 0, 1).sum() > target:
            lo = mid
        else:
            hi = mid
    h = np.clip(x - (lo + hi) / 2, 0, 1)
    for _ in range(5):
        r = target - h.sum()
        if abs(r) < 1e-12:
            break
        free = (h > 0) & (h < 1)
        if not free.any():
            break
        h[free] += r / free.sum()
        h = np.clip(h, 0, 1)
    return h


def _score(h):
    n = len(h)
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _stage(h, duration, beta0, beta1, lr0, lr1):
    N = len(h)
    target = N / 2
    t0 = time.time()
    best = h.copy()
    best_s = _score(h)
    m = np.zeros(N)
    v = np.zeros(N)
    t = 0
    while True:
        frac = (time.time() - t0) / duration
        if frac >= 1:
            break
        beta = beta0 * (beta1 / beta0) ** frac
        lr = lr0 + (lr1 - lr0) * frac
        t += 1
        g = 1 - h
        C = np.correlate(h, g, mode="full") * 2 / N
        w = np.exp(beta * (C - C.max()))
        w /= w.sum()
        t1 = np.convolve(w, g)[N - 1:2 * N - 1]
        t2 = np.convolve(w[::-1], h)[N - 1:2 * N - 1]
        grad = (t1 - t2) * 2
        m = 0.9 * m + 0.1 * grad
        v = 0.999 * v + 0.001 * grad * grad
        mh = m / (1 - 0.9 ** t)
        vh = v / (1 - 0.999 ** t)
        h = _project(h - lr * mh / (np.sqrt(vh) + 1e-12), target)
        if t % 10 == 0:
            s = _score(h)
            if s < best_s:
                best_s, best = s, h.copy()
    s = _score(h)
    if s < best_s:
        best_s, best = s, h.copy()
    return best, best_s


def construct():
    rng = np.random.default_rng(0)
    N = 100
    h = _project(0.5 + 0.1 * rng.standard_normal(N), N / 2)
    best, best_s = _stage(h, 3.5, 50, 4000, 0.01, 0.0005)
    plan = [(200, 5.5, 500, 6000, 0.004, 0.0003),
            (400, 18.0, 1000, 8000, 0.002, 0.0002)]
    for n2, dur, b0, b1, l0, l1 in plan:
        start = _project(np.repeat(best, 2).astype(float), n2 / 2)
        best, best_s = _stage(start, dur, b0, b1, l0, l1)
    h, s = _stage(best.copy(), 24.0, 6000, 150000, 0.0003, 0.000002)
    if s < best_s:
        best, best_s = h, s
    if best_s >= 0.5:
        return [0.5] * 16
    return [float(x) for x in best]