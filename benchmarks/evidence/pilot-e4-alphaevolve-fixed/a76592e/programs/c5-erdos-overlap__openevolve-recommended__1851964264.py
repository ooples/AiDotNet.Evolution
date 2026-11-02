import time
import numpy as np


def _val(h):
    n = len(h)
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _fix_sum(h):
    n = len(h)
    h = np.clip(h, 0, 1)
    target = n / 2.0
    for _ in range(200):
        r = target - h.sum()
        if abs(r) < 1e-13:
            break
        room = (1 - h) if r > 0 else h
        tot = room.sum()
        if tot <= 0:
            break
        h = np.clip(h + r * room / tot, 0, 1)
    return h


def _jac(h):
    n = len(h)
    gp = np.zeros(3 * n)
    gp[n:2 * n] = 1 - h
    hp = np.zeros(3 * n)
    hp[n:2 * n] = h
    a = np.arange(n)[None, :]
    m = np.arange(2 * n - 1)[:, None]
    return gp[a - m + 2 * n - 1] - hp[a + m + 1]


def _slp(h, deadline, delta=0.1):
    from scipy.optimize import linprog
    n = len(h)
    best = _val(h)
    while time.time() < deadline and delta > 1e-7:
        c = np.correlate(h, 1 - h, mode="full")
        J = _jac(h)
        M = len(c)
        cost = np.zeros(n + 1)
        cost[-1] = 1
        A = np.hstack([J, -np.ones((M, 1))])
        Aeq = np.zeros((1, n + 1))
        Aeq[0, :n] = 1
        lo = np.maximum(-delta, -h)
        hi = np.minimum(delta, 1 - h)
        bounds = [(lo[i], hi[i]) for i in range(n)] + [(None, None)]
        tl = max(1.0, deadline - time.time())
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq, b_eq=[0],
                          bounds=bounds, method="highs",
                          options={"time_limit": tl})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = _fix_sum(h + res.x[:n])
        v = _val(hn)
        if v < best - 1e-13:
            best = v
            h = hn
            delta = min(delta * 1.3, 0.3)
        else:
            delta *= 0.5
    return h, best


def construct():
    start = time.time()
    fallback = [0.5] * 16
    try:
        rng = np.random.default_rng(3)
        n = 64
        bh, bv = None, 10.0
        for r in range(3):
            now = time.time()
            if now - start > 15:
                break
            h0 = _fix_sum(0.5 + 0.3 * rng.standard_normal(n))
            h, v = _slp(h0, min(start + 16, now + 5))
            if v < bv:
                bv, bh = v, h
        if bh is None:
            return fallback
        # polish best at the base resolution
        h, v = _slp(bh.copy(), start + 22, delta=0.01)
        if v < bv:
            bh, bv = h, v
        # one upsampling stage
        if time.time() - start < 30:
            h2 = _fix_sum(np.repeat(bh, 2))
            h2, v2 = _slp(h2, start + 54, delta=0.02)
            if v2 < bv:
                bh, bv = h2, v2
        bh = _fix_sum(bh)
        nn = len(bh)
        bv = _val(bh)
        if (bv >= 0.5 or abs(bh.sum() - nn / 2) > 1e-9 * nn
                or bh.min() < 0 or bh.max() > 1):
            return fallback
        return [float(x) for x in bh]
    except Exception:
        return fallback