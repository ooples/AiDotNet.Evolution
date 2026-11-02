import time
import numpy as np


def _score(h):
    s = h.sum()
    if abs(s) < 1e-9 or not np.isfinite(s):
        return float("inf")
    n = len(h)
    m = 1
    while m < 2 * n:
        m *= 2
    f = np.fft.rfft(h, m)
    c = np.fft.irfft(f * f, m)[: 2 * n - 1]
    return 2 * n * abs(c).max() / s ** 2


def _opt(x, secs, p0, p1, lr0, lr1, state):
    n = len(x)
    m = 1
    while m < 2 * n:
        m *= 2
    t0 = time.time()
    mom = np.zeros(n)
    vel = np.zeros(n)
    b1, b2 = 0.9, 0.999
    step = 0
    while True:
        frac = (time.time() - t0) / secs
        if frac >= 1:
            break
        p = p0 * (p1 / p0) ** frac
        lr = lr1 + (lr0 - lr1) * (1 - frac)
        S = x.sum()
        if S <= 0 or not np.all(np.isfinite(x)):
            break
        fh = np.fft.rfft(x, m)
        c = np.fft.irfft(fh * fh, m)[: 2 * n - 1]
        ac = np.abs(c)
        cmax = ac.max()
        if cmax <= 0:
            break
        r = ac / cmax
        sp = (r ** p).sum()
        N = cmax * sp ** (1.0 / p)
        g = np.sign(c) * r ** (p - 1) * sp ** (1.0 / p - 1)
        gf = np.fft.rfft(g, m)
        hrf = np.fft.rfft(x[::-1], m)
        full = np.fft.irfft(gf * hrf, m)
        corr = full[n - 1: 2 * n - 1]
        grad = 2 * corr / N - 2.0 / S
        step += 1
        mom = b1 * mom + (1 - b1) * grad
        vel = b2 * vel + (1 - b2) * grad * grad
        mh = mom / (1 - b1 ** step)
        vh = vel / (1 - b2 ** step)
        x = x - lr * mh / (np.sqrt(vh) + 1e-8)
        if step % 25 == 0:
            sc = _score(x)
            if sc < state[0]:
                state[0] = sc
                state[1] = x.copy()
    sc = _score(x)
    if sc < state[0]:
        state[0] = sc
        state[1] = x.copy()
    return x


def construct():
    rng = np.random.default_rng(0)
    n = 40
    x = 1.0 + 0.2 * rng.standard_normal(n)
    h0 = np.ones(16)
    best = [_score(h0), h0]
    stages = [
        (4.0, 4.0, 128.0, 0.03, 0.002),
        (6.0, 32.0, 256.0, 0.01, 0.001),
        (10.0, 64.0, 512.0, 0.006, 0.0005),
        (14.0, 128.0, 1024.0, 0.003, 0.0002),
        (16.0, 256.0, 4096.0, 0.0015, 0.00005),
    ]
    for i, (secs, p0, p1, lr0, lr1) in enumerate(stages):
        local = [float("inf"), x.copy()]
        _opt(x, secs, p0, p1, lr0, lr1, local)
        if np.isfinite(local[0]):
            xb = local[1]
            if local[0] < best[0]:
                best = [local[0], xb.copy()]
        else:
            xb = np.ones(n * 2 ** i)
        x = xb
        if i < len(stages) - 1:
            x = np.repeat(x, 2)
        x = x / np.mean(np.abs(x))
    return [float(v) for v in best[1]]