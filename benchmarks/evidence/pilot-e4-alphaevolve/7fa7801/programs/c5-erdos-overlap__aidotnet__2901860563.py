import time
import numpy as np


def _project(x, target):
    lo, hi = x.min() - 1.0, x.max()
    for _ in range(60):
        mid = 0.5 * (lo + hi)
        if np.clip(x - mid, 0, 1).sum() > target:
            lo = mid
        else:
            hi = mid
    h = np.clip(x - 0.5 * (lo + hi), 0, 1)
    for _ in range(5):
        diff = target - h.sum()
        if abs(diff) < 1e-13:
            break
        free = (h > 1e-12) & (h < 1 - 1e-12)
        if not free.any():
            break
        h[free] += diff / free.sum()
        h = np.clip(h, 0, 1)
    return h


def _value(h):
    n = len(h)
    return 2.0 * np.correlate(h, 1 - h, mode="full").max() / n


def _adam(h, t_end, lr, beta0, beta1):
    n = len(h)
    target = n / 2.0
    h = _project(h, target)
    best_h, best_v = h.copy(), _value(h)
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2 = 0.9, 0.999
    step = 0
    start = time.time()
    span = max(t_end - start, 1e-3)
    while True:
        el = time.time() - start
        if el >= span:
            break
        step += 1
        frac = el / span
        beta = beta0 * (beta1 / beta0) ** frac
        g = 1 - h
        c = np.correlate(h, g, mode="full") * 2.0 / n
        w = np.exp(beta * (c - c.max()))
        w /= w.sum()
        grad_h = np.convolve(w, g)[n - 1:2 * n - 1]
        grad_g = np.correlate(w, h, mode="valid")[::-1]
        grad = (grad_h - grad_g) * 2.0 / n
        m = b1 * m + (1 - b1) * grad
        v = b2 * v + (1 - b2) * grad * grad
        mh = m / (1 - b1 ** step)
        vh = v / (1 - b2 ** step)
        cur_lr = lr * (1 - 0.97 * frac)
        h = _project(h - cur_lr * mh / (np.sqrt(vh) + 1e-12), target)
        val = _value(h)
        if val < best_v:
            best_v, best_h = val, h.copy()
    return best_h, best_v


def _ok(h):
    return abs(h.sum() - len(h) / 2.0) < 1e-9 * len(h)


def construct():
    t0 = time.time()
    n = 200
    rng = np.random.default_rng(0)
    x = 0.5 + 0.1 * rng.standard_normal(n)
    h = _project(x, n / 2.0)

    best_h, best_v = _adam(h, t0 + 8.0, 0.008, 100.0, 5000.0)

    try:
        h2 = np.repeat(best_h, 3)
        h2, v2 = _adam(h2, t0 + 28.0, 0.003, 1500.0, 20000.0)
        if v2 < best_v and _ok(h2):
            best_h, best_v = h2, v2
        h3, v3 = _adam(best_h, t0 + 54.0, 0.0012, 8000.0, 100000.0)
        if v3 < best_v and _ok(h3):
            best_h, best_v = h3, v3
    except Exception:
        pass

    if not _ok(best_h):
        best_h = np.full(len(best_h), 0.5)
    return [float(a) for a in best_h]