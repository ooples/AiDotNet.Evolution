import time
import numpy as np
from scipy.optimize import minimize, linprog


def _score(h):
    h = np.asarray(h, dtype=float)
    s = h.sum()
    if abs(s) < 1e-12 or not np.all(np.isfinite(h)):
        return float("inf")
    return 2 * len(h) * abs(np.max(np.convolve(h, h))) / s ** 2


def _softmax(x, betas, t0, limit, maxiter):
    n = len(x)
    x = x / x.sum()
    best, best_v = x.copy(), _score(x)

    def make(beta):
        def fg(x):
            S = x.sum()
            if abs(S) < 1e-9:
                return 1e9, np.zeros_like(x)
            c = np.convolve(x, x)
            z = beta * c / S ** 2
            m = z.max()
            e = np.exp(z - m)
            se = e.sum()
            w = e / se
            f = 2.0 * n * (m + np.log(se)) / beta
            corr = np.correlate(w, x, "valid")
            g = 2.0 * n * (2.0 * corr / S ** 2 - 2.0 * (w @ c) / S ** 3)
            return f, g
        return fg

    for b in betas:
        if time.time() - t0 > limit:
            break
        try:
            res = minimize(make(b * n / 2.0), x, jac=True, method="L-BFGS-B",
                           options={"maxiter": maxiter,
                                    "maxfun": int(maxiter * 1.4)})
        except Exception:
            break
        if not np.all(np.isfinite(res.x)) or abs(res.x.sum()) < 1e-9:
            break
        x = res.x / res.x.sum()
        v = _score(x)
        if np.isfinite(v) and v < best_v:
            best, best_v = x.copy(), v
    return best, best_v


def _slp(h, t0, deadline, r0):
    n = len(h)
    m = 2 * n - 1
    best = h / h.sum()
    best_val = _score(best)
    r = r0 / n
    cost = np.zeros(n + 1)
    cost[n] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    A = np.zeros((m, n + 1))
    A[:, n] = -1.0
    while time.time() - t0 < deadline - 1.5 and r > 1e-11 / n:
        c0 = np.convolve(best, best)
        for j in range(n):
            A[j:j + n, j] = 2 * best
        bounds = [(-r, r)] * n + [(None, None)]
        remaining = max(1.0, deadline - (time.time() - t0))
        try:
            res = linprog(cost, A_ub=A, b_ub=-c0, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": float(remaining)})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        cand = best + res.x[:n]
        s = cand.sum()
        if abs(s) < 1e-12:
            r *= 0.5
            continue
        cand = cand / s
        v = _score(cand)
        if v < best_val - 1e-14:
            best, best_val = cand, v
            r *= 1.3
        else:
            r *= 0.5
    return best, best_val


def construct():
    t0 = time.time()
    best = np.ones(16)
    best_val = _score(best)
    try:
        rng = np.random.default_rng(0)
        x0 = np.ones(200) + 0.1 * rng.standard_normal(200)
        h, v = _softmax(x0, [5, 20, 80, 300, 1000, 3000, 10000, 30000],
                        t0, 6.0, 1500)
        if np.isfinite(v) and v < best_val:
            best, best_val = h, v
        up = np.repeat(best, 3) / 3.0
        h, v = _softmax(up, [1000, 3000, 10000, 30000, 100000],
                        t0, 28.0, 4000)
        if np.isfinite(v) and v < best_val:
            best, best_val = h, v
        h, v = _slp(best, t0, 54.0, 0.01)
        if np.isfinite(v) and v < best_val:
            best, best_val = h, v
    except Exception:
        pass
    return [float(v) for v in best]