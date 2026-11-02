import time
import numpy as np


def _value(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = np.convolve(h, h)
    return 2.0 * len(h) * abs(c.max()) / s ** 2


def _adam(h, n, budget):
    L = 2 * n - 1
    nf = 1
    while nf < L:
        nf *= 2
    best, best_v = h.copy(), _value(h)
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2, lr = 0.9, 0.999, 0.01
    ts = time.time()
    it = 0
    while time.time() - ts < budget:
        it += 1
        frac = min(1.0, (time.time() - ts) / budget)
        beta = 20.0 * (300.0 ** frac)
        F = np.fft.rfft(h, nf)
        c = np.fft.irfft(F * F, nf)[:L] / n
        e = np.exp(beta * (c - c.max()))
        w = e / e.sum()
        Fw = np.fft.rfft(w, nf)
        g = 2.0 * np.fft.irfft(Fw * np.conj(F), nf)[:n] / n
        g -= g.mean()
        m = b1 * m + (1 - b1) * g
        v = b2 * v + (1 - b2) * g * g
        mh = m / (1 - b1 ** it)
        vh = v / (1 - b2 ** it)
        h = h - lr * (1.0 - 0.9 * frac) * mh / (np.sqrt(vh) + 1e-8)
        s = h.sum()
        if abs(s) < 1e-9:
            h = np.ones(n)
            s = float(n)
        h = h * (n / s)
        if it % 20 == 0:
            val = _value(h)
            if val < best_v:
                best_v, best = val, h.copy()
    val = _value(h)
    if val < best_v:
        best_v, best = val, h.copy()
    return best, best_v


def _lp_polish(h, n, t0, limit):
    try:
        from scipy.optimize import linprog
    except Exception:
        return h
    L = 2 * n - 1
    idx = np.arange(L)[:, None] - np.arange(n)[None, :]
    mask = (idx >= 0) & (idx < n)
    idx = np.clip(idx, 0, n - 1)
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1.0
    best_v = _value(h)
    delta = 0.01
    last = 0.0
    while delta > 1e-9 and time.time() - t0 + 1.6 * last < limit:
        ts = time.time()
        c = np.convolve(h, h)
        margin = 2.5 * delta * np.abs(h).sum() + n * delta * delta
        sel = np.where(c >= c.max() - 3.5 * margin)[0]
        A = np.where(mask[sel], h[idx[sel]], 0.0)
        A_ub = np.hstack([2.0 * A, -np.ones((len(sel), 1))])
        bounds = [(-delta, delta)] * n + [(None, None)]
        try:
            r = linprog(cost, A_ub=A_ub, b_ub=-c[sel], A_eq=A_eq, b_eq=[0.0],
                        bounds=bounds, method="highs")
        except Exception:
            break
        last = time.time() - ts
        if r.x is None:
            delta *= 0.5
            continue
        hn = h + r.x[:n]
        vn = _value(hn)
        if vn < best_v:
            h, best_v = hn, vn
            delta *= 1.5
        else:
            delta *= 0.5
    return h


def construct():
    t0 = time.time()
    rng = np.random.default_rng(0)
    n = 500
    h = np.ones(n) + 0.1 * rng.standard_normal(n)
    h *= n / h.sum()
    best, best_v = _adam(h, n, 10.0)
    try:
        hp = _lp_polish(best.copy(), n, t0, 55.0)
        vp = _value(hp)
        if vp < best_v:
            best, best_v = hp, vp
    except Exception:
        pass
    if not np.isfinite(best_v) or best_v > 2.0:
        best = np.ones(16)
    return [float(x) for x in best]