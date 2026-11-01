import time
import numpy as np


def _fftconv(a, b):
    L = len(a) + len(b) - 1
    s = 1 << (L - 1).bit_length()
    return np.fft.irfft(np.fft.rfft(a, s) * np.fft.rfft(b, s), s)[:L]


def _score(h):
    n = len(h)
    return 2 * _fftconv(h, (1 - h)[::-1]).max() / n


def _project(x, target):
    lo, hi = -2.0, 2.0
    for _ in range(80):
        mid = 0.5 * (lo + hi)
        if np.clip(x + mid, 0, 1).sum() > target:
            hi = mid
        else:
            lo = mid
    return np.clip(x + 0.5 * (lo + hi), 0, 1)


def _optimize(h, steps, lr0, b0, b1, t_end):
    N = len(h)
    target = N / 2.0
    h = _project(h, target)
    best_h, best_s = h.copy(), _score(h)
    m = np.zeros(N)
    v = np.zeros(N)
    t_start = time.time()
    for t in range(1, steps + 1):
        now = time.time()
        if now > t_end:
            break
        frac = max(t / steps, (now - t_start) / max(t_end - t_start, 1e-9))
        frac = min(frac, 1.0)
        beta = b0 * (b1 / b0) ** frac
        g = 1 - h
        c = _fftconv(h, g[::-1]) * 2.0 / N
        w = np.exp(beta * (c - c.max()))
        w /= w.sum()
        gh = _fftconv(w, g)[N - 1:2 * N - 1]
        gg = _fftconv(w, h[::-1])[N - 1:2 * N - 1][::-1]
        grad = (gh - gg) * 2.0 / N
        m = 0.9 * m + 0.1 * grad
        v = 0.999 * v + 0.001 * grad * grad
        mh = m / (1 - 0.9 ** t)
        vh = v / (1 - 0.999 ** t)
        lr = lr0 * (1 - 0.9 * frac)
        h = _project(h - lr * mh / (np.sqrt(vh) + 1e-12), target)
        if t % 10 == 0:
            s = _score(h)
            if s < best_s:
                best_s, best_h = s, h.copy()
    s = _score(h)
    if s < best_s:
        best_s, best_h = s, h.copy()
    return best_h, best_s


def construct():
    t0 = time.time()
    rng = np.random.default_rng(0)
    h = 0.5 + 0.1 * rng.standard_normal(100)
    # (steps, lr, beta0, beta1, duration, upsample)
    stages = [
        (6000, 0.01, 50, 3000, 4, False),
        (5000, 0.003, 1000, 5000, 6, True),
        (4000, 0.0015, 2000, 8000, 7, True),
        (6000, 0.0008, 3000, 12000, 9, True),
        (6000, 0.0004, 8000, 30000, 9, False),
        (8000, 0.0002, 20000, 60000, 7, False),
        (8000, 0.0001, 40000, 120000, 6, False),
        (8000, 0.00004, 100000, 300000, 5, False),
    ]
    best_s, best_h = float("inf"), None
    prev = None
    for i, (steps, lr, b0, b1, dur, up) in enumerate(stages):
        if time.time() > t0 + 50:
            break
        if i > 0:
            h = np.repeat(prev, 2) if up else prev.copy()
        t_end = min(t0 + 53, time.time() + dur)
        hs, ss = _optimize(h, steps, lr, b0, b1, t_end)
        prev = hs
        if ss < best_s:
            best_s, best_h = ss, hs.copy()
    n = len(best_h)
    best_h = _project(best_h, n / 2)
    if abs(best_h.sum() - n / 2) > 1e-9 * n or best_h.min() < 0 or best_h.max() > 1:
        best_h = np.full(n, 0.5)
    return [float(a) for a in best_h]