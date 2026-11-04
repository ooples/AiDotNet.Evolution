import time
import numpy as np


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    return 2 * len(h) * abs(np.max(np.convolve(h, h))) / s ** 2


def _pnorm_stage(x, p, maxiter):
    from scipy.optimize import minimize
    from scipy.signal import fftconvolve
    n = len(x)

    def f(x):
        h = x * x
        s = h.sum()
        c = np.maximum(fftconvolve(h, h), 1e-300)
        M = c.max()
        q = c / M
        norm = M * (q ** p).sum() ** (1.0 / p)
        g = (c / norm) ** (p - 1)
        dh = 2 * fftconvolve(g, h[::-1])[n - 1:2 * n - 1]
        val = 2 * n * norm / s ** 2
        gh = 2 * n * (dh / s ** 2 - 2 * norm / s ** 3)
        return val, gh * 2 * x

    res = minimize(f, x, jac=True, method="L-BFGS-B",
                   options={"maxiter": maxiter, "maxfun": maxiter * 2})
    return res.x


def _lp(h, deadline, r0):
    from scipy.optimize import linprog
    n = len(h)
    m = 2 * n - 1
    k = np.arange(m)[:, None]
    j = np.arange(n)[None, :]
    idx = k - j
    mask = (idx >= 0) & (idx < n)
    idxc = np.clip(idx, 0, n - 1)
    cost = np.zeros(n + 1)
    cost[n] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    best = h * (n / h.sum())
    best_s = _score(best)
    r = r0
    A = np.zeros((m, n + 1))
    A[:, n] = -1.0
    last = 0.0
    while time.time() + last * 1.5 < deadline and r > 1e-8:
        ts = time.time()
        c0 = np.convolve(best, best)
        A[:, :n] = np.where(mask, best[idxc], 0.0) * 2.0
        bounds = [(-r, r)] * n + [(None, None)]
        try:
            res = linprog(cost, A_ub=A, b_ub=-c0, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs")
        except Exception:
            break
        last = time.time() - ts
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        hn = best + res.x[:n]
        if hn.sum() <= 1e-9:
            r *= 0.5
            continue
        hn = hn * (n / hn.sum())
        s = _score(hn)
        if s < best_s - 1e-13:
            gain = best_s - s
            best, best_s = hn, s
            if gain < 1e-9:
                r *= 0.5
            else:
                r = min(r * 1.2, 0.5)
        else:
            r *= 0.5
    return best, best_s


def construct():
    t0 = time.time()
    n = 150
    xs = (np.arange(n) + 0.5) / n
    h = 1.0 / np.sqrt(xs * (1 - xs) + 0.02)
    h = h * (n / h.sum())
    gbest, gscore = h.copy(), _score(h)
    try:
        x = np.sqrt(h)
        for p in (8, 32, 128, 512, 1024):
            x = _pnorm_stage(x, p, 800)
        h = x * x
        h = h * (len(h) / h.sum())
        s0 = _score(h)
        if s0 < gscore:
            gbest, gscore = h.copy(), s0
        h = np.repeat(gbest, 2)
        h = h * (len(h) / h.sum())
        s0 = _score(h)
        if s0 < gscore:
            gbest, gscore = h.copy(), s0
        x = np.sqrt(np.maximum(h, 1e-12))
        for p in (128, 512, 1024, 2048, 4096, 8192):
            if time.time() - t0 > 20:
                break
            x = _pnorm_stage(x, p, 900)
            hh = x * x
            hh = hh * (len(hh) / hh.sum())
            s = _score(hh)
            if s < gscore:
                gbest, gscore = hh.copy(), s
        h, s = _lp(gbest, t0 + 55, 0.02)
        if s < gscore:
            gbest, gscore = h.copy(), s
    except Exception:
        pass
    return [float(v) for v in gbest]