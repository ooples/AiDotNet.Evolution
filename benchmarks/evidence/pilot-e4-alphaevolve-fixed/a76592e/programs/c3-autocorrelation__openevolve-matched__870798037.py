import time
import numpy as np
from scipy.signal import fftconvolve
from scipy.optimize import minimize, linprog


def _score(h):
    h = np.asarray(h, dtype=float)
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = np.convolve(h, h) if len(h) < 64 else fftconvolve(h, h)
    return 2 * len(h) * abs(c.max()) / s ** 2


def _smooth(n, t0, budget, seed=0):
    rng = np.random.default_rng(seed)
    h = 1.0 + 0.3 * rng.standard_normal(n)
    h *= n / h.sum()

    def make(beta):
        def f(x):
            S = x.sum()
            if S < 1e-6:
                return 1e9, np.zeros_like(x)
            c = fftconvolve(x, x)
            m = c.max()
            e = np.exp(beta * (c - m))
            se = e.sum()
            L = m + np.log(se) / beta
            w = e / se
            g = 2 * fftconvolve(w, x[::-1], "valid")
            val = L / S ** 2
            grad = g / S ** 2 - 2 * L / S ** 3
            return val * 2 * n, grad * 2 * n
        return f

    best_h, best_s = None, float("inf")
    for b in (30, 100, 300, 1000, 3000, 10000, 30000, 100000):
        if time.time() - t0 > budget:
            break
        m = max(np.max(fftconvolve(h, h)), 1e-9)
        try:
            r = minimize(make(b / m), h, jac=True, method="L-BFGS-B",
                         options={"maxiter": 500, "maxfun": 800})
        except Exception:
            break
        x = r.x
        if not np.all(np.isfinite(x)) or x.sum() <= 1e-6:
            break
        h = x * (n / x.sum())
        sc = _score(h)
        if sc < best_s:
            best_s, best_h = sc, h.copy()
    return best_h, best_s


def _slp(h, t0, limit, r0=0.2, frac=0.4):
    n = len(h)
    h = np.asarray(h, float)
    h = h / h.sum()
    cur = _score(h)
    r = r0 / n
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    beq = [0.0]
    cost = np.zeros(n + 1)
    cost[n] = 1.0
    idx = np.arange(n)
    while time.time() - t0 < limit and r > 1e-11 / n:
        c = fftconvolve(h, h)
        m = c.max()
        ks = np.nonzero(c >= m - frac * abs(m))[0]
        j = ks[:, None] - idx[None, :]
        mask = (j >= 0) & (j < n)
        A = np.zeros((len(ks), n + 1))
        A[:, :n] = 2 * h[np.clip(j, 0, n - 1)] * mask
        A[:, n] = -1.0
        b = -c[ks]
        bounds = [(-r, r)] * n + [(None, None)]
        remaining = max(1.0, limit - (time.time() - t0))
        try:
            res = linprog(cost, A_ub=A, b_ub=b, A_eq=Aeq, b_eq=beq,
                          bounds=bounds, method="highs",
                          options={"time_limit": remaining})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        hn = h + res.x[:n]
        if hn.sum() <= 0:
            r *= 0.5
            continue
        hn = hn / hn.sum()
        sc = _score(hn)
        if sc < cur - 1e-13:
            h, cur = hn, sc
            r *= 1.3
        else:
            r *= 0.5
    return h, cur


def construct():
    t0 = time.time()
    const = [1.0] * 16
    best_h, best_s = None, float("inf")
    try:
        h, s = _smooth(100, t0, 5)
        if h is not None:
            best_h, best_s = h, s
            stages = [(100, 18, 0.2, 0.4), (200, 34, 0.08, 0.3),
                      (400, 53, 0.05, 0.2)]
            for n, lim, r0, fr in stages:
                if len(h) != n:
                    h = np.repeat(h, n // len(h))
                    h = h / h.sum()
                h2, s2 = _slp(h, t0, lim, r0=r0, frac=fr)
                if s2 < _score(h):
                    h = h2
                sc = _score(h)
                if sc < best_s:
                    best_h, best_s = h.copy(), sc
                if time.time() - t0 > 54:
                    break
    except Exception:
        pass
    if best_h is not None and best_s < _score(const):
        return [float(v) for v in best_h]
    return const