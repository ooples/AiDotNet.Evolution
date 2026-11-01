import time
import numpy as np


def _project(x, target):
    lo, hi = x.min() - 1.0, x.max() + 1.0
    for _ in range(100):
        mu = 0.5 * (lo + hi)
        if np.clip(x - mu, 0.0, 1.0).sum() > target:
            lo = mu
        else:
            hi = mu
    return np.clip(x - 0.5 * (lo + hi), 0.0, 1.0)


def _value(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1.0 - h, mode="full")) / n


def construct():
    t0 = time.time()
    budget = 44.0
    n = 200
    target = n / 2.0
    rng = np.random.default_rng(0)
    best = np.full(n, 0.5)
    best_v = _value(best)
    M = 1
    while M < 2 * n:
        M *= 2

    def fconv(a, b):
        return np.fft.irfft(np.fft.rfft(a, M) * np.fft.rfft(b, M), M)[:2 * n - 1]

    try:
        h = _project(0.5 + 0.2 * rng.standard_normal(n), target)
        m = np.zeros(n)
        v = np.zeros(n)
        t = 0
        frac = 0.0
        while True:
            t += 1
            if t % 10 == 1:
                frac = (time.time() - t0) / budget
                if frac >= 1.0:
                    break
            beta = 100.0 * (1000.0 ** frac)
            lr = 0.015 * (1 - frac) ** 1.8 + 0.0001
            g = 1.0 - h
            s = 2.0 * fconv(h, g[::-1]) / n
            smax = s.max()
            if smax < best_v and abs(h.sum() - target) < 1e-10 * n:
                best_v = smax
                best = h.copy()
            w = np.exp(beta * (s - smax))
            w /= w.sum()
            t1 = fconv(w, g)[n - 1:2 * n - 1]
            t2 = fconv(w[::-1], h)[n - 1:2 * n - 1]
            grad = (t1 - t2) * (2.0 / n)
            m = 0.9 * m + 0.1 * grad
            v = 0.999 * v + 0.001 * grad * grad
            mh = m / (1 - 0.9 ** t)
            vh = v / (1 - 0.999 ** t)
            h = _project(h - lr * mh / (np.sqrt(vh) + 1e-12), target)
        val = _value(h)
        if val < best_v:
            best_v = val
            best = h.copy()
        best = _project(best, target)
        best_v = _value(best)
        if abs(best.sum() - target) > 1e-9 * n or best_v >= 0.5:
            best = np.full(n, 0.5)
    except Exception:
        best = np.full(n, 0.5)
    return [float(a) for a in best]