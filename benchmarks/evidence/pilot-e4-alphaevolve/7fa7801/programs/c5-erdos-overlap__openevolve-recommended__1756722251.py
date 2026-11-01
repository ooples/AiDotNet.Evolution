"""Erdos minimum overlap: multi-start smoothed-max Adam + refinement + sequential LP polish."""
import time
import numpy as np


def _score(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _project(h, target):
    lo, hi = -1.0 - h.max(), 1.0 - h.min() + 1.0
    for _ in range(80):
        mid = 0.5 * (lo + hi)
        if np.clip(h + mid, 0, 1).sum() < target:
            lo = mid
        else:
            hi = mid
    return np.clip(h + 0.5 * (lo + hi), 0, 1)


def _grad(h, beta):
    n = len(h)
    g = 1 - h
    C = np.correlate(h, g, mode="full")
    f = 2.0 * C / n
    w = np.exp(beta * (f - f.max()))
    w /= w.sum()
    ga = np.convolve(w, g)[n - 1:2 * n - 1]
    gv = np.correlate(w, h, mode="valid")[::-1]
    return (2.0 / n) * (ga - gv)


def _adam(h, T, b0, b1, lr0, lr1, target):
    best_h, best = h.copy(), _score(h)
    m = np.zeros(len(h))
    v = np.zeros(len(h))
    t0 = time.time()
    it = 0
    while True:
        el = time.time() - t0
        if el > T:
            break
        frac = el / T
        beta = b0 * (b1 / b0) ** frac
        lr = lr1 + (lr0 - lr1) * (1 - frac)
        gr = _grad(h, beta)
        it += 1
        m = 0.9 * m + 0.1 * gr
        v = 0.999 * v + 0.001 * gr * gr
        mh = m / (1 - 0.9 ** it)
        vh = v / (1 - 0.999 ** it)
        h = _project(h - lr * mh / (np.sqrt(vh) + 1e-12), target)
        if it % 5 == 0:
            s = _score(h)
            if s < best:
                best, best_h = s, h.copy()
    s = _score(h)
    if s < best:
        best, best_h = s, h.copy()
    return best_h, best


def _jac(h):
    n = len(h)
    g = 1 - h
    gp = np.zeros(3 * n)
    hp = np.zeros(3 * n)
    gp[n:2 * n] = g
    hp[n:2 * n] = h
    I = np.arange(n)[None, :]
    S = (np.arange(2 * n - 1) - (n - 1))[:, None]
    return gp[I - S + n] - hp[I + S + n]


def _slp(h, target, T):
    from scipy.optimize import linprog
    n = len(h)
    t0 = time.time()
    best = _score(h)
    delta = 0.01
    c = np.zeros(n + 1)
    c[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    while time.time() - t0 < T and delta > 1e-7:
        C = np.correlate(h, 1 - h, mode="full")
        J = _jac(h)
        M = len(C)
        A = np.hstack([J, -np.ones((M, 1))])
        lo = np.maximum(-delta, -h)
        hi = np.minimum(delta, 1 - h)
        bounds = [(lo[i], hi[i]) for i in range(n)] + [(None, None)]
        res = linprog(c, A_ub=A, b_ub=-C, A_eq=Aeq, b_eq=[0.0],
                      bounds=bounds, method="highs")
        if res.status != 0:
            delta *= 0.5
            continue
        hn = _project(h + res.x[:n], target)
        sc = _score(hn)
        if sc < best - 1e-13:
            h, best = hn, sc
            delta = min(delta * 1.5, 0.05)
        else:
            delta *= 0.5
    return h, best


def construct():
    n = 128
    fallback = [0.5] * 16
    try:
        target = n / 2.0
        bh, bs = None, 1e9
        for seed in (1, 2, 3):
            rng = np.random.default_rng(seed)
            h = _project(0.5 + 0.2 * rng.standard_normal(n), target)
            h, s = _adam(h, 6.0, 50.0, 3000.0, 0.02, 0.0005, target)
            if s < bs:
                bh, bs = h, s
        h, b1 = _adam(bh, 9.0, 1000.0, 30000.0, 0.002, 0.0001, target)
        if b1 < bs:
            bh, bs = h, b1
        h = _project(bh, target)
        try:
            hp, bp = _slp(h, target, 15.0)
            hp = _project(hp, target)
            if _score(hp) < _score(h):
                h = hp
        except Exception:
            pass
        h = _project(h, target)
        if abs(h.sum() - target) > 1e-9 * n or _score(h) >= 0.5:
            return fallback
        return [float(x) for x in h]
    except Exception:
        return fallback