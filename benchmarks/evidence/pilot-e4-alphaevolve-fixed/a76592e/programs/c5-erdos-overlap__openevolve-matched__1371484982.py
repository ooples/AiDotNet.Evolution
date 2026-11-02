import time
import numpy as np
from scipy.optimize import linprog


def _val(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _fix(h, n):
    h = np.clip(h, 0, 1)
    for _ in range(300):
        r = n / 2 - h.sum()
        if abs(r) < 1e-13:
            break
        room = (1 - h) if r > 0 else h
        s = room.sum()
        if s <= 0:
            break
        h = np.clip(h + r * room / s, 0, 1)
    return h


def _jac(h, g):
    n = len(h)
    m = 2 * n - 1
    e0 = np.zeros(n)
    e0[0] = 1.0
    c0 = np.correlate(e0, g, mode="full")
    d0 = np.correlate(h, e0, mode="full")
    J = np.zeros((m, n))
    for j in range(n):
        J[j:, j] = c0[:m - j]
        J[:m - j, j] -= d0[j:]
    return J


def _refine(h, deadline, tr):
    n = len(h)
    h = _fix(h, n)
    best, bestv = h.copy(), _val(h)
    c = np.zeros(n + 1)
    c[-1] = 1
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1
    while time.time() < deadline and tr > 1e-7:
        g = 1 - h
        C = np.correlate(h, g, mode="full")
        m = len(C)
        A = np.hstack([_jac(h, g), -np.ones((m, 1))])
        lo = np.maximum(-tr, -h)
        hi = np.minimum(tr, 1 - h)
        bounds = list(zip(lo, hi)) + [(None, None)]
        try:
            res = linprog(c, A_ub=A, b_ub=-C, A_eq=Aeq, b_eq=[0],
                          bounds=bounds, method="highs",
                          options={"time_limit": max(1.0, deadline - time.time())})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            tr *= 0.5
            continue
        cand = _fix(h + res.x[:n], n)
        v = _val(cand)
        if v < bestv - 1e-12:
            h = cand
            best, bestv = cand.copy(), v
            tr = min(tr * 1.3, 0.2)
        else:
            tr *= 0.5
    return best, bestv


def construct():
    t0 = time.time()
    n = 100
    rng = np.random.default_rng(0)
    h = _fix(0.5 + 0.2 * rng.standard_normal(n), n)
    best, bestv = h.copy(), _val(h)
    try:
        stages = [(None, 12, 0.2), (2, 28, 0.03), (2, 50, 0.01)]
        cur = best
        for factor, tend, tr in stages:
            if factor:
                cur = _fix(np.repeat(cur, factor), len(cur) * factor)
            cur, v = _refine(cur, t0 + tend, tr)
            if abs(cur.sum() - len(cur) / 2) < 1e-9 * len(cur) and v < bestv:
                best, bestv = cur.copy(), v
            else:
                break
    except Exception:
        pass
    if bestv > 0.5 or abs(best.sum() - len(best) / 2) > 1e-9 * len(best):
        return [0.5] * 16
    return [float(x) for x in best]