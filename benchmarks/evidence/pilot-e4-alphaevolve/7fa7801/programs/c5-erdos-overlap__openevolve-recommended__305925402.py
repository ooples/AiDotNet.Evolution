import time
import numpy as np


def _project(h, target):
    lo, hi = -1.0, 1.0
    for _ in range(60):
        t = 0.5 * (lo + hi)
        if np.clip(h + t, 0, 1).sum() > target:
            hi = t
        else:
            lo = t
    out = np.clip(h + 0.5 * (lo + hi), 0, 1)
    for _ in range(3):
        diff = target - out.sum()
        if abs(diff) < 1e-12:
            break
        free = (out > 1e-9) & (out < 1 - 1e-9)
        if not free.any():
            break
        out[free] += diff / free.sum()
        out = np.clip(out, 0, 1)
    return out


def _value(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _optimize(h, iters, beta0, beta1, lr0, lr1, deadline):
    n = len(h)
    target = n / 2.0
    best_h, best_v = h.copy(), _value(h)
    m = np.zeros(n)
    s = np.zeros(n)
    b1, b2 = 0.9, 0.999
    for it in range(1, iters + 1):
        if time.time() > deadline:
            break
        frac = it / iters
        beta = beta0 * (beta1 / beta0) ** frac
        lr = lr0 * (lr1 / lr0) ** frac
        r = (1 - h)[::-1]
        c = np.convolve(h, r)
        v = 2.0 * c / n
        w = np.exp(beta * (v - v.max()))
        w /= w.sum()
        A = np.correlate(w, r, "valid")
        B = np.correlate(w, h, "valid")
        g = A - B[::-1]
        m = b1 * m + (1 - b1) * g
        s = b2 * s + (1 - b2) * g * g
        mh = m / (1 - b1 ** it)
        sh = s / (1 - b2 ** it)
        h = _project(h - lr * mh / (np.sqrt(sh) + 1e-12), target)
        if it % 25 == 0 or it == iters:
            val = _value(h)
            if val < best_v:
                best_v, best_h = val, h.copy()
    val = _value(h)
    if val < best_v:
        best_v, best_h = val, h.copy()
    return best_h, best_v


def construct():
    t0 = time.time()
    n = 200
    rng = np.random.default_rng(0)
    h = _project(0.5 + 0.2 * rng.standard_normal(n), n / 2.0)

    best_h, best_v = _optimize(h, 6000, 100.0, 5000.0, 0.01, 0.0005, t0 + 14)

    stages = [(4000, 3000.0, 30000.0, 0.003, 0.0001, t0 + 27),
              (3000, 5000.0, 60000.0, 0.002, 0.00005, t0 + 40)]
    for iters, b0, b1, l0, l1, dl in stages:
        h2, v2 = _optimize(np.repeat(best_h, 2), iters, b0, b1, l0, l1, dl)
        if v2 < best_v:
            best_h, best_v = h2, v2
        else:
            break

    # repeated polish rounds with varied sharpness / step until time budget
    scales = [(60000.0, 150000.0, 1e-4, 1e-5),
              (100000.0, 250000.0, 4e-5, 5e-6),
              (80000.0, 200000.0, 6e-5, 5e-6),
              (150000.0, 400000.0, 2e-5, 3e-6)]
    k = 0
    end = t0 + 53
    while time.time() < end - 3:
        b0, b1, l0, l1 = scales[k % len(scales)]
        k += 1
        dl = min(end, time.time() + 7)
        h2, v2 = _optimize(best_h.copy(), 2000, b0, b1, l0, l1, dl)
        if v2 < best_v:
            best_h, best_v = h2, v2

    best_h = np.clip(best_h, 0, 1)
    best_h = _project(best_h, len(best_h) / 2.0)
    return [float(x) for x in best_h]