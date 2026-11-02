import time
import numpy as np


def _obj(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1.0 - h, mode="full")) / n


def _proj(h):
    n = len(h)
    target = n / 2.0
    lo, hi = -1.0 - h.max(), 1.0 - h.min() + 1.0
    lo = float(h.min() - 1.0)
    hi = float(h.max())
    for _ in range(80):
        mid = 0.5 * (lo + hi)
        if np.clip(h - mid, 0.0, 1.0).sum() > target:
            lo = mid
        else:
            hi = mid
    out = np.clip(h - 0.5 * (lo + hi), 0.0, 1.0)
    # exact fix of residual
    r = target - out.sum()
    if abs(r) > 1e-12:
        free = (out < 1.0) if r > 0 else (out > 0.0)
        if free.any():
            out[free] += r / free.sum()
            out = np.clip(out, 0.0, 1.0)
    return out


def construct():
    t0 = time.time()
    n = 200
    best = np.full(n, 0.5)
    best_v = _obj(best)
    rng = np.random.default_rng(3)
    restart = 0
    while time.time() - t0 < 35:
        h = _proj(0.5 + 0.2 * rng.standard_normal(n))
        m = np.zeros(n)
        v = np.zeros(n)
        iters = 6000
        lr0 = 0.02
        for it in range(1, iters + 1):
            if time.time() - t0 > 40:
                break
            frac = it / iters
            beta = 50.0 * (1.0 + 400.0 * frac)  # sharpen over time
            g = 1.0 - h
            c = np.correlate(h, g, mode="full")
            z = beta * (c - c.max()) / n
            w = np.exp(z)
            w /= w.sum()
            gh = np.convolve(w, g)[n - 1:2 * n - 1]
            gg = np.correlate(w, h, mode="valid")[::-1]
            grad = gh - gg
            m = 0.9 * m + 0.1 * grad
            v = 0.999 * v + 0.001 * grad * grad
            mh = m / (1 - 0.9 ** it)
            vh = v / (1 - 0.999 ** it)
            lr = lr0 * (1.0 - frac) + 1e-4
            h = _proj(h - lr * mh / (np.sqrt(vh) + 1e-12))
            if it % 50 == 0:
                val = _obj(h)
                if val < best_v:
                    best_v = val
                    best = h.copy()
        val = _obj(h)
        if val < best_v:
            best_v = val
            best = h.copy()
        restart += 1
        if restart >= 3:
            break
    return [float(x) for x in best]