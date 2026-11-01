import time
import numpy as np


def _val(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _fix(h, n):
    lo, hi = -2.0, 2.0
    for _ in range(100):
        mid = 0.5 * (lo + hi)
        if np.clip(h + mid, 0, 1).sum() < n / 2:
            lo = mid
        else:
            hi = mid
    return np.clip(h + 0.5 * (lo + hi), 0, 1)


def _jac(h, eye):
    n = len(h)
    g = 1 - h
    J = np.empty((2 * n - 1, n))
    for i in range(n):
        e = eye[i]
        J[:, i] = np.correlate(e, g, mode="full") - np.correlate(h, e, mode="full")
    return J


def _slp(h, n, linprog, deadline):
    eye = np.eye(n)
    m = 2 * n - 1
    delta = 0.1
    bestv = _val(h)
    cost = np.zeros(n + 1)
    cost[-1] = 1
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1
    while time.time() < deadline and delta > 1e-7:
        c = np.correlate(h, 1 - h, mode="full")
        J = _jac(h, eye)
        A = np.hstack([J, -np.ones((m, 1))])
        lo = np.maximum(-delta, -h)
        hi = np.minimum(delta, 1 - h)
        bounds = [(lo[i], hi[i]) for i in range(n)] + [(None, None)]
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs")
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = _fix(h + res.x[:n], n)
        v = _val(hn)
        if v < bestv - 1e-13:
            bestv, h = v, hn
            delta = min(delta * 1.5, 0.3)
        else:
            delta *= 0.5
    return h, bestv


def _adam(h, n, deadline, iters=3000):
    eye = np.eye(n)
    best, bestv = h.copy(), _val(h)
    mom = np.zeros(n)
    vel = np.zeros(n)
    t = 0
    for it in range(iters):
        if time.time() > deadline:
            break
        frac = it / iters
        beta = 100.0 * (60.0 ** frac)
        lr = 0.02 * (0.03 ** frac)
        c = np.correlate(h, 1 - h, mode="full") * 2.0 / n
        w = np.exp(beta * (c - c.max()))
        w /= w.sum()
        grad = _jac(h, eye).T @ w
        t += 1
        mom = 0.9 * mom + 0.1 * grad
        vel = 0.999 * vel + 0.001 * grad * grad
        step = (mom / (1 - 0.9 ** t)) / (np.sqrt(vel / (1 - 0.999 ** t)) + 1e-12)
        h = _fix(h - lr * step, n)
        v = _val(h)
        if v < bestv:
            best, bestv = h.copy(), v
    return best, bestv


def construct():
    t0 = time.time()
    n = 100
    rng = np.random.default_rng(1)
    x = (np.arange(n) + 0.5) / n
    best = np.full(n, 0.5)
    bestv = _val(best)
    try:
        from scipy.optimize import linprog
    except Exception:
        linprog = None
    k = 0
    while time.time() - t0 < 38:
        if k == 0:
            h0 = 0.5 + 0.5 * np.sin(np.pi * x) ** 2 - 0.3
        elif k == 1:
            h0 = 0.5 + 0.4 * np.cos(2 * np.pi * x)
        else:
            h0 = np.full(n, 0.5)
            for f in range(1, 5):
                h0 = h0 + rng.normal() * 0.25 / f * np.cos(2 * np.pi * f * x + rng.uniform(0, 6.28))
            h0 = h0 + 0.05 * rng.standard_normal(n)
        k += 1
        h0 = _fix(h0, n)
        dl = min(t0 + 38, time.time() + 12)
        try:
            if linprog is not None:
                h, v = _slp(h0, n, linprog, dl)
            else:
                h, v = _adam(h0, n, dl)
        except Exception:
            continue
        if v < bestv and abs(h.sum() - n / 2) <= 1e-9 * n:
            best, bestv = h.copy(), v
    best = _fix(best, n)
    if _val(best) > 0.5 or abs(best.sum() - n / 2) > 1e-9 * n:
        return [0.5] * 16
    return [float(v) for v in best]