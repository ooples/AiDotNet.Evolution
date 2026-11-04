import time
import numpy as np


def _obj(h):
    n = len(h)
    s = np.sum(h)
    if s == 0 or not np.all(np.isfinite(h)):
        return float("inf")
    return 2 * n * np.max(np.convolve(h, h)) / (s * s)


def _grad_stage(n, deadline, seed=0):
    from scipy.signal import fftconvolve
    rng = np.random.default_rng(seed)
    x = np.sqrt(1.0 + 0.3 * rng.random(n))
    m1 = np.zeros(n)
    m2 = np.zeros(n)
    t0 = time.time()
    total = max(deadline - t0, 0.1)
    best_h, best_v = None, float("inf")
    it = 0
    lr = 0.02
    while True:
        frac = (time.time() - t0) / total
        if frac >= 1:
            break
        p = 20.0 * (3000.0 / 20.0) ** frac
        h = x * x
        s = h.sum()
        f = np.maximum(fftconvolve(h, h), 0.0)
        m = f.max()
        if m <= 0:
            break
        w = (f / m) ** p
        S = w.sum()
        g = w / (S * (f + 1e-12 * m))
        corr = fftconvolve(g, h[::-1])[n - 1:2 * n - 1]
        dh = 2 * corr - 2.0 / s
        gx = dh * 2 * x
        it += 1
        m1 = 0.9 * m1 + 0.1 * gx
        m2 = 0.999 * m2 + 0.001 * gx * gx
        step = lr * (1 - 0.9 * frac) * (m1 / (1 - 0.9 ** it)) / (
            np.sqrt(m2 / (1 - 0.999 ** it)) + 1e-18)
        x = x - step * np.sqrt(s / n)
        x *= np.sqrt(n / np.sum(x * x))
        if it % 50 == 0:
            v = _obj(x * x)
            if v < best_v:
                best_v, best_h = v, (x * x).copy()
    v = _obj(x * x)
    if v < best_v:
        best_v, best_h = v, x * x
    return best_h, best_v


def _slp(h, deadline, linprog, rcap):
    n = len(h)
    h = h / h.sum()
    cur = _obj(h)
    r = rcap
    J = np.arange(n)
    c = np.zeros(n + 1)
    c[-1] = 1.0
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1.0
    while r > 1e-8 / n:
        remaining = deadline - time.time()
        if remaining < 0.4:
            break
        f0 = np.convolve(h, h)
        margin = 2 * r * np.sum(np.abs(h)) + n * r * r
        rows = np.nonzero(f0 >= f0.max() - 2 * margin)[0]
        idx = rows[:, None] - J[None, :]
        valid = (idx >= 0) & (idx < n)
        A = np.zeros((len(rows), n + 1))
        A[:, :n] = np.where(valid, 2 * h[np.clip(idx, 0, n - 1)], 0.0)
        A[:, n] = -1.0
        bounds = [(-r, r)] * n + [(None, None)]
        try:
            res = linprog(c, A_ub=A, b_ub=-f0[rows], A_eq=A_eq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": max(0.3, remaining - 0.3)})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            if remaining < 1.0:
                break
            r *= 0.5
            continue
        hn = h + res.x[:n]
        s = hn.sum()
        if s <= 0:
            r *= 0.5
            continue
        hn = hn / s
        v = _obj(hn)
        if v < cur - 1e-13:
            h, cur = hn, v
            r = min(r * 1.4, rcap)
        else:
            r *= 0.5
    return h, cur


def construct():
    t0 = time.time()
    n = 200
    best = np.ones(n)
    best_v = _obj(best)
    try:
        h, v = _grad_stage(n, t0 + 11.0)
        if h is not None and np.isfinite(v) and v < best_v:
            best, best_v = h / h.sum(), v
        from scipy.optimize import linprog
        h2, v2 = _slp(best.copy(), t0 + 30.0, linprog, 0.03 / n)
        if np.all(np.isfinite(h2)) and v2 < best_v:
            best, best_v = h2, v2
        hu = np.repeat(best, 2)
        hu = hu / hu.sum()
        h3, v3 = _slp(hu, t0 + 56.0, linprog, 0.01 / len(hu))
        if np.all(np.isfinite(h3)) and v3 < best_v:
            best, best_v = h3, v3
    except Exception:
        pass
    if not np.isfinite(best_v) or best_v > 2.0:
        best = np.ones(n)
    return [float(x) for x in best]