import time
import numpy as np
from scipy.optimize import linprog


def _project(h, target):
    lo, hi = -1.0, 1.0
    for _ in range(100):
        mid = 0.5 * (lo + hi)
        if np.clip(h + mid, 0.0, 1.0).sum() < target:
            lo = mid
        else:
            hi = mid
    return np.clip(h + 0.5 * (lo + hi), 0.0, 1.0)


def _value(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1.0 - h, mode="full")) / n


def _jac_rows(h, idx):
    n = len(h)
    g = 1 - h
    S = (idx - (n - 1))[:, None]
    P = np.arange(n)[None, :]
    i1 = P - S
    m1 = (i1 >= 0) & (i1 < n)
    i2 = P + S
    m2 = (i2 >= 0) & (i2 < n)
    J = np.where(m1, g[np.clip(i1, 0, n - 1)], 0.0)
    J -= np.where(m2, h[np.clip(i2, 0, n - 1)], 0.0)
    return J


def _adam(N, t0, tlimit, seed=0):
    target = N / 2.0
    rng = np.random.default_rng(seed)
    h = _project(0.5 + 0.1 * rng.standard_normal(N), target)
    best, best_val = h.copy(), _value(h)
    iters = 4000
    m = np.zeros(N)
    v = np.zeros(N)
    b1, b2 = 0.9, 0.999
    for it in range(1, iters + 1):
        if time.time() - t0 > tlimit:
            break
        frac = it / iters
        beta = 50.0 * (100.0 ** frac) / N
        g = 1.0 - h
        c = np.correlate(h, g, mode="full")
        w = np.exp(beta * (c - c.max()))
        w /= w.sum()
        g1 = np.convolve(w, g, mode="full")[N - 1:2 * N - 1]
        g2 = np.convolve(h[::-1], w, mode="full")[N - 1:2 * N - 1][::-1]
        grad = g1 - g2
        grad -= grad.mean()
        m = b1 * m + (1 - b1) * grad
        v = b2 * v + (1 - b2) * grad * grad
        mh = m / (1 - b1 ** it)
        vh = v / (1 - b2 ** it)
        lr = 0.02 * (1 - frac) + 0.0005
        h = _project(h - lr * mh / (np.sqrt(vh) + 1e-12), target)
        if it % 10 == 0:
            val = _value(h)
            if val < best_val:
                best_val, best = val, h.copy()
    val = _value(h)
    if val < best_val:
        best_val, best = val, h.copy()
    return best, best_val


def _slp(h, deadline, delta, dmin):
    n = len(h)
    target = n / 2.0
    cur = _value(h)
    A_eq = np.zeros((1, n + 1))
    A_eq[0, :n] = 1.0
    b_eq = [target]
    c = np.zeros(n + 1)
    c[-1] = 1.0
    while time.time() < deadline - 0.5 and delta > dmin:
        try:
            C = np.correlate(h, 1 - h, mode="full")
            margin = 2.5 * delta * n + 1e-9
            idx = np.where(C >= C.max() - margin)[0]
            J = _jac_rows(h, idx)
            A_ub = np.hstack([J, -np.ones((J.shape[0], 1))])
            b_ub = J @ h - C[idx]
            bounds = [(max(0.0, x - delta), min(1.0, x + delta)) for x in h]
            bounds.append((None, None))
            tl = max(1.0, deadline - time.time())
            res = linprog(c, A_ub=A_ub, b_ub=b_ub, A_eq=A_eq, b_eq=b_eq,
                          bounds=bounds, method="highs",
                          options={"time_limit": tl})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        cand = _project(res.x[:n].copy(), target)
        cv = _value(cand)
        if cv < cur - 1e-13:
            h, cur = cand, cv
            delta = min(delta * 1.5, 0.1)
        else:
            delta *= 0.5
    return h, cur


def construct():
    t0 = time.time()
    best, best_val = None, 1e9
    for seed in range(3):
        s = time.time()
        h0, v0 = _adam(100, s, 2.0, seed)
        h1, v1 = _slp(h0.copy(), s + 4.5, 0.02, 1e-7)
        if v1 > v0:
            h1, v1 = h0, v0
        if v1 < best_val:
            best, best_val = h1.copy(), v1

    h, v = _slp(best.copy(), t0 + 14, 0.01, 1e-7)
    if v < best_val:
        best, best_val = h.copy(), v

    for dl, d0 in [(28, 0.01), (42, 0.006), (53, 0.004)]:
        if time.time() - t0 > dl - 3:
            continue
        h2 = _project(np.repeat(best, 2).astype(float), best.size)
        h2, v2 = _slp(h2, t0 + dl, d0, 1e-7)
        if v2 < best_val:
            best, best_val = h2.copy(), v2

    n = len(best)
    best = _project(np.asarray(best, dtype=float), n / 2.0)
    if _value(best) > 0.5 or abs(best.sum() - n / 2.0) > 1e-9 * n:
        return [0.5] * 16
    return [float(x) for x in best]