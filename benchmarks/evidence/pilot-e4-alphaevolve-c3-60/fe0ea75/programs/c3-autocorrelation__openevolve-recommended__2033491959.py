import time
import numpy as np
from scipy.linalg import toeplitz
from scipy.optimize import linprog


def _value(h):
    c = np.convolve(h, h)
    s = h.sum()
    if s <= 0:
        return np.inf
    return 2 * len(h) * abs(c.max()) / (s * s)


def _adam(h, limit, beta0, beta1, lr0):
    n = len(h)
    t0 = time.time()
    h = h * (n / h.sum())
    m = np.zeros(n)
    v2 = np.zeros(n)
    b1, b2, eps = 0.9, 0.999, 1e-8
    step = 0
    best_h, best_v = h.copy(), _value(h)
    while True:
        el = time.time() - t0
        if el > limit:
            break
        frac = el / limit
        beta = beta0 * (beta1 / beta0) ** frac
        lr = lr0 * (1 - frac) + lr0 * 0.05
        step += 1
        s = h.sum()
        c = np.convolve(h, h)
        val = c / (s * s) * 2 * n
        w = np.exp(beta * (val - val.max()))
        w /= w.sum()
        corr = np.convolve(w, h[::-1])[n - 1:2 * n - 1]
        wc = float((w * c).sum())
        g = 2 * n * (2 * corr / (s * s) - 2 * wc / (s ** 3))
        m = b1 * m + (1 - b1) * g
        v2 = b2 * v2 + (1 - b2) * g * g
        h = h - lr * (m / (1 - b1 ** step)) / (np.sqrt(v2 / (1 - b2 ** step)) + eps)
        sc = h.sum()
        if sc <= 0 or not np.isfinite(sc):
            h = best_h.copy()
            continue
        h = h * (n / sc)
        if step % 25 == 0:
            v = _value(h)
            if v < best_v:
                best_v, best_h = v, h.copy()
    v = _value(h)
    if v < best_v:
        best_h = h.copy()
    return best_h


def _lp(h, deadline, delta0):
    n = len(h)
    h = h / h.sum()
    best_h, best_v = h.copy(), _value(h)
    delta = delta0
    fails = 0
    while time.time() < deadline - 1.0 and delta > 1e-12 and fails < 8:
        c = np.convolve(h, h)
        col = np.concatenate([2 * h, np.zeros(n - 1)])
        row = np.zeros(n)
        row[0] = col[0]
        A = np.hstack([toeplitz(col, row), -np.ones((2 * n - 1, 1))])
        Aeq = np.concatenate([np.ones(n), [0.0]])[None, :]
        cost = np.zeros(n + 1)
        cost[-1] = 1.0
        bounds = [(-delta, delta)] * n + [(None, None)]
        rem = deadline - time.time()
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": float(max(rem, 0.5))})
        except Exception:
            break
        if res.x is None:
            delta *= 0.5
            fails += 1
            continue
        hn = h + res.x[:n]
        sm = hn.sum()
        if sm <= 0:
            delta *= 0.5
            fails += 1
            continue
        hn = hn / sm
        v = _value(hn)
        if np.isfinite(v) and v < best_v - 1e-12:
            best_v, best_h, h = v, hn.copy(), hn
            delta *= 1.3
            fails = 0
        else:
            delta *= 0.5
            fails += 1
    return best_h, best_v


def construct():
    t0 = time.time()
    best_h = np.ones(16)
    best_v = _value(best_h)

    n = 100
    cands = []
    for seed in range(4):
        rng = np.random.default_rng(seed)
        h = 1.0 + 0.1 * rng.standard_normal(n)
        h = _adam(h, 2.5, 30.0, 3000.0, 0.01)
        cands.append((_value(h), h))
    cands.sort(key=lambda x: x[0])
    h = cands[0][1]

    h = np.repeat(h, 3)
    h = _adam(h, 6.0, 300.0, 5000.0, 0.003)
    h = h / h.sum()
    v = _value(h)
    if v < best_v:
        best_v, best_h = v, h.copy()

    h1, v1 = _lp(h, t0 + 28.0, 0.03 / len(h))
    if v1 < best_v:
        best_v, best_h = v1, h1

    h2 = np.repeat(best_h, 2)
    h2 = h2 / h2.sum()
    h2b, v2 = _lp(h2, t0 + 50.0, 0.005 / len(h2))
    if v2 < best_v:
        best_v, best_h = v2, h2b

    nn = len(best_h)
    return [float(x * nn / best_h.sum()) for x in best_h]