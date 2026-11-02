import time
import numpy as np


def _corr(h):
    return np.correlate(h, 1 - h, mode="full")


def _f(h):
    return 2 * _corr(h).max() / len(h)


def _fix_sum(h, target):
    h = np.clip(h, 0, 1)
    for _ in range(200):
        r = target - h.sum()
        if abs(r) < 1e-13:
            break
        free = (h < 1) if r > 0 else (h > 0)
        if not free.any():
            break
        h = h.copy()
        h[free] += r / free.sum()
        h = np.clip(h, 0, 1)
    return h


def _jac(h):
    n = len(h)
    g = 1 - h
    k = (np.arange(2 * n - 1) - (n - 1))[:, None]
    m = np.arange(n)[None, :]
    j1 = m - k
    ok1 = (j1 >= 0) & (j1 < n)
    j2 = m + k
    ok2 = (j2 >= 0) & (j2 < n)
    J = np.where(ok1, g[np.clip(j1, 0, n - 1)], 0.0)
    J -= np.where(ok2, h[np.clip(j2, 0, n - 1)], 0.0)
    return J


def _optimize(h, deadline, r, linprog, rmin=1e-7):
    n = len(h)
    target = n / 2
    h = _fix_sum(h, target)
    val = _f(h)
    cost = np.zeros(n + 1)
    cost[-1] = 1
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1
    while time.time() < deadline and r > rmin:
        c = _corr(h)
        cmax = c.max()
        sel = c > cmax - 2.5 * r * n
        J = _jac(h)[sel]
        K = J.shape[0]
        A = np.hstack([J, -np.ones((K, 1))])
        lo = np.maximum(-r, -h)
        hi = np.minimum(r, 1 - h)
        bounds = [(lo[i], hi[i]) for i in range(n)] + [(None, None)]
        try:
            res = linprog(cost, A_ub=A, b_ub=-c[sel], A_eq=Aeq, b_eq=[0],
                          bounds=bounds, method="highs")
        except Exception:
            break
        if res.status != 0:
            r *= 0.5
            continue
        hn = _fix_sum(h + res.x[:n], target)
        vn = _f(hn)
        if vn < val - 1e-13:
            h, val = hn, vn
            r = min(r * 1.3, 0.3)
        else:
            r *= 0.5
    return h, val


def construct():
    from scipy.optimize import linprog

    t0 = time.time()
    n = 100
    rng = np.random.default_rng(1)
    h = _fix_sum(0.5 + 0.2 * rng.standard_normal(n), n / 2)
    best, bv = h.copy(), _f(h)

    h, v = _optimize(h, t0 + 12, 0.1, linprog)
    if v < bv:
        best, bv = h.copy(), v

    for dl, r0 in ((t0 + 28, 0.03), (t0 + 46, 0.02)):
        if time.time() > dl - 4:
            break
        h = np.repeat(best, 2)
        h, v = _optimize(h, dl, r0, linprog)
        if v < bv:
            best, bv = h.copy(), v

    n = len(best)
    best = _fix_sum(best, n / 2)
    if abs(best.sum() - n / 2) > 1e-9 * n or _f(best) > 0.5:
        return [0.5] * 16
    return [float(x) for x in best]