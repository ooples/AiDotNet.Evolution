import time
import numpy as np

_T0 = time.time()


def _value(h):
    s = h.sum()
    if abs(s) < 1e-9:
        return float("inf")
    c = np.convolve(h, h)
    return 2 * len(h) * np.max(np.abs(c)) / s ** 2


def _opt(h, budget, p0, p1, lr0, lr1):
    n = len(h)
    N = 1
    while N < 2 * n:
        N *= 2
    t0 = time.time()
    h = h * (n / h.sum())
    best = h.copy()
    bestv = _value(h)
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2 = 0.9, 0.999
    t = 0
    while True:
        el = time.time() - t0
        if el > budget:
            break
        frac = el / budget
        p = p0 * ((p1 / p0) ** frac)
        lr = lr0 * ((lr1 / lr0) ** frac)
        H = np.fft.rfft(h, N)
        c = np.fft.irfft(H * H, N)[:2 * n - 1]
        ac = np.abs(c)
        mx = ac.max()
        if mx == 0:
            break
        w = (ac / mx) ** p
        f = mx * w.sum() ** (1.0 / p)
        r = ac / f
        d = np.sign(c) * r ** (p - 1)
        gf = 2 * np.fft.irfft(np.fft.rfft(d, N) * np.conj(H), N)[:n]
        s = h.sum()
        g = (gf / s ** 2 - 2 * f / s ** 3) * n
        t += 1
        m = b1 * m + (1 - b1) * g
        v = b2 * v + (1 - b2) * g * g
        mh = m / (1 - b1 ** t)
        vh = v / (1 - b2 ** t)
        h = h - lr * mh / (np.sqrt(vh) + 1e-12)
        s = h.sum()
        if s <= 0:
            h = best.copy()
            m[:] = 0
            v[:] = 0
            continue
        h *= n / s
        if t % 20 == 0:
            val = _value(h)
            if val < bestv:
                bestv = val
                best = h.copy()
    val = _value(h)
    if val < bestv:
        bestv = val
        best = h.copy()
    return best, bestv


def construct():
    n = 300
    rng = np.random.default_rng(0)
    x = (np.arange(n) + 0.5) / n
    h = 1.0 + 0.1 * rng.standard_normal(n) + 0.5 * (x - 0.5) ** 2

    end = _T0 + 51.0
    stages = [
        (6.0, 8, 700, 0.02, 0.0003),
        (8.0, 128, 1200, 0.004, 0.00012),
        (12.0, 128, 1800, 0.003, 0.00006),
        (1e9, 256, 5000, 0.002, 0.00001),
    ]
    overall, overallv = None, float("inf")
    cur = h
    for i, (bud, p0, p1, lr0, lr1) in enumerate(stages):
        left = end - time.time()
        if left < 3.0:
            break
        bud = min(bud, left - 0.5)
        if i == len(stages) - 1:
            bud = left - 0.5
        b, v = _opt(cur, bud, p0, p1, lr0, lr1)
        if v < overallv:
            overall, overallv = b.copy(), v
        if i < len(stages) - 1:
            cur = np.repeat(overall, 2)
    return [float(a) for a in overall]