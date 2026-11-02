import time
import numpy as np


def _val(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1.0 - h, mode="full")) / n


def _fix_sum(h, target):
    h = np.clip(h, 0.0, 1.0)
    for _ in range(100):
        diff = target - h.sum()
        if abs(diff) < 1e-12:
            break
        room = (1.0 - h) if diff > 0 else h
        tot = room.sum()
        if tot <= 0:
            break
        h = np.clip(h + diff * room / tot, 0.0, 1.0)
    return h


def _jac(h, g):
    N = len(h)
    k = np.arange(2 * N - 1)[:, None]
    j = np.arange(N)[None, :]
    i1 = j - k + N - 1
    m1 = (i1 >= 0) & (i1 < N)
    J1 = np.where(m1, g[np.clip(i1, 0, N - 1)], 0.0)
    i2 = j + k - N + 1
    m2 = (i2 >= 0) & (i2 < N)
    J2 = np.where(m2, h[np.clip(i2, 0, N - 1)], 0.0)
    return J1 - J2


def _valid(h):
    n = len(h)
    return (abs(h.sum() - n / 2.0) < 1e-10 * n and h.min() >= 0.0
            and h.max() <= 1.0)


def _refine(h, t_end, delta, linprog, state):
    n = len(h)
    target = n / 2.0
    h = _fix_sum(h, target)
    best = _val(h)
    m = 2 * n - 1
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    while time.time() < t_end and delta > 1e-8:
        g = 1.0 - h
        c = np.correlate(h, g, mode="full")
        J = _jac(h, g)
        A = np.hstack([J, -np.ones((m, 1))])
        lo = np.maximum(-delta, -h)
        hi = np.minimum(delta, 1.0 - h)
        bounds = list(zip(lo, hi)) + [(None, None)]
        remaining = max(1.0, t_end - time.time())
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": remaining})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        cand = _fix_sum(h + res.x[:n], target)
        v = _val(cand)
        if v < best - 1e-13:
            h, best = cand, v
            delta *= 1.2
            if best < state["v"] and _valid(h):
                state["h"], state["v"] = h.copy(), best
        else:
            delta *= 0.5
    return h, best


def construct():
    state = {"h": None, "v": 1e9}
    try:
        from scipy.optimize import linprog

        t0 = time.time()
        rng = np.random.default_rng(1)
        n = 40
        h = _fix_sum(0.5 + 0.3 * (rng.random(n) - 0.5), n / 2.0)
        stages = [(6, 0.1), (15, 0.03), (27, 0.02), (40, 0.01)]
        for idx, (dt, d0) in enumerate(stages):
            h, v = _refine(h, t0 + dt, d0, linprog, state)
            if v < state["v"] and _valid(h):
                state["h"], state["v"] = h.copy(), v
            if idx < len(stages) - 1:
                h = np.repeat(h, 2)
    except Exception:
        pass
    if state["h"] is not None and state["v"] < 0.5:
        out = _fix_sum(state["h"], len(state["h"]) / 2.0)
        if _valid(out) and _val(out) < 0.5:
            return [float(x) for x in out]
    return [0.5] * 16