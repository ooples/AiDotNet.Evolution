import time
import numpy as np
from scipy.signal import fftconvolve
from scipy.optimize import minimize, linprog


def _score(h):
    h = np.asarray(h, dtype=float)
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = np.convolve(h, h)
    return 2 * len(h) * abs(c.max()) / s ** 2


def _smooth_stage(n, t0, limit):
    rng = np.random.default_rng(0)
    x = np.ones(n) + 0.1 * rng.standard_normal(n)

    def make_obj(p):
        def obj(x):
            h = x * x
            s = h.sum()
            c = np.maximum(fftconvolve(h, h), 0.0)
            cm = c.max()
            if cm <= 0 or s <= 0:
                return 1e9, np.zeros_like(x)
            r = c / cm
            P = (r ** p).sum()
            F = cm * P ** (1.0 / p)
            g = P ** (1.0 / p - 1.0) * r ** (p - 1)
            gh = 2.0 * fftconvolve(g, h[::-1])[n - 1:2 * n - 1]
            val = 2 * n * F / s ** 2
            grad_h = 2 * n * (gh / s ** 2 - 2 * F / s ** 3)
            return val, grad_h * 2 * x
        return obj

    best = np.ones(n)
    best_s = _score(best)
    for p in (8, 16, 32, 64, 128, 256, 512, 1024):
        if time.time() - t0 > limit:
            break
        try:
            res = minimize(make_obj(p), x, jac=True, method="L-BFGS-B",
                           options={"maxiter": 600})
            x = res.x
        except Exception:
            break
        h = x * x
        sc = _score(h)
        if sc < best_s:
            best_s, best = sc, h.copy()
    return best


def _lp_stage(h, deadline, r0):
    n = len(h)
    h = h * (n / h.sum())
    best = _score(h)
    c = np.zeros(n + 1)
    c[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    r = r0
    J = np.arange(n)
    while time.time() < deadline - 1.0 and r > 1e-9:
        c0 = np.convolve(h, h)
        cm = c0.max()
        margin = 2.5 * r * np.abs(h).sum() + 1e-9
        rows = np.nonzero(c0 >= cm - margin)[0]
        if len(rows) > 1500:
            rows = np.argsort(-c0)[:1500]
        idx = rows[:, None] - J[None, :]
        valid = (idx >= 0) & (idx < n)
        A = np.where(valid, 2 * h[np.clip(idx, 0, n - 1)], 0.0)
        Aub = np.hstack([A, -np.ones((len(rows), 1))])
        try:
            res = linprog(
                c, A_ub=Aub, b_ub=-c0[rows], A_eq=Aeq, b_eq=[0.0],
                bounds=[(-r, r)] * n + [(None, None)], method="highs",
                options={"time_limit": max(0.5, deadline - time.time() - 0.5)},
            )
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        cand = h + res.x[:n]
        v = _score(cand)
        if v < best - 1e-13:
            best = v
            h = cand
            r = min(r * 1.3, 0.5)
        else:
            r *= 0.6
    return h, best


def construct():
    t0 = time.time()
    try:
        n = 200
        try:
            h = _smooth_stage(n, t0, 8)
        except Exception:
            h = np.ones(n)
        h = h * (n / h.sum())
        best_h, best = h.copy(), _score(h)
        h, v = _lp_stage(h, t0 + 16, 0.05)
        if v < best:
            best, best_h = v, h.copy()
        for mult, dl, r0 in ((2, 30, 0.03), (2, 56, 0.015)):
            h2 = np.repeat(best_h, mult)
            h2 = h2 * (len(h2) / h2.sum())
            cur = _score(h2)
            h2, v = _lp_stage(h2, t0 + dl, r0)
            v = min(v, cur)
            if v <= best + 1e-9:
                best, best_h = v, h2.copy()
        if not np.isfinite(best) or best > 2.0:
            return [1.0] * 16
        return [float(x) for x in best_h]
    except Exception:
        return [1.0] * 16