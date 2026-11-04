"""Smoothed-max L-BFGS continuation with resolution doubling (200 -> 3200), FFT convolution, deadlines, final polish."""
import time
import numpy as np


class _Timeout(Exception):
    pass


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    n = len(h)
    if n >= 500:
        from scipy.signal import fftconvolve
        c = fftconvolve(h, h)
    else:
        c = np.convolve(h, h)
    return 2 * n * np.max(c) / (s * s)


def _make_obj(n, beta, deadline):
    big = n >= 500
    if big:
        from scipy.signal import fftconvolve as conv
    else:
        conv = np.convolve

    def f(x):
        if time.time() > deadline:
            raise _Timeout()
        s = x.sum()
        h = x / s
        t = 2 * n * conv(h, h)
        m = t.max()
        e = np.exp(beta * (t - m))
        Z = e.sum()
        val = m + np.log(Z) / beta
        w = e / Z
        if big:
            gh = 4 * n * conv(w, h[::-1], "valid")
        else:
            gh = 4 * n * np.correlate(w, h, "valid")
        gx = (gh - h @ gh) / s
        return val, gx
    return f


def _continuation(x, betas, deadline, maxiter):
    from scipy.optimize import minimize
    n = len(x)
    best, bestv = x.copy(), _score(x)
    for beta in betas:
        if time.time() > deadline:
            break
        try:
            res = minimize(_make_obj(n, beta, deadline), x, jac=True,
                           method="L-BFGS-B",
                           options={"maxiter": maxiter,
                                    "maxfun": int(maxiter * 1.3)})
        except _Timeout:
            break
        xn = res.x
        if not np.all(np.isfinite(xn)) or xn.sum() <= 0:
            break
        x = xn / xn.sum()
        v = _score(x)
        if v < bestv:
            bestv, best = v, x.copy()
    return best, bestv


def construct():
    t0 = time.time()
    n = 200
    best = np.ones(n) / n
    bestv = _score(best)
    try:
        betas0 = [20, 50, 100, 200, 400, 800, 1500, 3000, 6000, 12000, 30000, 80000, 200000]
        cands = []
        for seed in (1, 2):
            rng = np.random.default_rng(seed)
            x = np.ones(n) + 0.3 * rng.random(n)
            x = x / x.sum()
            b, v = _continuation(x, betas0, time.time() + 4.5, 1200)
            cands.append((v, b))
        cands.sort(key=lambda c: c[0])
        if cands[0][0] < bestv:
            bestv, best = cands[0]
        stages = [
            ([3000, 6000, 12000, 30000, 80000, 200000, 500000], 17, 2500),
            ([12000, 30000, 80000, 200000, 500000, 1000000], 27, 2500),
            ([50000, 200000, 500000, 1000000, 2000000], 38, 2800),
            ([200000, 500000, 1000000, 2000000, 4000000], 50, 3000),
        ]
        for betas, limit, mi in stages:
            if time.time() - t0 > limit - 3:
                continue
            try:
                xs = np.repeat(best, 2)
                xs = xs / xs.sum()
                bn, vn = _continuation(xs, betas, t0 + limit, mi)
                if vn <= bestv:
                    best, bestv = bn, vn
                else:
                    best = xs
                    bestv = _score(xs)
            except Exception:
                break
        try:
            if time.time() - t0 < 52:
                bn, vn = _continuation(best.copy(),
                                       [3000000, 6000000, 12000000, 24000000],
                                       t0 + 57, 3000)
                if vn < bestv:
                    best, bestv = bn, vn
        except Exception:
            pass
    except Exception:
        pass
    if not np.all(np.isfinite(best)) or not np.isfinite(_score(best)):
        best = np.ones(n) / n
    return [float(v) for v in best]