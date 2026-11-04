import time
import numpy as np
from scipy.signal import fftconvolve


def _score(h):
    s = h.sum()
    return 2 * len(h) * np.max(np.convolve(h, h)) / s ** 2


def _smooth(h, beta):
    n = len(h)
    s = h.sum()
    c = fftconvolve(h, h)
    r = 2 * n * c / s ** 2
    mx = r.max()
    e = np.exp(beta * (r - mx))
    se = e.sum()
    f = mx + np.log(se) / beta
    w = e / se
    corr = fftconvolve(w, h[::-1])[n - 1:2 * n - 1]
    g = 2 * n * (2 * corr / s ** 2 - 2 * (w @ c) / s ** 3)
    return f, g


def _slp(h, t_end, linprog, toeplitz):
    n = len(h)
    best = h.copy()
    best_s = _score(best)
    delta = 0.003 / n
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    while time.time() < t_end - 1.0 and delta > 1e-10 / n:
        c = np.convolve(best, best)
        B = 2 * delta * np.abs(best).sum() + n * delta * delta
        idx = np.where(c >= c.max() - 2.5 * B)[0]
        col = np.concatenate([best, np.zeros(n - 1)])
        row = np.zeros(n)
        row[0] = best[0]
        T = toeplitz(col, row)[idx]
        A = np.hstack([2 * T, -np.ones((len(idx), 1))])
        bounds = [(-delta, delta)] * n + [(None, None)]
        remaining = max(1.0, t_end - time.time())
        try:
            res = linprog(cost, A_ub=A, b_ub=-c[idx], A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": remaining})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        cand = best + res.x[:n]
        sm = cand.sum()
        if not np.isfinite(sm) or sm <= 0:
            delta *= 0.5
            continue
        cand = cand / sm
        s = _score(cand)
        if s < best_s - 1e-13:
            best_s, best = s, cand
            delta *= 1.3
        else:
            delta *= 0.5
    return best, best_s


def construct():
    t0 = time.time()
    total = 50.0
    rng = np.random.default_rng(0)
    n = 50
    h = 1.0 + 0.2 * rng.random(n)
    h /= h.sum()
    best = h.copy()
    best_s = _score(best)
    try:
        from scipy.optimize import minimize
        stages = [(50, 0.05), (100, 0.12), (200, 0.25), (400, 0.45),
                  (800, 0.82)]
        for n, frac in stages:
            if len(h) != n:
                h = np.repeat(h, n // len(h))
                h = h / h.sum()
            t_stage = t0 + frac * total

            def cb(xk, _te=t_stage):
                if time.time() > _te:
                    raise StopIteration

            stage_best, stage_s = h.copy(), _score(h)
            beta = 50.0
            scale = n
            while beta < 5e5 and time.time() < t_stage:
                try:
                    res = minimize(lambda x: _smooth(x / scale, beta),
                                   h * scale, jac=True, method="L-BFGS-B",
                                   callback=cb,
                                   options={"maxiter": 600})
                    x = res.x / scale
                except Exception:
                    x = h
                sm = x.sum()
                if np.isfinite(sm) and sm > 0:
                    x = x / sm
                    s = _score(x)
                    if np.isfinite(s):
                        h = x
                        if s < stage_s:
                            stage_s, stage_best = s, x.copy()
                beta *= 1.6
            h = stage_best
            best_s, best = stage_s, stage_best.copy()
        try:
            from scipy.optimize import linprog
            from scipy.linalg import toeplitz
            cand, s = _slp(best.copy(), t0 + total, linprog, toeplitz)
            if s < best_s:
                best_s, best = s, cand
        except Exception:
            pass
    except Exception:
        pass
    return [float(x) for x in best]