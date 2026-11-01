import time
import numpy as np


def _value(h):
    s = h.sum()
    if abs(s) < 1e-9:
        return float("inf")
    n = len(h)
    return 2 * n * np.max(np.abs(np.convolve(h, h))) / s ** 2


def _adam(n, budget, seed):
    rng = np.random.default_rng(seed)
    x = (np.arange(n) + 0.5) / n
    h0 = x ** -0.5
    if seed > 0:
        h0 = h0 * (1 + 0.8 * rng.standard_normal(n))
    h0 = h0 / h0.sum()
    h = h0.copy()
    best, best_v = h.copy(), _value(h)
    m = np.zeros(n)
    v = np.zeros(n)
    lr = 2e-3 * np.abs(h).mean()
    b1, b2 = 0.9, 0.999
    it = 0
    ts = time.time()
    while time.time() - ts < budget:
        it += 1
        frac = min(1.0, (time.time() - ts) / budget)
        beta = 30.0 * (200.0 ** frac)
        s = h.sum()
        hn = h / s
        c = np.convolve(hn, hn) * (2 * n)
        a = np.abs(c)
        w = np.exp(beta * (a - a.max()))
        w /= w.sum()
        ws = w * np.sign(c)
        g_hn = 2 * (2 * n) * np.correlate(ws, hn, "valid")
        g = (g_hn - g_hn @ hn) / s
        m = b1 * m + (1 - b1) * g
        v = b2 * v + (1 - b2) * g * g
        mh = m / (1 - b1 ** it)
        vh = v / (1 - b2 ** it)
        h = h - lr * (1 - 0.9 * frac) * mh / (np.sqrt(vh) + 1e-12)
        if it % 20 == 0:
            val = _value(h)
            if val < best_v:
                best_v, best = val, h.copy()
    val = _value(h)
    if val < best_v:
        best, best_v = h.copy(), val
    return best / best.sum(), best_v


def _slp(h, deadline, delta0):
    from scipy.optimize import linprog
    n = len(h)
    h = h / h.sum()
    cur = _value(h)
    K = np.arange(2 * n - 1)[:, None] - np.arange(n)[None, :]
    valid = (K >= 0) & (K < n)
    Kc = np.clip(K, 0, n - 1)
    scale = np.abs(h).mean()
    delta = delta0 * scale
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1.0
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    one = np.ones((2 * n - 1, 1))
    while delta > 1e-9 * scale:
        remaining = deadline - time.time()
        if remaining < 1.0:
            break
        A = np.where(valid, h[Kc], 0.0) * (2.0 * 2 * n)
        c0 = np.convolve(h, h) * (2 * n)
        A_ub = np.vstack([np.hstack([A, -one]), np.hstack([-A, -one])])
        b_ub = np.concatenate([c0, -c0])
        bounds = [(h[i] - delta, h[i] + delta) for i in range(n)]
        bounds.append((None, None))
        try:
            res = linprog(cost, A_ub=A_ub, b_ub=b_ub, A_eq=A_eq, b_eq=[1.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": float(remaining)})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = res.x[:n]
        if abs(hn.sum()) < 1e-9:
            delta *= 0.5
            continue
        hn = hn / hn.sum()
        val = _value(hn)
        if val < cur - 1e-12:
            h, cur = hn, val
            delta *= 1.4
        else:
            delta *= 0.5
    return h, cur


def construct():
    t0 = time.time()
    best = np.ones(16)
    best_v = _value(best)
    try:
        cand, cv = None, float("inf")
        for seed in range(5):
            h, v = _adam(100, 3.0, seed)
            if v < cv:
                cand, cv = h, v
        if cv < best_v:
            best, best_v = cand, cv
        try:
            h, v = _slp(best, t0 + 17.0, 0.5)
            if v < best_v:
                best, best_v = h, v
        except Exception:
            pass
        for deadline, d0 in ((t0 + 35.0, 0.2), (t0 + 56.0, 0.1)):
            try:
                h2 = np.repeat(best, 2)
                h2 = h2 / h2.sum()
                h3, v3 = _slp(h2, deadline, d0)
                if v3 < best_v:
                    best, best_v = h3, v3
                else:
                    v2 = _value(h2)
                    if v2 < best_v:
                        best, best_v = h2, v2
            except Exception:
                pass
    except Exception:
        pass
    return [float(a) for a in best]