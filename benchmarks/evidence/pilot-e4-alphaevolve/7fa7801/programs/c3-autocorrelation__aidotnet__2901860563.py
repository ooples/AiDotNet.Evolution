import time
import numpy as np


def _conv(a, b):
    L = len(a) + len(b) - 1
    N = 1 << (L - 1).bit_length()
    return np.fft.irfft(np.fft.rfft(a, N) * np.fft.rfft(b, N), N)[:L]


def _score(h):
    s = h.sum()
    if abs(s) < 1e-9:
        return 1e9
    c = _conv(h, h)
    return 2 * len(h) * np.max(np.abs(c)) / s ** 2


def _stage(h, t_end, p0, p1, lr0, lr1):
    n = len(h)
    h = h * (n / h.sum())
    best, best_v = h.copy(), _score(h)
    m1 = np.zeros(n)
    m2 = np.zeros(n)
    t = 0
    t_start = time.time()
    dur = max(t_end - t_start, 1e-3)
    while True:
        now = time.time()
        if now >= t_end:
            break
        frac = (now - t_start) / dur
        p = p0 * (p1 / p0) ** frac
        lr = lr0 * (1 - frac) + lr1 * frac
        h = h * (n / h.sum())
        c = _conv(h, h)
        a = np.abs(c)
        m = a.max()
        if m <= 0:
            break
        r = a / m
        S = np.sum(r ** p)
        F = m * S ** (1.0 / p)
        g = np.sign(c) * r ** (p - 1) / S ** ((p - 1) / p)
        s = h.sum()
        corr = _conv(g, h[::-1])[n - 1:2 * n - 1]
        grad = (2 * corr / s ** 2 - 2 * F / s ** 3) * n * n
        t += 1
        m1 = 0.9 * m1 + 0.1 * grad
        m2 = 0.999 * m2 + 0.001 * grad ** 2
        mh = m1 / (1 - 0.9 ** t)
        vh = m2 / (1 - 0.999 ** t)
        h = h - lr * mh / (np.sqrt(vh) + 1e-12)
        if t % 10 == 0:
            v = _score(h)
            if v < best_v:
                best_v, best = v, h.copy()
    v = _score(h)
    if v < best_v:
        best_v, best = v, h.copy()
    return best, best_v


def _up(b, n2):
    h = np.interp(np.linspace(0, 1, n2), np.linspace(0, 1, len(b)), b)
    return h * (n2 / h.sum())


def construct():
    t0 = time.time()
    n = 256
    rng = np.random.default_rng(0)
    x = np.linspace(-1, 1, n)
    h = 1.0 + 0.5 * (1 - x ** 2) + 0.2 * rng.random(n)
    best = np.ones(16)
    best_v = _score(best)
    try:
        b, v = _stage(h, t0 + 8, 8, 512, 0.01, 0.0005)
        if v < best_v:
            best, best_v = b, v
        plan = [(512, 15, 32, 1024, 0.004, 0.0003),
                (1024, 25, 64, 2048, 0.003, 0.0002),
                (2000, 38, 128, 4096, 0.002, 0.0001),
                (2000, 52, 512, 8192, 0.0007, 0.00002)]
        for n2, te, p0, p1, l0, l1 in plan:
            start = best if len(best) == n2 else _up(best, n2)
            b2, v2 = _stage(start, t0 + te, p0, p1, l0, l1)
            if v2 < best_v:
                best, best_v = b2, v2
    except Exception:
        pass
    if best_v >= 2.0 or not np.all(np.isfinite(best)):
        return [1.0] * 16
    return [float(a) for a in best]