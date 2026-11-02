import time
import numpy as np


def _val(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return 1e9
    return 2.0 * len(h) * abs(np.max(np.convolve(h, h))) / (s * s)


def _pnorm(h, deadline, ps=(16, 64, 256), maxiter=150):
    from scipy.optimize import minimize

    def make(p):
        def f(x):
            c = np.convolve(x, x)
            s = x.sum()
            cp = np.maximum(c, 0.0)
            m = cp.max()
            if m <= 0 or abs(s) < 1e-12:
                return 1e9, np.zeros_like(x)
            r = cp / m
            sp = np.sum(r ** p)
            val = np.log(m) + np.log(sp) / p - 2 * np.log(abs(s))
            g = (r ** (p - 1)) / (m * sp)
            gh = 2 * np.correlate(g, x, 'valid') - 2.0 / s
            return val, gh
        return f

    h = h / h.sum()
    for p in ps:
        if time.time() > deadline:
            break
        try:
            r = minimize(make(p), h, jac=True, method="L-BFGS-B",
                         options={"maxiter": maxiter})
            x = r.x
            if np.all(np.isfinite(x)) and abs(x.sum()) > 1e-9:
                h = x / x.sum()
        except Exception:
            break
    return h


def _refine(h, deadline):
    from scipy.optimize import linprog
    from scipy.linalg import toeplitz
    n = len(h)
    h = h / h.sum()
    bv = _val(h)
    r = 0.3 / n
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    Aeq = np.concatenate([np.ones(n), [0.0]])[None, :]
    while time.time() < deadline and r > 1e-9 / n:
        c = np.convolve(h, h)
        mx = c.max()
        rows = np.where(c > 0.3 * mx)[0]
        col = np.concatenate([h, np.zeros(n - 1)])
        row = np.zeros(n)
        row[0] = h[0]
        A = toeplitz(col, row)[rows]
        A_ub = np.hstack([2 * A, -np.ones((len(rows), 1))])
        bounds = [(-r, r)] * n + [(None, None)]
        try:
            res = linprog(cost, A_ub=A_ub, b_ub=-c[rows], A_eq=Aeq,
                          b_eq=[0.0], bounds=bounds, method="highs")
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        cand = h + res.x[:n]
        cv = _val(cand)
        if cv < bv - 1e-13:
            h, bv = cand / cand.sum(), cv
            r *= 1.2
        else:
            r *= 0.5
    return h, bv


def construct():
    t0 = time.time()
    total = 52.0
    n = 50
    x = (np.arange(n) + 0.5) / n
    h = 1.0 / np.sqrt(x * (1 - x) + 0.02)
    h = h / h.sum()
    best, bv = h.copy(), _val(h)
    try:
        stages = [(50, 6), (100, 10), (200, 18), (400, 100)]
        for m, share in stages:
            if time.time() - t0 > total - 2:
                break
            if len(h) != m:
                h = np.repeat(h, m // len(h))
                h = h / h.sum()
            dl = min(t0 + total, time.time() + share)
            if m == 400:
                dl = t0 + total
            # smooth warm start (kept only if it helps)
            try:
                hp = _pnorm(h, min(dl, time.time() + 3))
                if _val(hp) < _val(h) * 1.05:
                    h = hp
            except Exception:
                pass
            h, v = _refine(h, dl)
            if v < bv:
                bv, best = v, h.copy()
            else:
                h = best.copy() if len(best) == len(h) else h
    except Exception:
        pass
    return [float(v) for v in best]