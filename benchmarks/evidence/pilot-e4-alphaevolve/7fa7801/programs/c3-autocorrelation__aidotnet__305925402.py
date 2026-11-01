import time
import numpy as np


def _fftconv(a, b):
    m = len(a) + len(b) - 1
    s = 1 << (m - 1).bit_length()
    return np.fft.irfft(np.fft.rfft(a, s) * np.fft.rfft(b, s), s)[:m]


def _value(h):
    s = h.sum()
    if abs(s) < 1e-9:
        return float("inf")
    c = _fftconv(h, h)
    return 2 * len(h) * np.max(np.abs(c)) / s ** 2


def _stage(h, dur, beta0, beta1, lr0, lr1):
    n = len(h)
    t0 = time.time()
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2 = 0.9, 0.999
    best_v, best_h = _value(h), h.copy()
    it = 0
    while True:
        frac = (time.time() - t0) / dur
        if frac >= 1:
            break
        it += 1
        beta = beta0 * (beta1 / beta0) ** frac
        lr = lr0 + (lr1 - lr0) * frac
        S = h.sum()
        if S <= 0:
            h = -h
            S = -S
        h = h * (n / S)
        S = float(n)
        u = _fftconv(h, h) / n
        z = beta * u
        zmax = z.max()
        e = np.exp(z - zmax)
        Z = e.sum()
        w = e / Z
        Msm = (zmax + np.log(Z)) / beta
        corr = _fftconv(w, h[::-1])[n - 1:2 * n - 1]
        g = (2.0 / n) * corr / Msm - 2.0 / S
        m = b1 * m + (1 - b1) * g
        v = b2 * v + (1 - b2) * g * g
        mh = m / (1 - b1 ** it)
        vh = v / (1 - b2 ** it)
        h = h - lr * mh / (np.sqrt(vh) + 1e-8)
        if it % 20 == 0:
            val = _value(h)
            if val < best_v:
                best_v, best_h = val, h.copy()
    val = _value(h)
    if val < best_v:
        best_v, best_h = val, h.copy()
    return best_v, best_h


def construct():
    rng = np.random.default_rng(0)
    n = 128
    h = 1.0 + 0.1 * rng.standard_normal(n)
    gv, gh = _stage(h, 7.0, 10.0, 1000.0, 0.02, 0.0005)
    stages = [(6.0, 256), (9.0, 512), (12.0, 1024)]
    for dur, nn in stages:
        h = np.repeat(gh, nn // len(gh))
        h = h * (len(h) / h.sum())
        h = h + 0.003 * rng.standard_normal(len(h))
        v, hb = _stage(h, dur, 150.0, 3000.0, 0.003, 0.0001)
        if v < gv:
            gv, gh = v, hb
        else:
            gh = np.repeat(gh, nn // len(gh))
    # final polish at full resolution
    h = gh * (len(gh) / gh.sum())
    v, hb = _stage(h, 14.0, 1500.0, 15000.0, 0.0006, 0.00001)
    if v < gv:
        gv, gh = v, hb
    return [float(x) for x in gh]