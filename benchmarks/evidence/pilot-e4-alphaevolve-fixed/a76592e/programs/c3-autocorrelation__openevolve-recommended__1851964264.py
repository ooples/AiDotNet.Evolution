import time
import numpy as np
from scipy.signal import fftconvolve


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = np.convolve(h, h)
    return 2 * len(h) * abs(c.max()) / s ** 2


class _Stop(Exception):
    pass


def _opt(h, beta, deadline, maxiter=200):
    from scipy.optimize import minimize
    n = len(h)

    def f(x):
        if time.time() > deadline:
            raise _Stop()
        s = x.sum()
        c = fftconvolve(x, x)
        u = 2 * n * c / s ** 2
        z = beta * u
        zm = z.max()
        e = np.exp(z - zm)
        S = e.sum()
        L = (zm + np.log(S)) / beta
        w = e / S
        g = w * 2 * n / s ** 2
        gh = 2 * fftconvolve(g, x[::-1], mode="valid")
        gh = gh - 2 / s * np.dot(w, u)
        return L, gh

    state = {"x": h.copy()}

    def cb(xk):
        state["x"] = xk.copy()

    try:
        r = minimize(f, h, jac=True, method="L-BFGS-B", callback=cb,
                     options={"maxiter": maxiter, "maxcor": 20})
        return r.x
    except Exception:
        return state["x"]


def _polish(h, deadline, delta=0.01):
    from scipy.optimize import linprog
    from scipy.linalg import toeplitz
    n = len(h)
    m = 2 * n - 1
    cur = _score(h)
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1
    cost = np.zeros(n + 1)
    cost[-1] = 1
    while time.time() < deadline - 1 and delta > 1e-6:
        c = np.convolve(h, h)
        col = np.concatenate([h, np.zeros(n - 1)])
        row = np.zeros(n)
        row[0] = h[0]
        A = 2 * toeplitz(col, row)
        A_ub = np.hstack([A, -np.ones((m, 1))])
        bounds = [(-delta, delta)] * n + [(None, None)]
        try:
            rem = max(1.0, deadline - time.time())
            res = linprog(cost, A_ub=A_ub, b_ub=-c, A_eq=A_eq, b_eq=[0],
                          bounds=bounds, method="highs",
                          options={"time_limit": rem})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = h + res.x[:n]
        v = _score(hn)
        if v < cur:
            h, cur = hn, v
            delta *= 1.3
        else:
            delta *= 0.5
    return h, cur


def construct():
    t0 = time.time()
    deadline = t0 + 38
    rng = np.random.default_rng(0)
    n = 100
    best = np.ones(n)
    best_val = _score(best)
    try:
        h = np.ones(n) + 0.3 * rng.random(n)
        h *= n / h.sum()
        sizes = [100, 200, 400, 800]
        betas = [30, 100, 300, 1000, 3000, 10000]
        for i, sz in enumerate(sizes):
            if i > 0:
                if time.time() > deadline - 3:
                    break
                h = np.repeat(h, 2)
                h *= len(h) / h.sum()
            for b in betas:
                if time.time() > deadline:
                    break
                h = _opt(h, b, deadline)
                h = h * (len(h) / h.sum())
                v = _score(h)
                if v < best_val:
                    best, best_val = h.copy(), v
        # LP polish of best vector
        if time.time() < t0 + 50:
            hp, vp = _polish(best.copy(), t0 + 54)
            if vp < best_val:
                best, best_val = hp, vp
    except Exception:
        pass
    return [float(x) for x in best]