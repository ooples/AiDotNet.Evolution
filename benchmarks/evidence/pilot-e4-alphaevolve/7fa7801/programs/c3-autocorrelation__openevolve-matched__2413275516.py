import time
import numpy as np


def _value(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = np.convolve(h, h)
    return 2 * len(h) * np.max(np.abs(c)) / (s * s)


def _stage(h, dur, p_lo, p_hi, lr_hi, lr_lo):
    n = len(h)
    m1 = np.zeros(n)
    m2 = np.zeros(n)
    b1, b2 = 0.9, 0.999
    step = 0
    best_h = h.copy()
    best_v = _value(h)
    ts = time.time()
    while True:
        t = (time.time() - ts) / dur
        if t >= 1.0:
            break
        p = p_lo * (p_hi / p_lo) ** t
        lr = lr_hi * (lr_lo / lr_hi) ** t
        for _ in range(10):
            s = h.sum()
            if abs(s) < 1e-9:
                h = h + 0.01
                s = h.sum()
            c = np.convolve(h, h)
            a = np.abs(c)
            m = a.max()
            if m <= 0:
                break
            r = a / m
            rp1 = r ** (p - 1)
            S = (rp1 * r).sum()
            g_c = np.sign(c) * rp1 / (m * S)
            g = 2 * np.correlate(g_c, h, mode="valid") - 2.0 / s
            step += 1
            m1 = b1 * m1 + (1 - b1) * g
            m2 = b2 * m2 + (1 - b2) * g * g
            mh = m1 / (1 - b1 ** step)
            vh = m2 / (1 - b2 ** step)
            h = h - lr * mh / (np.sqrt(vh) + 1e-12)
        v = _value(h)
        if v < best_v:
            best_v = v
            best_h = h.copy()
    v = _value(h)
    if v < best_v:
        best_v = v
        best_h = h.copy()
    return best_h, best_v


def construct():
    t0 = time.time()
    rng = np.random.default_rng(0)
    h = 1.0 + 0.1 * rng.standard_normal(50)
    stages = [
        (50, 3.0, 8.0, 256.0, 0.03, 2e-3),
        (100, 5.0, 8.0, 512.0, 0.02, 1e-3),
        (200, 7.0, 64.0, 1024.0, 0.01, 3e-4),
        (400, 10.0, 128.0, 1024.0, 0.005, 1e-4),
        (800, 22.0, 256.0, 2048.0, 0.003, 4e-5),
    ]
    best_h = np.ones(50)
    best_v = _value(best_h)
    for n, dur, plo, phi, lhi, llo in stages:
        remaining = 54 - (time.time() - t0)
        if remaining < 2:
            break
        dur = min(dur, remaining - 1)
        if len(h) != n:
            h = np.repeat(h, n // len(h))
        h = h / h.mean()
        h, v = _stage(h, dur, plo, phi, lhi, llo)
        if v < best_v or len(best_h) != n:
            best_h, best_v = h.copy(), v
    return [float(x) for x in best_h]