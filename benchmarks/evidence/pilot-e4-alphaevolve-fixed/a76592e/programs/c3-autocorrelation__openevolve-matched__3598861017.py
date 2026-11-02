import time
import numpy as np
from scipy.signal import fftconvolve


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    c = fftconvolve(h, h)
    return 2 * len(h) * abs(c.max()) / s ** 2


def _adam(n, budget, seed=0):
    t0 = time.time()
    rng = np.random.default_rng(seed)
    x = np.ones(n) + 0.3 * rng.standard_normal(n)
    x *= n / x.sum()
    best_h = np.ones(n)
    best_s = _score(best_h)
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2, lr = 0.9, 0.999, 0.01
    t = 0
    while True:
        frac = (time.time() - t0) / budget
        if frac > 1:
            break
        p = 8 * (64 ** frac)
        t += 1
        h = x
        s = h.sum()
        c = np.maximum(fftconvolve(h, h), 0)
        cm = c.max()
        if cm <= 0 or s <= 0:
            break
        r = c / cm
        lps = (np.sum(r ** p)) ** (1.0 / p)
        Lp = lps * cm
        g = r ** (p - 1) / lps ** (p - 1)
        d = 2 * fftconvolve(g, h[::-1])[n - 1:2 * n - 1]
        grad = d / s ** 2 - 2 * Lp / s ** 3
        grad = grad * (s ** 2 / (Lp + 1e-30)) / np.sqrt(n)
        m = b1 * m + (1 - b1) * grad
        v = b2 * v + (1 - b2) * grad * grad
        mh = m / (1 - b1 ** t)
        vh = v / (1 - b2 ** t)
        x = x - lr * (1 - 0.9 * frac) * mh / (np.sqrt(vh) + 1e-12)
        x = x * (n / x.sum())
        if t % 20 == 0:
            sc = _score(x)
            if sc < best_s:
                best_s, best_h = sc, x.copy()
    sc = _score(x)
    if sc < best_s:
        best_h = x.copy()
    return best_h


def _slp(h, deadline, thr=0.9):
    from scipy.optimize import linprog
    n = len(h)
    h = h * (n / h.sum())
    cur = _score(h)
    tr = 0.05 * np.abs(h).mean()
    idx = np.arange(n)
    obj = np.zeros(n + 1)
    obj[-1] = 1
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    while time.time() < deadline - 1.5 and tr > 1e-8:
        c = np.convolve(h, h)
        sel = np.where(c > thr * c.max())[0]
        hp = np.concatenate([np.zeros(n), h, np.zeros(n)])
        A = hp[n + sel[:, None] - idx[None, :]]
        Aub = np.hstack([2 * A, -np.ones((len(sel), 1))])
        bub = -c[sel]
        bounds = [(-tr, tr)] * n + [(None, None)]
        rem = max(1.0, deadline - time.time())
        try:
            res = linprog(obj, A_ub=Aub, b_ub=bub, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": rem})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            tr *= 0.5
            continue
        hn = h + res.x[:n]
        if hn.sum() <= 1e-9:
            tr *= 0.5
            continue
        hn = hn * (n / hn.sum())
        s2 = _score(hn)
        if s2 < cur:
            h, cur = hn, s2
            tr *= 1.3
        else:
            tr *= 0.5
            if tr < 1e-2 and thr > 0.5:
                thr = max(0.5, thr - 0.05)
    return h


def construct():
    t0 = time.time()
    n = 400
    best_h = _adam(n, 10.0)
    best_s = _score(best_h)
    try:
        h2 = _slp(best_h.copy(), t0 + 54.0)
        s2 = _score(h2)
        if s2 < best_s:
            best_h = h2
    except Exception:
        pass
    return [float(a) for a in best_h]