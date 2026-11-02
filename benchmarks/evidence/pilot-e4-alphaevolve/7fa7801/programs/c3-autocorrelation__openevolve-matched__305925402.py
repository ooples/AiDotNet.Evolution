"""Minimize 2*n*max|conv(h,h)|/sum(h)^2 via multi-resolution p-norm Adam descent (FFT-accelerated)."""
import time
import numpy as np


def _fconv(a, b):
    size = len(a) + len(b) - 1
    if size < 400:
        return np.convolve(a, b)
    nf = 1 << (size - 1).bit_length()
    return np.fft.irfft(np.fft.rfft(a, nf) * np.fft.rfft(b, nf), nf)[:size]


def _value(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = _fconv(h, h)
    return 2 * len(h) * np.abs(c).max() / s ** 2


def _stage(h, steps, lr, p0, p1, deadline):
    n = len(h)
    h = h * (n / h.sum())
    best, best_v = h.copy(), _value(h)
    m1 = np.zeros(n)
    m2 = np.zeros(n)
    b1, b2 = 0.9, 0.999
    step = 0
    while step < steps and time.time() < deadline:
        step += 1
        frac = step / steps
        p = p0 + (p1 - p0) * frac
        S = h.sum()
        c = _fconv(h, h)
        mx = np.abs(c).max()
        if mx == 0 or abs(S) < 1e-9:
            break
        a = np.abs(c) / mx
        Lp = mx * (a ** p).sum() ** (1.0 / p)
        g = np.sign(c) * (np.abs(c) / Lp) ** (p - 1)
        corr = _fconv(g, h[::-1])[n - 1:2 * n - 1]
        grad = (2 * corr / S ** 2 - 2 * Lp / S ** 3) * n
        m1 = b1 * m1 + (1 - b1) * grad
        m2 = b2 * m2 + (1 - b2) * grad * grad
        mh = m1 / (1 - b1 ** step)
        vh = m2 / (1 - b2 ** step)
        cur_lr = lr * (1 - 0.95 * frac)
        h = h - cur_lr * mh / (np.sqrt(vh) + 1e-12)
        s = h.sum()
        if abs(s) < 1e-9:
            break
        h = h * (n / s)
        if step % 10 == 0:
            v = _value(h)
            if v < best_v:
                best_v, best = v, h.copy()
    v = _value(h)
    if v < best_v:
        best_v, best = v, h.copy()
    return best, best_v


def _resample(h, n):
    if n == len(h):
        return h.copy()
    k = n / len(h)
    idx = np.minimum((np.arange(n) / k).astype(int), len(h) - 1)
    return h[idx]


def construct():
    t0 = time.time()
    total = 50.0
    rng = np.random.default_rng(0)
    sizes = [60, 120, 240, 480, 900, 1600, 2400, 2400]
    shares = [0.05, 0.08, 0.11, 0.15, 0.17, 0.17, 0.15, 0.12]
    h = np.ones(sizes[0]) + 0.3 * rng.standard_normal(sizes[0])
    best, best_v = np.ones(sizes[0]), _value(np.ones(sizes[0]))
    acc = 0.0
    for i, (n, sh) in enumerate(zip(sizes, shares)):
        acc += sh
        deadline = t0 + total * acc
        if i > 0:
            h = _resample(h, n)
        if i == 0:
            lr, p0, p1 = 0.02, 10, 300
        elif i == len(sizes) - 1:
            lr, p0, p1 = 0.0012, 300, 1500
        else:
            lr, p0, p1 = 0.006, 30, 300
        h, v = _stage(h, 12000, lr, p0, p1, deadline)
        if v < best_v:
            best_v, best = v, h.copy()
    return [float(x) for x in best]