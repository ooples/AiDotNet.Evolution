import time
import numpy as np
from scipy.signal import fftconvolve
from scipy.optimize import minimize, linprog


def _value(h):
    s = np.sum(h)
    c = np.convolve(h, h)
    return 2 * len(h) * abs(np.max(c)) / (s * s)


def _make_fg(p, n):
    def fg(x):
        s = np.sum(x)
        h = x / s
        c = fftconvolve(h, h)
        r = np.maximum(c, 0.0)
        m = r.max()
        if m <= 0:
            return 0.0, np.zeros_like(x)
        q = r / m
        S = np.sum(q ** p)
        F = m * S ** (1.0 / p)
        w = (r / F) ** (p - 1)
        g_h = 2.0 * fftconvolve(w, h[::-1])[n - 1:2 * n - 1]
        g_x = (g_h - np.dot(g_h, h)) / s
        return F * 1e3, g_x * 1e3
    return fg


def _stage(x, ps, t0, tlimit, maxiter):
    n = len(x)
    best, best_v = x.copy(), _value(x)
    for p in ps:
        if time.time() - t0 > tlimit:
            break
        res = minimize(_make_fg(p, n), x, jac=True, method="L-BFGS-B",
                       options={"maxiter": maxiter, "maxfun": int(maxiter * 1.5)})
        xn = res.x
        s = np.sum(xn)
        if abs(s) < 1e-9 or not np.all(np.isfinite(xn)):
            break
        x = xn / s * n
        v = _value(x)
        if v < best_v:
            best_v, best = v, x.copy()
    return x, best, best_v


def _up(x, m):
    n = len(x)
    xs = (np.arange(n) + 0.5) / n
    ms = (np.arange(m) + 0.5) / m
    y = np.interp(ms, xs, x)
    return y / y.sum() * m


def construct():
    t0 = time.time()
    n = 600
    rng = np.random.default_rng(0)
    n0 = 100
    t = np.linspace(-1, 1, n0)
    x = (1.0 + 0.3 * rng.random(n0)) * (1.0 + 0.5 * t * t)

    best = None
    best_v = float("inf")
    try:
        x, _, _ = _stage(x, [8, 16, 32, 64, 128, 256, 512], t0, 4, 600)
        x = _up(x, 200)
        x, _, _ = _stage(x, [64, 128, 256, 512, 1024], t0, 9, 600)
        x = _up(x, 400)
        x, _, _ = _stage(x, [128, 256, 512, 1024, 2048], t0, 16, 600)
        x = _up(x, n)
        x, best, best_v = _stage(x, [256, 512, 1024, 2048, 4096], t0, 25, 800)
    except Exception:
        pass

    if best is None or best_v >= 2.0 or len(best) != n:
        return [1.0] * 16

    try:
        h = best / best.sum()
        cur = 2 * n * np.max(np.convolve(h, h))
        K = np.arange(2 * n - 1)[:, None] - np.arange(n)[None, :]
        mask = (K >= 0) & (K < n)
        Kc = np.clip(K, 0, n - 1)
        delta = 0.3 / n
        Aeq = np.zeros((1, n + 1))
        Aeq[0, :n] = 1.0
        cost = np.zeros(n + 1)
        cost[n] = 1.0
        while time.time() - t0 < 50 and delta > 1e-10:
            c = np.convolve(h, h)
            bound = 2.0 * delta * np.sum(np.abs(h)) + delta * delta * n
            idx = np.where(c >= c.max() - 2.0 * bound)[0]
            A = 2.0 * h[Kc[idx]] * mask[idx]
            Aub = np.hstack([A, -np.ones((len(idx), 1))])
            bounds = [(-delta, delta)] * n + [(None, None)]
            res = linprog(cost, A_ub=Aub, b_ub=-c[idx], A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs")
            if res.status != 0:
                delta *= 0.5
                continue
            hn = h + res.x[:n]
            hn = hn / hn.sum()
            v = 2 * n * np.max(np.convolve(hn, hn))
            if v < cur:
                gain = cur - v
                h, cur = hn, v
                if gain < 1e-6:
                    delta *= 0.7
            else:
                delta *= 0.6
        xs = h * n
        v = _value(xs)
        if np.all(np.isfinite(xs)) and v < best_v:
            best_v, best = v, xs
    except Exception:
        pass

    return [float(v) for v in best]