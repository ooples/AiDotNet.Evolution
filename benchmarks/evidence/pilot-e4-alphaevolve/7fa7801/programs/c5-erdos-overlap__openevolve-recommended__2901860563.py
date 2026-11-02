import time
import numpy as np


def _value(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _project(x, target):
    lo, hi = -1.0 - x.max(), 1.0 - x.min()
    for _ in range(100):
        mid = 0.5 * (lo + hi)
        if np.clip(x + mid, 0, 1).sum() < target:
            lo = mid
        else:
            hi = mid
    return np.clip(x + 0.5 * (lo + hi), 0, 1)


def _run(h, beta0, beta1, lr0, steps, t_end, best, bestv):
    n = len(h)
    target = n / 2
    m = np.zeros(n)
    s = np.zeros(n)
    for it in range(1, steps + 1):
        if (it & 31) == 0 and time.time() > t_end:
            break
        frac = it / steps
        beta = beta0 * (beta1 / beta0) ** frac
        lr = lr0 * (1 - frac) + 0.0002
        b = 1 - h
        C = np.correlate(h, b, mode="full") * (2.0 / n)
        w = np.exp(beta * (C - C.max()))
        w /= w.sum()
        g = np.convolve(w, b)[n - 1:2 * n - 1] - np.correlate(h, w, mode="full")[n - 1:2 * n - 1]
        g *= 2.0 / n
        m = 0.9 * m + 0.1 * g
        s = 0.999 * s + 0.001 * g * g
        mh = m / (1 - 0.9 ** it)
        sh = s / (1 - 0.999 ** it)
        h = _project(h - lr * mh / (np.sqrt(sh) + 1e-12), target)
        if (it & 15) == 0:
            v = _value(h)
            if v < bestv and abs(h.sum() - target) <= 1e-10 * n:
                best, bestv = h.copy(), v
    return h, best, bestv


def construct():
    fallback = [0.5] * 16
    t0 = time.time()
    rng = np.random.default_rng(0)
    n = 100
    target = n / 2
    best, bestv = None, 0.5

    restart = 0
    while time.time() - t0 < 18:
        restart += 1
        amp = [0.3, 0.8, 0.1][restart % 3]
        h = _project(0.5 + amp * (rng.random(n) - 0.5), target)
        t_end = min(time.time() + 6, t0 + 18)
        h, best, bestv = _run(h, 30.0, 3000.0, 0.02, 6000, t_end, best, bestv)

    # staged refinement at higher resolution
    if best is not None:
        for t_stop, lr0 in ((34, 0.003), (50, 0.0015)):
            h2 = np.repeat(best, 2)
            h2 = _project(h2, len(h2) / 2)
            best2, bestv2 = None, bestv
            h2, best2, bestv2 = _run(h2, 500.0, 6000.0, lr0, 8000, t0 + t_stop, best2, bestv2)
            if best2 is not None and bestv2 < bestv:
                best, bestv = best2, bestv2
            else:
                break

    if best is not None and bestv < 0.5:
        return [float(x) for x in best]
    return fallback