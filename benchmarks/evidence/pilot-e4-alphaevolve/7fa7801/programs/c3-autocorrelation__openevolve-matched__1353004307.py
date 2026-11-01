import time
import numpy as np


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = np.convolve(h, h)
    return 2 * len(h) * np.abs(c).max() / s ** 2


def _stage(x, budget, p0, p1, lr0, lrmin):
    t0 = time.time()
    n = len(x)
    x = x / x.mean()
    best_h = x / x.sum()
    best_v = _score(best_h)
    m = np.zeros(n)
    v = np.zeros(n)
    N = 1
    while N < 2 * n:
        N *= 2
    b1, b2, eps = 0.9, 0.999, 1e-8
    t = 0
    frac = 0.0
    while True:
        t += 1
        if t % 5 == 1:
            frac = (time.time() - t0) / budget
        if frac >= 1.0:
            break
        p = p0 * (p1 / p0) ** frac
        lr = lr0 * (1 - frac) ** 1.5 + lrmin
        S = x.sum()
        if abs(S) < 1e-9:
            break
        h = x / S
        H = np.fft.rfft(h, N)
        c = np.fft.irfft(H * H, N)[: 2 * n - 1]
        a = np.abs(c)
        cm = a.max()
        if cm <= 0:
            break
        r = c / cm
        ar = np.abs(r)
        rp = np.sign(r) * ar ** (p - 1)
        Z = (ar ** p).sum()
        w = rp * Z ** (1.0 / p - 1.0)
        W = np.fft.rfft(w, N)
        g = 2 * np.fft.irfft(W * np.conj(H), N)[:n]
        g = g * 2 * n
        gx = (g - g @ h) / S
        m = b1 * m + (1 - b1) * gx
        v = b2 * v + (1 - b2) * gx * gx
        mh = m / (1 - b1 ** t)
        vh = v / (1 - b2 ** t)
        x = x - lr * mh / (np.sqrt(vh) + eps)
        x = x / x.mean()
        if t % 25 == 0:
            hv = x / x.sum()
            val = _score(hv)
            if val < best_v:
                best_v = val
                best_h = hv.copy()
    if abs(x.sum()) > 1e-9:
        hv = x / x.sum()
        val = _score(hv)
        if val < best_v:
            best_v, best_h = val, hv
    return best_h, best_v


def construct():
    rng = np.random.default_rng(0)
    x = 1.0 + 0.2 * rng.standard_normal(300)
    h1, v1 = _stage(x, 6.0, 6.0, 600.0, 0.01, 0.0003)
    cands = [(v1, h1)]
    h2, v2 = _stage(np.repeat(h1, 2), 12.0, 40.0, 1000.0, 0.004, 0.0002)
    cands.append((v2, h2))
    h3, v3 = _stage(np.repeat(h2, 2), 30.0, 80.0, 1500.0, 0.0013, 0.0001)
    cands.append((v3, h3))
    v, h = min(cands, key=lambda z: z[0])
    if not np.isfinite(v):
        return [1.0] * 16
    return [float(a) for a in h]