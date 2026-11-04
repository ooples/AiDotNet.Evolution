import time
import numpy as np


def _score(h):
    s = h.sum()
    if abs(s) < 1e-9:
        return float("inf")
    return 2 * len(h) * np.max(np.convolve(h, h)) / (s * s)


def _smooth_opt(h, t, fftconvolve, minimize, maxiter):
    n = len(h)
    h = h * n / h.sum()
    m0 = np.max(fftconvolve(h, h))
    beta = t / m0

    def fg(x):
        s = x.sum()
        c = fftconvolve(x, x)
        m = c.max()
        e = np.exp(beta * (c - m))
        z = e.sum()
        L = m + np.log(z) / beta
        w = e / z
        gL = 2 * np.correlate(w, x, "valid")
        f = 2 * n * L / (s * s)
        g = 2 * n * (gL / (s * s) - 2 * L / s ** 3)
        return f, g

    try:
        res = minimize(fg, h, jac=True, method="L-BFGS-B",
                       options={"maxiter": maxiter, "maxfun": maxiter * 2})
        x = res.x
        if x.sum() > 1e-9 and np.all(np.isfinite(x)):
            return x * n / x.sum()
    except Exception:
        pass
    return h


def _refine(h, deadline, r, linprog, toeplitz, frac):
    n = len(h)
    h = h * n / h.sum()
    bs = _score(h)
    c = np.zeros(n + 1)
    c[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    while time.time() < deadline - 0.3 and r > 1e-7:
        conv = np.convolve(h, h)
        sel = np.where(conv >= frac * conv.max())[0]
        col = np.concatenate([h, np.zeros(n - 1)])
        row = np.zeros(n)
        row[0] = h[0]
        C = toeplitz(col, row)
        A = np.hstack([2 * C[sel], -np.ones((len(sel), 1))])
        bounds = [(-r, r)] * n + [(None, None)]
        try:
            res = linprog(c, A_ub=A, b_ub=-conv[sel], A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": max(1.0, deadline - time.time())})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        cand = h + res.x[:n]
        s = cand.sum()
        if s <= 1e-9:
            r *= 0.5
            continue
        cand = cand * n / s
        cs = _score(cand)
        if cs < bs - 1e-12:
            h, bs = cand, cs
            r = min(r * 1.3, 1.0)
        else:
            r *= 0.5
    return h, bs


def _resample(h, m):
    idx = np.minimum(((np.arange(m) + 0.5) / m * len(h)).astype(int), len(h) - 1)
    g = h[idx]
    return g * m / g.sum()


def construct():
    t0 = time.time()
    fallback = [1.0] * 16
    try:
        from scipy.optimize import linprog, minimize
        from scipy.linalg import toeplitz
        from scipy.signal import fftconvolve

        n = 100
        rng = np.random.default_rng(0)
        x = (np.arange(n) + 0.5) / n
        h = 1.0 / np.sqrt(np.maximum(x * (1 - x), 1e-2) * 4)
        h = h + 0.1 * rng.random(n)
        h = h * n / h.sum()
        best, bs = h.copy(), _score(h)

        final = 800
        sizes = [100, 200, 400, final]
        ts = [40, 150, 600, 2500, 10000, 40000, 150000, 600000, 1000000]
        for sz in sizes:
            if len(best) != sz:
                best = _resample(best, sz)
                bs = _score(best)
            h = best.copy()
            for t in ts:
                if time.time() - t0 > 33:
                    break
                h = _smooth_opt(h, t, fftconvolve, minimize, 3000)
                v = _score(h)
                if v < bs:
                    best, bs = h.copy(), v
                else:
                    h = best.copy()
        if len(best) != final:
            best = _resample(best, final)
            bs = _score(best)

        plan = [(43, 0.02, 0.7), (52, 0.01, 0.8), (56, 0.005, 0.85)]
        h = best.copy()
        for b, r0, fr in plan:
            h, v = _refine(h, t0 + b, r0, linprog, toeplitz, fr)
            if v < bs:
                best, bs = h.copy(), v
            else:
                h = best.copy()
        if bs < 2.0:
            return [float(v) for v in best]
    except Exception:
        pass
    return fallback