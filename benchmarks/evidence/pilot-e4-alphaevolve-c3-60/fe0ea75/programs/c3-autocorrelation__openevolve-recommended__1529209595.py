import time
import numpy as np


def _val(h):
    n = len(h)
    return 2 * n * np.max(np.convolve(h, h)) / np.sum(h) ** 2


def _smooth(h, fracs, deadline, iters=300):
    from scipy.optimize import minimize
    x = h / h.sum()
    for fr in fracs:
        if time.time() > deadline:
            break
        t = fr * np.convolve(x, x).max()

        def f(z):
            s = z.sum()
            cv = np.convolve(z, z)
            c = cv / s ** 2
            mx = c.max()
            e = np.exp((c - mx) / t)
            se = e.sum()
            L = mx + t * np.log(se)
            w = e / se
            g = 2 * np.correlate(w, z, "valid") / s ** 2 \
                - 2 * np.dot(w, cv) / s ** 3
            return L, g

        try:
            res = minimize(f, x, jac=True, method="L-BFGS-B",
                           options={"maxiter": iters})
            z = res.x
            if np.isfinite(z).all() and abs(z.sum()) > 1e-9:
                z = z / z.sum()
                if _val(z) < _val(x):
                    x = z
        except Exception:
            break
    return x


def _refine(h, deadline):
    from scipy.optimize import linprog
    from scipy.linalg import toeplitz
    n = len(h)
    m = 2 * n - 1
    h = h / h.sum()
    best = h.copy()
    bestv = _val(h)
    r = 0.5 / n
    cost = np.zeros(n + 1)
    cost[n] = 1
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1
    while time.time() < deadline - 0.5 and r > 1e-12 / (n / 150.0):
        c = np.convolve(h, h)
        col = np.concatenate([2 * h, np.zeros(n - 1)])
        row = np.zeros(n)
        row[0] = col[0]
        A = toeplitz(col, row)
        M = c.max()
        S = np.abs(h).sum()
        thr = M - 4.0 * r * S - 2.0 * n * r * r
        idx = np.where(c >= thr)[0]
        if len(idx) < 1:
            idx = np.arange(m)
        A_ub = np.hstack([A[idx], -np.ones((len(idx), 1))])
        bounds = [(-r, r)] * n + [(None, None)]
        tl = max(0.5, min(10.0, deadline - time.time()))
        try:
            res = linprog(cost, A_ub=A_ub, b_ub=-c[idx], A_eq=A_eq,
                          b_eq=[0], bounds=bounds, method="highs",
                          options={"time_limit": tl})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        cand = h + res.x[:n]
        s = cand.sum()
        if s == 0:
            r *= 0.5
            continue
        cand = cand / s
        v = _val(cand)
        if v < bestv - 1e-12:
            bestv = v
            best = cand.copy()
            h = cand
            r *= 1.2
        else:
            r *= 0.5
    return best


def construct():
    start = time.time()
    rng = np.random.default_rng(0)
    n = 100
    h = np.ones(n) + rng.normal(0, 0.05, n)
    best = h / h.sum()
    try:
        best = _smooth(best, [0.05, 0.02, 0.008, 0.003], start + 6, 400)
        b2 = np.repeat(best, 2)
        b2 = b2 / b2.sum()
        best = _smooth(b2, [0.01, 0.004, 0.0015], start + 14, 400)
        stages = [(200, 20), (400, 33), (800, 55)]
        for n, end in stages:
            if len(best) != n:
                best = np.repeat(best, n // len(best))
                best = best / best.sum()
            cand = _refine(best, start + end)
            if _val(cand) <= _val(best) + 1e-12:
                best = cand
    except Exception:
        pass
    if not np.isfinite(_val(best)) or _val(best) > 2.0:
        return [1.0] * 16
    return [float(x) for x in best]