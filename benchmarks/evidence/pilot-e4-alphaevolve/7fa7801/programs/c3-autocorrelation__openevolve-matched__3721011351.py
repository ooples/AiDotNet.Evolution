import time
import numpy as np


def _score(h):
    s = h.sum()
    if abs(s) < 1e-9:
        return float("inf")
    c = np.convolve(h, h)
    return 2 * len(h) * np.max(np.abs(c)) / s ** 2


def _adam(n, seed, total_time):
    rng = np.random.default_rng(seed)
    h = 1.0 + 0.3 * rng.standard_normal(n)
    h = h * (n / h.sum())
    best_h = np.ones(n)
    best_v = _score(best_h)
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2 = 0.9, 0.999
    step = 0
    t1 = time.time()
    while True:
        el = time.time() - t1
        if el >= total_time:
            break
        frac = el / total_time
        p = 8.0 * (64.0 ** frac)
        lr = 0.01 * (1 - frac) + 0.0003
        c = np.convolve(h, h)
        a = np.abs(c)
        mx = a.max()
        if mx <= 0:
            break
        r = a / mx
        rp = r ** (p - 1)
        sr = np.sum(r * rp)
        g = rp * np.sign(c) * sr ** (1.0 / p - 1.0)
        grad = 2 * np.correlate(g, h, "valid")
        grad = grad - grad.mean()
        step += 1
        m = b1 * m + (1 - b1) * grad
        v = b2 * v + (1 - b2) * grad * grad
        h = h - lr * (m / (1 - b1 ** step)) / (np.sqrt(v / (1 - b2 ** step)) + 1e-12)
        s = h.sum()
        if abs(s) < 1e-9:
            break
        h = h * (n / s)
        val = _score(h)
        if val < best_v:
            best_v = val
            best_h = h.copy()
    return best_h, best_v


def _slp(h, deadline):
    from scipy.optimize import linprog
    n = len(h)
    h = np.clip(h, 0, None)
    h = h * (n / h.sum())
    best = _score(h)
    delta = 0.05
    k = np.arange(2 * n - 1)[:, None]
    i = np.arange(n)[None, :]
    idx = k - i
    valid = (idx >= 0) & (idx < n)
    idx = np.clip(idx, 0, n - 1)
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    beq = [0.0]
    while time.time() < deadline and delta > 1e-8:
        c0 = np.convolve(h, h)
        rows = np.where(c0 >= 0.6 * c0.max())[0]
        M = np.where(valid[rows], h[idx[rows]], 0.0)
        A = np.hstack([2 * M, -np.ones((len(rows), 1))])
        b = -c0[rows]
        lo = np.maximum(-delta, -h)
        bounds = [(lo[j], delta) for j in range(n)] + [(None, None)]
        remaining = deadline - time.time()
        if remaining < 0.5:
            break
        try:
            res = linprog(cost, A_ub=A, b_ub=b, A_eq=Aeq, b_eq=beq,
                          bounds=bounds, method="highs",
                          options={"time_limit": float(remaining)})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = np.clip(h + res.x[:n], 0, None)
        if hn.sum() <= 0:
            delta *= 0.5
            continue
        hn = hn * (n / hn.sum())
        val = _score(hn)
        if val < best:
            best = val
            h = hn
            delta = min(delta * 1.3, 0.2)
        else:
            delta *= 0.5
    return h


def construct():
    t0 = time.time()
    n = 400
    best_h = np.ones(n)
    best_v = _score(best_h)
    for seed in (1, 2, 3):
        try:
            h, v = _adam(n, seed, 4.0)
            if v < best_v:
                best_h, best_v = h, v
        except Exception:
            pass
    try:
        h2 = _slp(best_h, t0 + 52.0)
        if _score(h2) < best_v:
            best_h = h2
    except Exception:
        pass
    return [float(x) for x in best_h]