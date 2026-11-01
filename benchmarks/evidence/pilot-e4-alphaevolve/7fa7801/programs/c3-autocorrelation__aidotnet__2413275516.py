import time
import numpy as np


def _true_val(h):
    s = h.sum()
    if abs(s) < 1e-9:
        return float("inf")
    n = len(h)
    L = 1 << (2 * n - 1).bit_length()
    f = np.fft.rfft(h, L)
    c = np.fft.irfft(f * f, L)[: 2 * n - 1]
    return 2 * n * np.abs(c).max() / s ** 2


def _stage(h, dur, p0, p1, lr0, best, deadline):
    n = len(h)
    L = 1 << (2 * n - 1).bit_length()
    m = np.zeros(n)
    v = np.zeros(n)
    t = 0
    ts = time.time()
    h = h / h.sum() * n
    while True:
        now = time.time()
        el = now - ts
        if el >= dur or now > deadline:
            break
        frac = el / dur
        p = p0 * (p1 / p0) ** frac
        lr = lr0 * (1.0 - 0.95 * frac)
        t += 1
        s = h.sum()
        if abs(s) < 1e-9:
            break
        u = h / s * n
        fu = np.fft.rfft(u, L)
        c = np.fft.irfft(fu * fu, L)[: 2 * n - 1]
        a = np.abs(c)
        mx = a.max()
        if mx <= 0:
            break
        r = a / mx
        S = np.sum(r ** p)
        g = (S ** (1.0 / p - 1.0)) * r ** (p - 1) * np.sign(c)
        fg = np.fft.rfft(g, L)
        fur = np.fft.rfft(u[::-1], L)
        corr = np.fft.irfft(fg * fur, L)[n - 1: 2 * n - 1]
        gu = 2 * corr
        gh = (n / s) * (gu - np.dot(gu, u) / n)
        m = 0.9 * m + 0.1 * gh
        v = 0.999 * v + 0.001 * gh * gh
        mh = m / (1 - 0.9 ** t)
        vh = v / (1 - 0.999 ** t)
        h = h - lr * mh / (np.sqrt(vh) + 1e-12)
        if t % 20 == 0:
            val = _true_val(h)
            if val < best[0]:
                best[0] = val
                best[1] = h / h.sum() * n
    val = _true_val(h)
    if val < best[0]:
        best[0] = val
        best[1] = h / h.sum() * n
    return h / h.sum() * n


def construct():
    t0 = time.time()
    deadline = t0 + 53.0
    rng = np.random.default_rng(1)
    n = 250
    h = 1.0 + 0.1 * rng.standard_normal(n)
    best = [_true_val(np.ones(n)), np.ones(n)]
    plan = [(8.0, 8, 512, 0.02),
            (9.0, 32, 1000, 0.008),
            (10.0, 64, 1500, 0.005),
            (12.0, 128, 3000, 0.0025),
            (13.0, 256, 5000, 0.0015)]
    for i, (dur, p0, p1, lr) in enumerate(plan):
        if time.time() > deadline:
            break
        if i > 0:
            h = np.repeat(best[1], 2)
            best = [_true_val(h), h.copy()]
        h = _stage(h, dur, p0, p1, lr, best, deadline)
    out = best[1]
    out = out / out.sum() * len(out)
    return [float(z) for z in out]