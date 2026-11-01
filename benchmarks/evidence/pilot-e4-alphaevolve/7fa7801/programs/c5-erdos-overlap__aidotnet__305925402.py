import time
import numpy as np


def _score(h):
    n = len(h)
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _fix_sum(h):
    n = len(h)
    target = n / 2
    h = np.clip(h, 0, 1)
    for _ in range(200):
        diff = target - h.sum()
        if abs(diff) < 1e-12:
            break
        free = (h < 1) if diff > 0 else (h > 0)
        if not free.any():
            break
        h = np.clip(h + diff / free.sum() * free, 0, 1)
    return h


def _grad_stage(n, t_limit, seed):
    rng = np.random.default_rng(seed)
    h = _fix_sum(0.5 + 0.05 * rng.standard_normal(n))
    best_h, best = h.copy(), _score(h)
    t0 = time.time()
    steps = 4000
    m = np.zeros(n)
    v = np.zeros(n)
    for it in range(1, steps + 1):
        if time.time() - t0 > t_limit:
            break
        frac = it / steps
        beta = 100 * (50 ** frac)
        lr = 0.02 * (1 - frac) + 0.0005
        g = 1 - h
        c = np.correlate(h, g, mode="full") / n
        w = np.exp(beta * (c - c.max()))
        w /= w.sum()
        t1 = np.convolve(w, g)[n - 1:2 * n - 1]
        t2 = np.convolve(w[::-1], h)[n - 1:2 * n - 1]
        grad = t1 - t2
        grad -= grad.mean()
        m = 0.9 * m + 0.1 * grad
        v = 0.999 * v + 0.001 * grad * grad
        step = lr * (m / (1 - 0.9 ** it)) / (np.sqrt(v / (1 - 0.999 ** it)) + 1e-12)
        h = _fix_sum(h - step)
        if it % 20 == 0:
            s = _score(h)
            if s < best:
                best, best_h = s, h.copy()
    s = _score(h)
    if s < best:
        best, best_h = s, h.copy()
    return best_h, best


def _slp(h, best, t_limit, r0=0.02):
    from scipy.optimize import linprog
    n = len(h)
    t0 = time.time()
    r = r0
    idx = np.arange(n)
    m = 2 * n - 1
    D = (np.arange(m) - (n - 1))[:, None]
    A_idx = idx[None, :] - D
    B_idx = idx[None, :] + D
    okA = (A_idx >= 0) & (A_idx < n)
    okB = (B_idx >= 0) & (B_idx < n)
    A_c = np.clip(A_idx, 0, n - 1)
    B_c = np.clip(B_idx, 0, n - 1)
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1
    cost = np.zeros(n + 1)
    cost[-1] = 1
    while time.time() - t0 < t_limit and r > 1e-7:
        g = 1 - h
        c = np.correlate(h, g, mode="full")
        J = np.where(okA, g[A_c], 0.0) - np.where(okB, h[B_c], 0.0)
        lo = np.maximum(-r, -h)
        hi = np.minimum(r, 1 - h)
        A = np.hstack([J, -np.ones((m, 1))])
        bounds = list(zip(lo, hi)) + [(None, None)]
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq, b_eq=[0],
                          bounds=bounds, method="highs")
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        hn = _fix_sum(h + res.x[:n])
        s = _score(hn)
        if s < best - 1e-12:
            h, best = hn, s
            r = min(r * 1.3, 0.2)
        else:
            r *= 0.5
    return h, best


def construct():
    n = 200
    t0 = time.time()
    best_h = np.full(n, 0.5)
    best = _score(best_h)
    try:
        h, s = _grad_stage(n, 8, 0)
        if s < best:
            best_h, best = h, s
    except Exception:
        pass
    try:
        h, s = _slp(best_h.copy(), best, 18 - (time.time() - t0))
        if s < best:
            best_h, best = h, s
    except Exception:
        pass
    # refine at double resolution
    try:
        h2 = np.repeat(best_h, 2)
        h2 = _fix_sum(h2)
        s2 = _score(h2)
        remaining = 40 - (time.time() - t0)
        if remaining > 3:
            h2, s2 = _slp(h2, s2, remaining, r0=0.01)
        if s2 < best and abs(h2.sum() - len(h2) / 2) <= 1e-9 * len(h2):
            best_h, best = h2, s2
    except Exception:
        pass
    n = len(best_h)
    if best >= 0.5 or abs(best_h.sum() - n / 2) > 1e-9 * n:
        return [0.5] * 16
    return [float(x) for x in best_h]