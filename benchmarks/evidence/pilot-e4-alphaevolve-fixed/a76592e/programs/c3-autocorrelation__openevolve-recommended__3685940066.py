import time
import numpy as np


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    return 2 * len(h) * abs(np.convolve(h, h).max()) / s**2


def _smooth(h, deadline):
    from scipy.optimize import minimize
    n = len(h)

    def f(x, tau):
        s = x.sum()
        c = np.convolve(x, x)
        cn = c * (2 * n) / s**2
        m = cn.max()
        e = np.exp((cn - m) / tau)
        Z = e.sum()
        val = m + tau * np.log(Z)
        w = e / Z
        corr = np.correlate(w, x, "valid")
        g = (2 * n) * (2 * corr / s**2 - 2 * (w * c).sum() / s**3)
        return val, g

    x = h.copy()
    for tau in [0.05, 0.02, 0.01, 0.005, 0.002, 0.001]:
        if time.time() > deadline:
            break
        try:
            res = minimize(f, x, args=(tau,), jac=True, method="L-BFGS-B",
                           options={"maxiter": 400})
            if np.all(np.isfinite(res.x)) and abs(res.x.sum()) > 1e-9:
                x = res.x / res.x.sum()
        except Exception:
            break
    return x


def _refine(h, deadline, frac=0.85):
    from scipy.optimize import linprog

    n = len(h)
    h = h / h.sum()
    cur = _score(h)
    r = 0.2 / n
    cost = np.zeros(n + 1)
    cost[-1] = 1
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1
    last = 0.0
    jj = np.arange(n)
    while r > 1e-11:
        now = time.time()
        if now + last * 1.5 + 0.05 >= deadline:
            break
        c = np.convolve(h, h)
        cm = c.max()
        idx = np.where(c >= cm - (1 - frac) * abs(cm))[0]
        kj = idx[:, None] - jj[None, :]
        valid = (kj >= 0) & (kj < n)
        A = np.where(valid, h[np.clip(kj, 0, n - 1)], 0.0)
        A_ub = np.hstack([2 * A, -np.ones((len(idx), 1))])
        bounds = [(-r, r)] * n + [(None, None)]
        try:
            res = linprog(cost, A_ub=A_ub, b_ub=-c[idx], A_eq=A_eq,
                          b_eq=[0], bounds=bounds, method="highs",
                          options={"time_limit": max(0.1, deadline - now)})
        except Exception:
            break
        last = time.time() - now
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        hn = h + res.x[:n]
        s = hn.sum()
        if abs(s) < 1e-12:
            r *= 0.5
            continue
        hn = hn / s
        sc = _score(hn)
        if sc < cur:
            h, cur = hn, sc
            r *= 1.3
        else:
            r *= 0.5
    return h, cur


def construct():
    best = np.ones(2)
    best_sc = _score(best)
    try:
        t0 = time.time()
        rng = np.random.default_rng(0)
        n0 = 100
        cand = None
        cand_sc = float("inf")
        for t in range(3):
            h0 = 1 + 0.3 * rng.random(n0)
            h0 = h0 / h0.sum()
            hs = _smooth(h0, t0 + 3 * (t + 1))
            sc = _score(hs)
            if sc < cand_sc:
                cand, cand_sc = hs, sc
        h = cand
        if cand_sc < best_sc:
            best, best_sc = h.copy(), cand_sc
        for n, d in zip([100, 200, 400], [14, 30, 52]):
            if len(h) != n:
                h = np.repeat(h, n // len(h))
                h = h / h.sum()
            h, sc = _refine(h, t0 + d)
            if sc < best_sc:
                best, best_sc = h.copy(), sc
    except Exception:
        pass
    return [float(x) for x in best]