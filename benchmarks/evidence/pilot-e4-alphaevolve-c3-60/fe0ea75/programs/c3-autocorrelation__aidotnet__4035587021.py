import time
import numpy as np
from scipy.signal import fftconvolve


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    return 2.0 * len(h) * np.max(np.convolve(h, h)) / s ** 2


def _smooth(x, ps, t0, limit, maxiter):
    from scipy.optimize import minimize

    def make(p):
        def f(x):
            h = x * x
            s = h.sum()
            c = np.maximum(fftconvolve(h, h), 0.0)
            m = c.max()
            if m <= 0 or s <= 0:
                return 1e9, np.zeros_like(x)
            r = c / m
            S = (r ** p).sum()
            val = np.log(m) + np.log(S) / p - 2 * np.log(s)
            g = r ** (p - 1) / (m * S)
            gh = 2 * fftconvolve(g, h[::-1], mode="valid") - 2.0 / s
            return val, gh * 2 * x
        return f

    for p in ps:
        if time.time() - t0 > limit:
            break
        try:
            res = minimize(make(p), x, jac=True, method="L-BFGS-B",
                           options={"maxiter": maxiter})
            if np.all(np.isfinite(res.x)):
                x = res.x
        except Exception:
            break
    return x


def _slp(h, t0, deadline, r0):
    from scipy.optimize import linprog
    n = len(h)
    m = 2 * n - 1
    h = h / h.sum()
    best = _score(h)
    r = r0 / n
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    ar_m = np.arange(m)
    ar_n = np.arange(n)
    while r > 1e-11 and time.time() - t0 < deadline - 0.5:
        c = np.convolve(h, h)
        mx = c.max()
        delta = 6.0 * r * np.abs(h).sum() + 2.0 * r * r * n + 1e-12
        rows = ar_m[c >= mx - delta]
        idx = rows[:, None] - ar_n[None, :]
        valid = (idx >= 0) & (idx < n)
        T = np.where(valid, h[np.clip(idx, 0, n - 1)], 0.0)
        A = np.hstack([2 * T, -np.ones((len(rows), 1))])
        bounds = [(-r, r)] * n + [(None, None)]
        rem = deadline - (time.time() - t0)
        try:
            res = linprog(cost, A_ub=A, b_ub=-c[rows], A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": float(max(rem, 0.5))})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            if rem < 1:
                break
            r *= 0.5
            continue
        hn = h + res.x[:n]
        s = hn.sum()
        if abs(s) < 1e-12:
            r *= 0.5
            continue
        hn = hn / s
        f = _score(hn)
        if f < best - 1e-13:
            best = f
            h = hn
            r *= 1.3
        else:
            r *= 0.5
    return h, best


def _up(h, n):
    xs = np.linspace(0, 1, len(h))
    xt = np.linspace(0, 1, n)
    g = np.interp(xt, xs, h)
    return g / g.sum()


def construct():
    t0 = time.time()
    try:
        rng = np.random.default_rng(0)
        x = np.sqrt(1.0 + 0.3 * rng.random(100))
        try:
            x = _smooth(x, (8, 32, 128, 512, 2048), t0, 5, 400)
        except Exception:
            pass
        h = x * x
        if not np.all(np.isfinite(h)) or h.sum() <= 0:
            h = np.ones(100)
        h = h / h.sum()
        h2 = _up(h, 200)
        best_h, best_f = h2, _score(h2)
        try:
            x2 = _smooth(np.sqrt(h2), (512, 2048, 8192), t0, 12, 500)
            hc = x2 * x2
            if np.all(np.isfinite(hc)) and hc.sum() > 0:
                hc = hc / hc.sum()
                fc = _score(hc)
                if fc < best_f:
                    best_h, best_f = hc, fc
        except Exception:
            pass
        h3, f3 = _slp(best_h, t0, 26, 0.03)
        if f3 < best_f and np.all(np.isfinite(h3)):
            best_h, best_f = h3, f3
        try:
            h4 = _up(best_h, 300)
            h5, f5 = _slp(h4, t0, 54, 0.01)
            if f5 < best_f and np.all(np.isfinite(h5)):
                best_h, best_f = h5, f5
        except Exception:
            pass
        if np.all(np.isfinite(best_h)) and best_f < 2.0:
            return [float(v) for v in best_h]
    except Exception:
        pass
    return [1.0] * 16