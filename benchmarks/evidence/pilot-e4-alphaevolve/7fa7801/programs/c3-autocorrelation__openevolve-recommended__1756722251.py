import time
import numpy as np


def _value(h):
    s = h.sum()
    if s <= 0:
        return float("inf")
    c = np.convolve(h, h)
    return 2 * len(h) * abs(c.max()) / (s * s)


def _run(x, dur, p0, p1, lr0, decay, squared, best):
    n = len(x)
    nf = 1
    while nf < 2 * n:
        nf *= 2
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2, eps = 0.9, 0.999, 1e-8
    ts = time.time()
    step = 0
    while True:
        el = time.time() - ts
        if el > dur:
            break
        step += 1
        frac = el / dur
        p = p0 * ((p1 / p0) ** frac)
        if squared:
            x = x * np.sqrt(n / np.sum(x * x))
            h = x * x
        else:
            s = x.sum()
            if s <= 0:
                x = np.ones(n)
                continue
            x = x * (n / s)
            h = x
        H = np.fft.rfft(h, nf)
        c = np.fft.irfft(H * H, nf)[: 2 * n - 1]
        cm = c.max()
        if cm <= 0:
            x = np.ones(n)
            continue
        r = np.clip(c / cm, 0, None)
        rp = r ** (p - 1)
        S = (rp * r).sum()
        w = rp / (S * cm)
        W = np.fft.rfft(w, nf)
        corr = np.fft.irfft(W * np.conj(H), nf)[:n]
        g = 2 * corr - 2.0 / n
        if squared:
            g = g * 2 * x
        m = b1 * m + (1 - b1) * g
        v = b2 * v + (1 - b2) * g * g
        mh = m / (1 - b1 ** step)
        vh = v / (1 - b2 ** step)
        x = x - lr0 * (1 - decay * frac) * mh / (np.sqrt(vh) + eps)
        if step % 10 == 0:
            hh = x * x if squared else x
            val = _value(hh)
            if val < best[0]:
                best[0] = val
                best[1] = hh.copy()
    hh = x * x if squared else x
    val = _value(hh)
    if val < best[0]:
        best[0] = val
        best[1] = hh.copy()
    return x


def construct():
    n = 600
    rng = np.random.default_rng(0)
    best = [float("inf"), np.ones(n)]
    x = np.ones(n) + 0.1 * rng.standard_normal(n)
    x = _run(x, 28.0, 16.0, 2048.0, 0.01, 0.97, True, best)
    h0 = best[1].copy()
    _run(h0, 12.0, 512.0, 4096.0, 0.003, 0.95, False, best)
    h1 = best[1].copy()
    _run(h1, 6.0, 2048.0, 20000.0, 0.001, 0.95, False, best)
    return [float(a) for a in best[1]]