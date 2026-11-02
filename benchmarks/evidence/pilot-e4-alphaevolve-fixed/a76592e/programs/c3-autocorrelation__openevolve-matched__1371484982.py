import time
import numpy as np


class _Stop(Exception):
    pass


def _optimize(g0, temps, t0, deadline, state):
    from scipy.optimize import minimize
    n = len(g0)
    N = 1
    while N < 2 * n:
        N *= 2
    L = 2 * n - 1

    def fun(g, t):
        if time.time() - t0 > deadline:
            raise _Stop()
        s = g.sum()
        if abs(s) < 1e-12:
            return 1e6, np.zeros(n)
        h = g / s
        H = np.fft.rfft(h, N)
        c = np.fft.irfft(H * H, N)[:L] * (2 * n)
        mx = c.max()
        if mx < state['bs']:
            state['bs'] = mx
            state['best'] = h.copy()
        e = np.exp((c - mx) / t)
        Z = e.sum()
        f = mx + t * np.log(Z)
        w = e / Z
        W = np.fft.rfft(w, N)
        r = np.fft.irfft(W * np.conj(H), N)[:n]
        G = 4 * n * r
        grad = (G - np.dot(G, h)) / s
        return f, grad

    g = g0.copy()
    try:
        for t, it in temps:
            res = minimize(fun, g, args=(t,), jac=True, method="L-BFGS-B",
                           options={"maxiter": it, "maxcor": 30,
                                    "ftol": 1e-15, "gtol": 1e-12})
            g = res.x / res.x.sum()
    except _Stop:
        pass
    return g


def construct():
    t0 = time.time()
    n = 100
    idx = np.arange(n)
    h = 1.0 / np.sqrt(np.minimum(idx + 0.5, n - idx - 0.5))
    h = h / h.sum()
    fallback = h.copy()
    state = {'bs': 1e18, 'best': h.copy()}
    try:
        temps = [(0.1, 150), (0.03, 200), (0.01, 300), (0.003, 300),
                 (0.001, 400), (0.0003, 400), (0.0001, 300)]
        deadlines = [6, 14, 24, 36, 52]
        cur = h
        for k, dl in enumerate(deadlines):
            if k > 0:
                cur = np.repeat(state['best'], 2)
                cur = cur / cur.sum()
                state['bs'] = 1e18
            tt = temps if k == 0 else temps[2:]
            cur = _optimize(cur, tt, t0, dl, state)
            if time.time() - t0 > 53:
                break
        best = state['best']
        if not np.all(np.isfinite(best)) or abs(best.sum()) < 1e-12:
            best = fallback
    except Exception:
        best = state['best']
        if not np.all(np.isfinite(best)):
            best = fallback
    return [float(x) for x in best]