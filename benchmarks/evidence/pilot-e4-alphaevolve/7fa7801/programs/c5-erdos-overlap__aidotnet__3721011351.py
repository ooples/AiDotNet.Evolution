import time
import numpy as np


def _conv(a, b):
    L = len(a) + len(b) - 1
    N = 1 << (L - 1).bit_length()
    return np.fft.irfft(np.fft.rfft(a, N) * np.fft.rfft(b, N), N)[:L]


def _val(h):
    n = len(h)
    return 2 * np.max(_conv(h, (1 - h)[::-1])) / n


def _project(x, target, iters=60):
    lo, hi = -1.0 - np.max(x), 2.0 - np.min(x)
    for _ in range(iters):
        mid = 0.5 * (lo + hi)
        if np.clip(x + mid, 0, 1).sum() < target:
            lo = mid
        else:
            hi = mid
    return np.clip(x + 0.5 * (lo + hi), 0, 1)


def _grad(h, beta):
    n = len(h)
    g = 1 - h
    C = _conv(h, g[::-1]) / n
    w = np.exp(beta * (C - C.max()))
    w /= w.sum()
    t1 = _conv(w, g)[n - 1:2 * n - 1]
    t2 = _conv(w[::-1], h)[n - 1:2 * n - 1]
    return (t1 - t2) / n


def _adam(h0, T, lr0=0.02, beta0=50.0, beta1=3e4):
    t0 = time.time()
    n = len(h0)
    target = n / 2
    h = _project(h0, target)
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2 = 0.9, 0.999
    best, bv = h.copy(), _val(h)
    k = 0
    while True:
        frac = (time.time() - t0) / T
        if frac >= 1:
            break
        beta = beta0 * (beta1 / beta0) ** frac
        lr = lr0 * (0.01) ** frac
        gr = _grad(h, beta)
        k += 1
        m = b1 * m + (1 - b1) * gr
        v = b2 * v + (1 - b2) * gr * gr
        mh = m / (1 - b1 ** k)
        vh = v / (1 - b2 ** k)
        h = _project(h - lr * mh / (np.sqrt(vh) + 1e-12), target)
        if k % 5 == 0:
            cv = _val(h)
            if cv < bv:
                best, bv = h.copy(), cv
    cv = _val(h)
    if cv < bv:
        best, bv = h.copy(), cv
    return best, bv


def construct():
    default = [0.5] * 200
    best = None
    bv = 1e9
    try:
        n = 100
        rng = np.random.default_rng(7)
        x = (np.arange(n) + 0.5) / n
        starts = [
            0.5 + 0.2 * np.cos(2 * np.pi * (x - 0.5)),
            0.5 + 0.3 * (rng.random(n) - 0.5),
            0.5 + 0.3 * (rng.random(n) - 0.5),
        ]
        cur, cb = None, 1e9
        for s in starts:
            c, v = _adam(s, 5.0)
            if v < cb:
                cur, cb = c, v
        best, bv = cur.copy(), cb
        for T, lr0, b0, b1 in ((10.0, 0.008, 500.0, 3e4),
                               (12.0, 0.004, 1500.0, 6e4),
                               (10.0, 0.002, 3000.0, 1e5)):
            up = np.repeat(cur, 2)
            c, v = _adam(up, T, lr0=lr0, beta0=b0, beta1=b1)
            u = _val(up)
            if v > u:
                c, v = up, u
            cur = c
            if v < bv or len(c) != len(best):
                best, bv = c.copy(), v
    except Exception:
        pass
    if best is None:
        return default
    n = len(best)
    best = _project(best, n / 2, 200)
    if abs(best.sum() - n / 2) > 1e-9 * n or _val(best) > 0.5:
        return [0.5] * n
    return [float(v) for v in best]