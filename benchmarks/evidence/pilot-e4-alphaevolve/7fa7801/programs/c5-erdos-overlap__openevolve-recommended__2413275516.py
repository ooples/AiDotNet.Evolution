import time
import numpy as np


def _project(x, target):
    lo, hi = -2.0, 2.0
    for _ in range(60):
        s = 0.5 * (lo + hi)
        if np.clip(x + s, 0, 1).sum() > target:
            hi = s
        else:
            lo = s
    h = np.clip(x + 0.5 * (lo + hi), 0, 1)
    for _ in range(5):
        r = target - h.sum()
        if abs(r) < 1e-12:
            break
        free = (h > 1e-9) & (h < 1 - 1e-9)
        if not free.any():
            break
        h[free] = np.clip(h[free] + r / free.sum(), 0, 1)
    return h


def _value(h):
    n = len(h)
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _adam(n, target, budget, t0):
    rng = np.random.default_rng(0)
    x = _project(0.5 + 0.3 * rng.standard_normal(n), target)
    m = np.zeros(n)
    v = np.zeros(n)
    lr = 0.02
    step = 0
    best, best_v = x.copy(), _value(x)
    while True:
        frac = (time.time() - t0) / budget
        if frac >= 1.0:
            break
        step += 1
        beta = 50 * (400.0 ** frac)
        g = 1 - x
        C = np.correlate(x, g, mode="full") * (2.0 / n)
        w = np.exp(beta * (C - C.max()))
        w /= w.sum()
        w = w * (2.0 / n)
        t1 = np.convolve(g, w)[n - 1:2 * n - 1]
        t2 = np.convolve(x, w[::-1])[n - 1:2 * n - 1]
        grad = t1 - t2
        m = 0.9 * m + 0.1 * grad
        v = 0.999 * v + 0.001 * grad * grad
        mh = m / (1 - 0.9 ** step)
        vh = v / (1 - 0.999 ** step)
        lr_t = lr * (1 - 0.97 * frac)
        x = _project(x - lr_t * mh / (np.sqrt(vh) + 1e-12), target)
        if step % 20 == 0:
            val = _value(x)
            if val < best_v:
                best, best_v = x.copy(), val
    if _value(x) < best_v:
        best = x.copy()
    return best


def _slp(x, target, deadline):
    from scipy.optimize import linprog
    n = len(x)
    K = np.arange(2 * n - 1)[:, None]
    M = np.arange(n)[None, :]
    i1 = M - K + n - 1
    v1 = (i1 >= 0) & (i1 < n)
    i1 = np.clip(i1, 0, n - 1)
    i2 = M + K - (n - 1)
    v2 = (i2 >= 0) & (i2 < n)
    i2 = np.clip(i2, 0, n - 1)
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    beq = [0.0]
    cur = _value(x)
    r = 0.005
    while time.time() < deadline and r > 1e-8:
        g = 1 - x
        J = np.where(v1, g[i1], 0.0) - np.where(v2, x[i2], 0.0)
        C0 = np.correlate(x, g, mode="full")
        A = np.hstack([J, -np.ones((2 * n - 1, 1))])
        b = -C0
        lb = np.maximum(-r, -x)
        ub = np.minimum(r, 1 - x)
        bounds = list(zip(lb, ub)) + [(None, None)]
        try:
            res = linprog(cost, A_ub=A, b_ub=b, A_eq=Aeq, b_eq=beq,
                          bounds=bounds, method="highs")
        except Exception:
            break
        if res.status != 0:
            r *= 0.5
            continue
        cand = _project(x + res.x[:n], target)
        val = _value(cand)
        if val < cur - 1e-13:
            x, cur = cand, val
            r = min(r * 1.3, 0.02)
        else:
            r *= 0.5
    return x


def construct():
    t0 = time.time()
    n = 300
    target = n / 2
    x = _adam(n, target, 24.0, t0)
    best = np.full(n, 0.5)
    best_v = _value(best)
    if abs(x.sum() - target) < 1e-9 * n and _value(x) < best_v:
        best, best_v = x.copy(), _value(x)
    try:
        y = _slp(best.copy(), target, t0 + 47.0)
        if abs(y.sum() - target) < 1e-9 * n and _value(y) < best_v:
            best, best_v = y, _value(y)
    except Exception:
        pass
    return [float(a) for a in best]