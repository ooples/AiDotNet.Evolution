import time
import numpy as np
from scipy import sparse


def _val(h):
    h = np.asarray(h, dtype=float)
    return 2 * len(h) * np.max(np.convolve(h, h)) / np.sum(h) ** 2


def _refine(h, t_end, delta0, rng):
    from scipy.optimize import linprog
    n = len(h)
    m = 2 * n - 1
    h = h / h.sum()
    cur = np.max(np.convolve(h, h))
    best, best_c = h.copy(), cur
    c = np.zeros(n + 1)
    c[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    beq = [0.0]
    ii = np.arange(n)
    rows = (ii[:, None] + ii[None, :]).ravel()
    cols = np.repeat(ii, n)
    last = np.arange(m)
    allrows = np.concatenate([rows, last])
    allcols = np.concatenate([cols, np.full(m, n)])
    negone = -np.ones(m)
    delta = delta0 / n
    while True:
        rem = t_end - time.time()
        if rem < 0.5:
            break
        if delta < 1e-11 / n:
            h = best * (1 + 0.02 * rng.standard_normal(n))
            h = h / h.sum()
            cur = np.max(np.convolve(h, h))
            delta = 0.05 / n
            continue
        conv = np.convolve(h, h)
        vals = np.concatenate([np.tile(2 * h, n), negone])
        A = sparse.coo_matrix((vals, (allrows, allcols)),
                              shape=(m, n + 1)).tocsr()
        bounds = [(-delta, delta)] * n + [(None, None)]
        try:
            res = linprog(c, A_ub=A, b_ub=-conv, A_eq=Aeq, b_eq=beq,
                          bounds=bounds, method="highs",
                          options={"time_limit": max(0.5, min(8.0, rem))})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = h + res.x[:n]
        s = hn.sum()
        if abs(s) < 1e-12:
            delta *= 0.5
            continue
        hn = hn / s
        new = np.max(np.convolve(hn, hn))
        if new < cur:
            h, cur = hn, new
            delta *= 1.3
            if cur < best_c:
                best, best_c = h.copy(), cur
        else:
            delta *= 0.5
    return best


def construct():
    t0 = time.time()
    n = 128
    best = np.ones(n) / n
    best_v = _val(best)
    try:
        rng = np.random.default_rng(0)
        h = best * (1 + 0.2 * rng.standard_normal(n))
        h = h / h.sum()
        h = _refine(h, t0 + 20, 0.3, rng)
        v = _val(h)
        if np.isfinite(v) and v < best_v:
            best, best_v = h.copy(), v
        h2 = np.repeat(best, 2)
        h2 = h2 / h2.sum()
        h2 = _refine(h2, t0 + 46, 0.05, rng)
        v = _val(h2)
        if np.isfinite(v) and v < best_v:
            best, best_v = h2.copy(), v
    except Exception:
        pass
    v = _val(best)
    if not np.isfinite(v) or v > 2.0:
        best = np.ones(128)
    return [float(x) for x in best]