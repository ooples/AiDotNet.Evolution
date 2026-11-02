import time
import numpy as np
from scipy.optimize import minimize, linprog
from scipy.signal import fftconvolve


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = fftconvolve(h, h)
    return 2 * len(h) * abs(c.max()) / s ** 2


def construct():
    t0 = time.time()
    n = 400
    rng = np.random.default_rng(0)
    x = rng.uniform(0.5, 1.5, n)

    def fg(x, beta):
        h = x * x
        s = h.sum()
        c = fftconvolve(h, h)
        r = n * c / s ** 2
        m = r.max()
        e = np.exp(beta * (r - m))
        z = e.sum()
        L = m + np.log(z) / beta
        w = e / z
        gh = n * (2 * fftconvolve(w, h[::-1], mode="valid") / s ** 2
                  - 2 * np.dot(w, c) / s ** 3)
        return L, gh * 2 * x

    best = np.ones(16)
    best_val = _score(best)

    for beta in [10, 30, 100, 300, 1000, 3000, 10000, 30000]:
        if time.time() - t0 > 14:
            break
        try:
            res = minimize(fg, x, args=(beta,), jac=True, method="L-BFGS-B",
                           options={"maxiter": 500})
            x = res.x
        except Exception:
            break
        h = x * x
        v = _score(h)
        if np.isfinite(v) and v < best_val:
            best_val = v
            best = h.copy()

    try:
        if len(best) == n:
            h = best * (n / best.sum())
            cur = _score(h)
            r = 0.1
            A_eq = np.zeros((1, n + 1))
            A_eq[0, :n] = 1.0
            cvec = np.zeros(n + 1)
            cvec[-1] = 1.0
            cols = np.arange(n)
            while time.time() - t0 < 50 and r > 1e-9:
                c = fftconvolve(h, h)
                m = c.max()
                sel = np.where(c > 0.9 * m)[0]
                idx = sel[:, None] - cols[None, :]
                mask = (idx >= 0) & (idx < n)
                H = np.where(mask, h[np.clip(idx, 0, n - 1)], 0.0)
                A_ub = np.hstack([2 * H, -np.ones((len(sel), 1))])
                rad = r * h.mean()
                bounds = [(-rad, rad)] * n + [(None, None)]
                tl = max(1.0, min(8.0, 53 - (time.time() - t0)))
                res = linprog(cvec, A_ub=A_ub, b_ub=-c[sel], A_eq=A_eq,
                              b_eq=[0.0], bounds=bounds, method="highs",
                              options={"time_limit": tl})
                if res.status != 0 or res.x is None:
                    r *= 0.5
                    continue
                hn = h + res.x[:n]
                v = _score(hn)
                if v < cur:
                    h = hn
                    cur = v
                    r *= 1.4
                    if v < best_val:
                        best_val = v
                        best = h.copy()
                else:
                    r *= 0.5
    except Exception:
        pass

    return [float(v) for v in best]