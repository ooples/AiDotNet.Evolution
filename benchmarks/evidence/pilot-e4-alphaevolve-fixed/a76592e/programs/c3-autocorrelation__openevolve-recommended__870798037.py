import time
import numpy as np
from scipy.optimize import minimize, linprog
from scipy.signal import fftconvolve


def _value(h):
    h = np.asarray(h, float)
    return 2 * len(h) * np.max(np.convolve(h, h)) / h.sum() ** 2


def _make(beta, n, deadline, square):
    big = n >= 300

    def cv(a, b):
        return fftconvolve(a, b) if big else np.convolve(a, b)

    def f(x):
        if time.time() > deadline:
            raise TimeoutError
        h = x * x if square else x
        s = h.sum()
        conv = cv(h, h)
        v = 2 * n * conv / s ** 2
        m = v.max()
        e = np.exp(beta * (v - m))
        Z = e.sum()
        w = e / Z
        val = m + np.log(Z) / beta
        corr = cv(w, h[::-1])[n - 1:2 * n - 1]
        g = 2 * n * (2 * corr / s ** 2 - 2 * np.dot(w, conv) / s ** 3)
        if square:
            g = g * 2 * x
        return val, g
    return f


def _opt(x, betas, square, maxiter, deadline):
    n = len(x)
    for beta in betas:
        if time.time() > deadline:
            break
        try:
            r = minimize(_make(beta, n, deadline, square), x, jac=True,
                         method="L-BFGS-B", options={"maxiter": maxiter})
            if np.all(np.isfinite(r.x)):
                x = r.x
        except Exception:
            break
    return x


def _slp(h, deadline):
    h = np.asarray(h, float)
    h = h / h.sum()
    n = len(h)
    best_h = h
    best_v = _value(h)
    delta = 0.02 / n
    m = 2 * n - 1
    cost = np.zeros(n + 1)
    cost[n] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    while time.time() < deadline - 1.5 and delta > 1e-10 / n:
        try:
            c = np.convolve(best_h, best_h)
            M = np.zeros((m, n + 1))
            for i in range(n):
                M[i:i + n, i] = 2 * best_h
            M[:, n] = -1.0
            bounds = [(-delta, delta)] * n + [(None, None)]
            r = linprog(cost, A_ub=M, b_ub=-c, A_eq=Aeq, b_eq=[0.0],
                        bounds=bounds, method="highs",
                        options={"time_limit": max(0.5, deadline - time.time() - 1.0)})
            if r.status != 0 or r.x is None:
                delta *= 0.5
                continue
            cand = best_h + r.x[:n]
            if abs(cand.sum()) < 1e-12:
                delta *= 0.5
                continue
            cand = cand / cand.sum()
            v = _value(cand)
            if np.isfinite(v) and v < best_v:
                best_v = v
                best_h = cand
            else:
                delta *= 0.5
        except Exception:
            break
    return best_h, best_v


def construct():
    t0 = time.time()
    best = np.ones(120)
    best_v = _value(best)

    def consider(h):
        nonlocal best, best_v
        h = np.asarray(h, float)
        if np.all(np.isfinite(h)) and abs(h.sum()) > 1e-9:
            v = _value(h)
            if np.isfinite(v) and v < best_v:
                best_v = v
                best = h.copy()

    # Phase 1: random restarts at small n
    n0 = 30
    rng = np.random.default_rng(1)
    cands = []
    r_deadline = t0 + 7.0
    k = 0
    while time.time() < r_deadline and k < 60:
        x = np.sqrt(1.0 + 0.5 * rng.random(n0))
        x = _opt(x, [20, 100, 500, 2000], True, 400, r_deadline)
        h = x * x
        if np.all(np.isfinite(h)) and h.sum() > 1e-9:
            cands.append((_value(h), h))
            consider(h)
        k += 1
    if cands:
        cands.sort(key=lambda t: t[0])
        h = cands[0][1].copy()
    else:
        h = np.ones(n0)

    # Phase 2: upsample and refine
    deadline = t0 + 34.0
    betas = [100, 300, 1000, 3000, 10000, 30000]
    x = h
    for stage in range(5):
        x = np.repeat(x, 2)
        n = len(x)
        maxiter = 1000 if n <= 240 else 600
        x = _opt(x, betas, False, maxiter, deadline)
        consider(x)
        if time.time() > deadline:
            break

    # Phase 3: SLP polish
    try:
        base = best
        if len(base) > 700:
            base = best
        h2, v2 = _slp(base, t0 + 50.0)
        if np.all(np.isfinite(h2)) and v2 < best_v:
            best = h2
            best_v = v2
    except Exception:
        pass

    return [float(a) for a in best]