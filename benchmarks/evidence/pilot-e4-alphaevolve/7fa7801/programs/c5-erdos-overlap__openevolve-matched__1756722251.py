import time
import numpy as np


def _project(h, target):
    lo, hi = -1.5, 1.5
    for _ in range(70):
        s = (lo + hi) / 2
        if np.clip(h + s, 0, 1).sum() > target:
            hi = s
        else:
            lo = s
    h = np.clip(h + (lo + hi) / 2, 0, 1)
    for _ in range(5):
        d = target - h.sum()
        if abs(d) < 1e-13:
            break
        room = (1 - h) if d > 0 else h
        t = room.sum()
        if t <= 0:
            break
        h = np.clip(h + d * room / t, 0, 1)
    return h


def _value(h):
    n = len(h)
    return 2 * np.correlate(h, 1 - h, mode="full").max() / n


def _run(n, seed, budget, init=None, temp0=0.02, temp1=0.0003,
         lr0=0.01, noise=0.02):
    target = n / 2
    rng = np.random.default_rng(seed)
    if init is None:
        h = _project(0.5 + 0.15 * rng.standard_normal(n), target)
    else:
        h = _project(init + noise * rng.standard_normal(n), target)
    best, best_v = h.copy(), _value(h)
    m = np.zeros(n)
    s = np.zeros(n)
    b1, b2 = 0.9, 0.999
    t0 = time.time()
    t = 0
    while True:
        el = time.time() - t0
        if el > budget:
            break
        frac = el / budget
        t += 1
        temp = temp0 * (temp1 / temp0) ** frac
        g = 1 - h
        C = np.correlate(h, g, mode="full") * (2.0 / n)
        w = np.exp((C - C.max()) / temp)
        w /= w.sum()
        g1 = np.correlate(w, g[::-1], mode="valid")
        g2 = np.correlate(w[::-1], h[::-1], mode="valid")
        grad = (g1 - g2) * (2.0 / n)
        m = b1 * m + (1 - b1) * grad
        s = b2 * s + (1 - b2) * grad * grad
        lr = lr0 * (1 - 0.95 * frac)
        h = h - lr * (m / (1 - b1 ** t)) / (np.sqrt(s / (1 - b2 ** t)) + 1e-12)
        h = _project(h, target)
        if t % 10 == 0:
            v = _value(h)
            if v < best_v:
                best, best_v = h.copy(), v
    v = _value(h)
    if v < best_v:
        best, best_v = h.copy(), v
    return best, best_v


def construct():
    n = 200
    target = n / 2
    best = np.full(n, 0.5)
    best_v = _value(best)
    for seed in range(2):
        h, v = _run(n, seed, 12.0, temp0=0.02, temp1=0.0003)
        if v < best_v:
            best, best_v = h, v
    for k in range(2):
        h, v = _run(n, 100 + k, 10.0, init=best, temp0=0.004,
                    temp1=0.00008, lr0=0.003, noise=0.01)
        if v < best_v:
            best, best_v = h, v
    best = _project(best, target)
    if _value(best) > 0.5 or abs(best.sum() - target) > 1e-9 * n:
        return [0.5] * n
    return [float(a) for a in best]