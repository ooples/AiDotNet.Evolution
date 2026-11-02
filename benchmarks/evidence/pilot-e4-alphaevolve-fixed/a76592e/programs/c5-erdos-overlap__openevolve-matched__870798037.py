import time
import numpy as np
from scipy.optimize import linprog


def _val(h):
    n = len(h)
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _fix_sum(h):
    n = len(h)
    h = np.clip(h, 0, 1)
    for _ in range(300):
        r = n / 2 - h.sum()
        if abs(r) < 1e-13:
            break
        room = (1 - h) if r > 0 else h
        tot = room.sum()
        if tot <= 0:
            break
        h = h + np.sign(r) * min(abs(r), tot) * room / tot
        h = np.clip(h, 0, 1)
    return h


def _jac(h):
    n = len(h)
    K = (np.arange(2 * n - 1) - (n - 1))[:, None]
    j = np.arange(n)[None, :]
    jm = j - K
    okm = (jm >= 0) & (jm < n)
    jp = j + K
    okp = (jp >= 0) & (jp < n)
    A = np.where(okm, 1 - h[np.clip(jm, 0, n - 1)], 0.0)
    B = np.where(okp, h[np.clip(jp, 0, n - 1)], 0.0)
    return A - B


def _refine(h, deadline, r0):
    n = len(h)
    cur = _fix_sum(h.copy())
    cur_val = _val(cur)
    r = r0
    cost = np.zeros(n + 1)
    cost[-1] = 1
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1
    while time.time() < deadline - 0.5 and r > 1e-7:
        C = np.correlate(cur, 1 - cur, mode="full")
        J = _jac(cur)
        A = np.hstack([J, -np.ones((len(C), 1))])
        lo = np.maximum(-r, -cur)
        hi = np.minimum(r, 1 - cur)
        bounds = list(zip(lo, hi)) + [(None, None)]
        tl = max(1.0, deadline - time.time())
        try:
            res = linprog(cost, A_ub=A, b_ub=-C, A_eq=Aeq, b_eq=[0],
                          bounds=bounds, method="highs",
                          options={"time_limit": tl})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        new = _fix_sum(cur + res.x[:n])
        v = _val(new)
        if v < cur_val - 1e-13:
            cur, cur_val = new, v
            r = min(r * 1.2, 0.3)
        else:
            r *= 0.5
    return cur, cur_val


def construct():
    t0 = time.time()
    rng = np.random.default_rng(0)
    n = 50
    h = _fix_sum(np.clip(0.5 + 0.3 * rng.standard_normal(n), 0, 1))
    best, best_val = _refine(h, t0 + 8, 0.2)
    cur = best
    for end, r0 in ((t0 + 18, 0.05), (t0 + 33, 0.03), (t0 + 52, 0.02)):
        if time.time() > end - 3:
            continue
        up = np.repeat(cur, 2)
        up = _fix_sum(up + 1e-6 * rng.standard_normal(len(up)))
        cand, val = _refine(up, end, r0)
        cur = cand
        if val < best_val:
            best, best_val = cand.copy(), val
    best = _fix_sum(best)
    if abs(best.sum() - len(best) / 2) > 1e-9 * len(best):
        return [0.5] * 16
    return [float(x) for x in best]