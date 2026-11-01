"""Minimize 2*n*max(conv(h,h))/sum(h)^2 with multiresolution Adam on a log-sum-exp smoothed max."""
import time
import numpy as np


def _conv(h):
    n = len(h)
    if n < 1000:
        return np.convolve(h, h)
    L = 1 << int(np.ceil(np.log2(2 * n)))
    f = np.fft.rfft(h, L)
    return np.fft.irfft(f * f, L)[: 2 * n - 1]


def _corr(wc, h):
    n = len(h)
    if n < 1000:
        return np.correlate(wc, h, "valid")
    L = 1 << int(np.ceil(np.log2(3 * n)))
    r = np.fft.irfft(np.fft.rfft(wc, L) * np.conj(np.fft.rfft(h, L)), L)
    return r[:n]


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = _conv(h)
    return 2 * len(h) * abs(np.max(c)) / s ** 2


def _stage(h, t_end, best, beta0, beta1, lr0, lrmin):
    n = len(h)
    h = h.copy()
    m = np.zeros(n)
    v = np.zeros(n)
    t0 = time.time()
    dur = max(t_end - t0, 1e-3)
    t = 0
    while True:
        frac = (time.time() - t0) / dur
        if frac >= 1.0:
            break
        t += 1
        beta = beta0 * (beta1 / beta0) ** frac
        S = h.sum()
        if abs(S) < 1e-9:
            break
        c = _conv(h)
        k = 2 * n / S ** 2
        u = c * k
        e = np.exp(beta * (u - u.max()))
        w = e / e.sum()
        gh = 2 * _corr(w * k, h) - 2.0 * np.sum(w * u) / S
        m = 0.9 * m + 0.1 * gh
        v = 0.999 * v + 0.001 * gh * gh
        mh = m / (1 - 0.9 ** t)
        vh = v / (1 - 0.999 ** t)
        lr = lr0 * (1 - frac) + lrmin
        h = h - lr * mh / (np.sqrt(vh) + 1e-12)
        h *= n / h.sum()
        if t % 20 == 0:
            sc = _score(h)
            if sc < best[0]:
                best[0] = sc
                best[1] = h.copy()
    sc = _score(h)
    if sc < best[0]:
        best[0] = sc
        best[1] = h.copy()
    return h


def construct():
    T0 = time.time()
    budget = 53.0
    rng = np.random.default_rng(0)
    n = 200
    h = 1.0 + 0.1 * rng.standard_normal(n)
    best = [_score(np.ones(n)), np.ones(n)]
    plan = [(200, 0.07, 30, 300, 0.02),
            (400, 0.11, 100, 1000, 0.01),
            (800, 0.18, 200, 2500, 0.005),
            (1600, 0.24, 400, 5000, 0.003),
            (3200, 0.28, 800, 10000, 0.002),
            (3200, 0.12, 6000, 30000, 0.0004)]
    final = None
    try:
        for i, (nn, fr, b0, b1, lr0) in enumerate(plan):
            if len(h) != nn:
                h = np.repeat(h, nn // len(h))
                h *= nn / h.sum()
            best = [_score(h), h.copy()]
            end = T0 + budget * sum(p[1] for p in plan[: i + 1])
            h = _stage(h, end, best, b0, b1, lr0, lr0 * 0.02 if i == 5 else 1e-4)
            h = best[1].copy()
            h *= len(h) / h.sum()
            final = best[1].copy()
    except Exception:
        if final is None:
            final = best[1]
    out = best[1] if final is None else final
    if best[1] is not None and _score(best[1]) < _score(out):
        out = best[1]
    if out is None or not np.all(np.isfinite(out)) or abs(np.sum(out)) < 1e-9:
        out = np.ones(400)
    return [float(a) for a in out]