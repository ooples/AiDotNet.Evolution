import time
import numpy as np


def _value(h):
    s = h.sum()
    if abs(s) < 1e-9:
        return float("inf")
    c = np.convolve(h, h)
    return 2.0 * len(h) * np.max(np.abs(c)) / (s * s)


def _adam(n, budget):
    rng = np.random.default_rng(0)
    x = (np.arange(n) + 0.5) / n
    h = 1.0 / np.sqrt(0.02 + x * (1 - x))
    h = h * (1 + 0.02 * rng.standard_normal(n))
    h = h * n / h.sum()
    best_h, best_v = h.copy(), _value(h)
    m = np.zeros(n)
    s2 = np.zeros(n)
    b1, b2 = 0.9, 0.999
    lr = 0.01
    it = 0
    ts = time.time()
    while time.time() - ts < budget:
        it += 1
        frac = min(1.0, (time.time() - ts) / budget)
        p = 8.0 * (64.0 ** frac)
        S = h.sum()
        c = np.convolve(h, h)
        mx = np.max(np.abs(c))
        if mx == 0:
            break
        w = np.abs(c) / mx
        wp = w ** (p - 1)
        denom = (wp * w).sum()
        g = np.sign(c) * wp / (mx * denom)
        gh = 2.0 * np.correlate(g, h, mode="valid")
        gh = (gh - 2.0 / S) * S
        cur_lr = lr * (1 - 0.9 * frac)
        m = b1 * m + (1 - b1) * gh
        s2 = b2 * s2 + (1 - b2) * gh * gh
        mh = m / (1 - b1 ** it)
        sh = s2 / (1 - b2 ** it)
        h = h - cur_lr * mh / (np.sqrt(sh) + 1e-12)
        sm = h.sum()
        if sm <= 1e-9:
            h = best_h.copy()
            continue
        h = h * n / sm
        if it % 20 == 0:
            v = _value(h)
            if v < best_v:
                best_v, best_h = v, h.copy()
    v = _value(h)
    if v < best_v:
        best_v, best_h = v, h.copy()
    return best_h, best_v


def _slp(h, t_end):
    from scipy.optimize import linprog
    n = len(h)
    K = 2 * n - 1
    h = h * n / h.sum()
    best_v = _value(h)
    delta = 0.05
    idx = np.arange(K)[:, None] - np.arange(n)[None, :]
    valid = (idx >= 0) & (idx < n)
    idxc = np.clip(idx, 0, n - 1)
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1.0
    ones = -np.ones((K, 1))
    while time.time() < t_end - 1.0 and delta > 1e-8:
        c = np.convolve(h, h)
        A = 2.0 * np.where(valid, h[idxc], 0.0)
        A_ub = np.vstack([np.hstack([A, ones]), np.hstack([-A, ones])])
        b_ub = np.concatenate([-c, c])
        bounds = [(-delta, delta)] * n + [(None, None)]
        remaining = max(1.0, t_end - time.time() - 0.5)
        try:
            res = linprog(cost, A_ub=A_ub, b_ub=b_ub, A_eq=A_eq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": remaining})
        except Exception:
            break
        if res.x is None or res.status not in (0, 1):
            delta *= 0.5
            continue
        hn = h + res.x[:n]
        v = _value(hn)
        if v < best_v:
            best_v, h = v, hn
            delta = min(0.5, delta * 1.3)
        else:
            delta *= 0.5
    return h, best_v


def construct():
    t0 = time.time()
    n = 400
    h, v = _adam(n, 10.0)
    best_h, best_v = h.copy(), v
    try:
        h2, v2 = _slp(h, t0 + 53.0)
        if v2 < best_v:
            best_h, best_v = h2, v2
    except Exception:
        pass
    return [float(a) for a in best_h]