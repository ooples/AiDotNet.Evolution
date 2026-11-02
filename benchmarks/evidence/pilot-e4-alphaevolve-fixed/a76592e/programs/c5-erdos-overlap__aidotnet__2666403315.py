import time
import numpy as np


def _fix_sum(h, target):
    h = np.clip(h, 0, 1)
    for _ in range(300):
        diff = target - h.sum()
        if abs(diff) < 1e-13:
            break
        room = (1 - h) if diff > 0 else h
        tot = room.sum()
        if tot <= 0:
            break
        h = np.clip(h + diff * room / tot, 0, 1)
    return h


def _val(h):
    n = len(h)
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _jac(h):
    n = len(h)
    g = 1 - h
    s = np.arange(-(n - 1), n)[:, None]
    p = np.arange(n)[None, :]
    a = p - s
    ok1 = (a >= 0) & (a < n)
    b = p + s
    ok2 = (b >= 0) & (b < n)
    J = np.where(ok1, g[np.clip(a, 0, n - 1)], 0.0)
    J -= np.where(ok2, h[np.clip(b, 0, n - 1)], 0.0)
    return J


def _optimize(h, t_end, r0):
    from scipy.optimize import linprog
    n = len(h)
    N = 2 * n - 1
    target = n / 2
    h = _fix_sum(h, target)
    v = _val(h)
    r = r0
    cost = np.zeros(n + 1)
    cost[-1] = 1
    Aeq = np.hstack([np.ones((1, n)), np.zeros((1, 1))])
    dur = 0.0
    while r > 1e-9:
        now = time.time()
        if now + 1.5 * dur > t_end:
            break
        c = np.correlate(h, 1 - h, mode="full")
        J = _jac(h)
        A = np.hstack([J, -np.ones((N, 1))])
        lo = np.maximum(-r, -h)
        hi = np.minimum(r, 1 - h)
        bounds = list(zip(lo, hi)) + [(None, None)]
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs")
        except Exception:
            break
        dur = max(dur, time.time() - now)
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        hn = _fix_sum(h + res.x[:n], target)
        vn = _val(hn)
        if vn < v - 1e-14:
            h, v = hn, vn
            r = min(r * 1.4, 0.3)
        else:
            r *= 0.5
    return h, v


def construct():
    t0 = time.time()
    best = np.full(16, 0.5)
    best_v = _val(best)
    try:
        n = 48
        cands = []
        for seed in range(1, 9):
            rng = np.random.default_rng(seed)
            h0 = 0.5 + 0.25 * rng.standard_normal(n)
            h, v = _optimize(h0, t0 + 1.0 * seed, 0.2)
            cands.append((v, h))
        v, h = min(cands, key=lambda x: x[0])
        if abs(h.sum() - n / 2) < 1e-9 * n and v < best_v:
            best, best_v = h.copy(), v

        for tl, r0 in ((16.0, 0.05), (32.0, 0.02), (51.0, 0.01)):
            h = np.repeat(h, 2)
            n = len(h)
            h, v = _optimize(h, t0 + tl, r0)
            if abs(h.sum() - n / 2) < 1e-9 * n and v < best_v:
                best, best_v = h.copy(), v
    except Exception:
        pass
    return [float(x) for x in best]