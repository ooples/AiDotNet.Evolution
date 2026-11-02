import time
import numpy as np
from scipy.optimize import linprog


def _val(h):
    n = len(h)
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _slp(h, n, deadline):
    r = 0.1
    cur = _val(h)
    while time.time() < deadline and r > 1e-7:
        g = 1 - h
        C = np.correlate(h, g, mode="full")
        m = len(C)
        J = np.empty((m, n))
        for i in range(n):
            e = np.zeros(n)
            e[i] = 1.0
            J[:, i] = np.correlate(e, g, mode="full") - np.correlate(h, e, mode="full")
        c = np.zeros(n + 1)
        c[-1] = 1.0
        A = np.hstack([J, -np.ones((m, 1))])
        b = -C
        Aeq = np.zeros((1, n + 1))
        Aeq[0, :n] = 1.0
        lo = np.maximum(-r, -h)
        hi = np.minimum(r, 1 - h)
        bounds = [(lo[i], hi[i]) for i in range(n)] + [(None, None)]
        try:
            res = linprog(c, A_ub=A, b_ub=b, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs")
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        cand = np.clip(h + res.x[:n], 0, 1)
        v = _val(cand)
        if v < cur - 1e-12 and abs(cand.sum() - n / 2) < 1e-7:
            h, cur = cand, v
            r *= 1.3
        else:
            r *= 0.5
    return h, cur


def _fix_sum(h, n):
    h = np.clip(h, 0, 1)
    for _ in range(50):
        s = n / 2 - h.sum()
        if abs(s) < 1e-12:
            break
        room = (1 - h) if s > 0 else h
        tot = room.sum()
        if tot <= 0:
            break
        h = h + np.sign(s) * min(abs(s), tot) * room / tot
    return h


def construct():
    start = time.time()
    deadline = start + 35
    n = 100
    best = np.full(n, 0.5)
    bestv = _val(best)
    rng = np.random.default_rng(0)
    trial = 0
    while time.time() < deadline:
        x = np.linspace(0, 1, n)
        if trial == 0:
            h = 0.5 + 0.4 * np.cos(np.pi * (x - 0.5) * 2) * 0.0 + 0.0
            h = 0.3 + 0.7 * (1 - (2 * x - 1) ** 2)
        else:
            noise = np.convolve(rng.normal(size=n + 10), np.ones(5) / 5, mode="valid")[:n]
            h = 0.5 + 0.3 * noise / (np.abs(noise).max() + 1e-9) + 0.2 * (rng.random() - 0.5)
        h = np.clip(h, 0, 1)
        h = _fix_sum(h, n)
        trial += 1
        try:
            h, v = _slp(h, n, deadline)
        except Exception:
            continue
        h = _fix_sum(h, n)
        if abs(h.sum() - n / 2) < 1e-9 * n:
            v = _val(h)
            if v < bestv:
                best, bestv = h.copy(), v
    return [float(t) for t in best]