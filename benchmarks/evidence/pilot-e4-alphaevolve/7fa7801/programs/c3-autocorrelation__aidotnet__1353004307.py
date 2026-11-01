import time
import numpy as np


def _fft_len(m):
    L = 1
    while L < m:
        L *= 2
    return L


def _conv(a, b):
    n = len(a)
    if n >= 256:
        L = _fft_len(2 * n)
        r = np.fft.irfft(np.fft.rfft(a, L) * np.fft.rfft(b, L), L)
        return r[: 2 * n - 1]
    return np.convolve(a, b)


def _corr(g, h, n):
    if n < 256:
        return np.correlate(g, h, mode="valid")
    L = _fft_len(len(g) + n)
    r = np.fft.irfft(np.fft.rfft(g, L) * np.conj(np.fft.rfft(h, L)), L)
    return r[:n]


def _value(h):
    s = h.sum()
    if abs(s) < 1e-9:
        return float("inf")
    c = _conv(h, h)
    return 2 * len(h) * np.max(np.abs(c)) / s ** 2


def _run(h, t_end, p0, p1, lr0, lr1, best):
    n = len(h)
    t_start = time.time()
    dur = max(t_end - t_start, 1e-6)
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2 = 0.9, 0.999
    step = 0
    h = h * (n / h.sum())
    while True:
        el = time.time() - t_start
        if el > dur:
            break
        frac = el / dur
        p = p0 + (p1 - p0) * frac
        lr = lr0 + (lr1 - lr0) * frac
        s = h.sum()
        c = _conv(h, h)
        ac = np.abs(c)
        mx = ac.max()
        if mx == 0:
            break
        S = ((ac / mx) ** p).sum()
        norm = mx * S ** (1.0 / p)
        g = np.sign(c) * (ac / norm) ** (p - 1)
        gh = 2 * _corr(g, h, n)
        L = norm / s ** 2
        grad = (gh / s ** 2 - 2 * L / s) * n
        step += 1
        m = b1 * m + (1 - b1) * grad
        v = b2 * v + (1 - b2) * grad ** 2
        mh = m / (1 - b1 ** step)
        vh = v / (1 - b2 ** step)
        h = h - lr * mh / (np.sqrt(vh) + 1e-12)
        h *= n / h.sum()
        if step % 20 == 0:
            val = _value(h)
            if val < best[0]:
                best[0] = val
                best[1] = h.copy()
    val = _value(h)
    if val < best[0]:
        best[0] = val
        best[1] = h.copy()
    return h


def construct():
    t0 = time.time()
    total = 51.0
    rng = np.random.default_rng(0)
    n = 50
    h = np.ones(n) + 0.3 * rng.standard_normal(n)
    h *= n / h.sum()
    best = [_value(np.ones(n)), np.ones(n)]
    stages = [
        (0.06, 10.0, 150.0, 0.02, 0.003),
        (0.14, 30.0, 250.0, 0.008, 0.001),
        (0.25, 60.0, 350.0, 0.004, 0.0006),
        (0.40, 120.0, 600.0, 0.002, 0.0003),
        (0.58, 200.0, 1000.0, 0.001, 0.00015),
        (0.78, 350.0, 1500.0, 0.0006, 0.0001),
        (1.00, 500.0, 2500.0, 0.0003, 0.00004),
    ]
    for i, (fr, p0, p1, l0, l1) in enumerate(stages):
        if i > 0:
            h = np.repeat(best[1], 2)
            h = h * (len(h) / h.sum())
            best = [_value(h), h.copy()]
        h = _run(h, t0 + total * fr, p0, p1, l0, l1, best)
        h = best[1].copy()
    return [float(x) for x in best[1]]