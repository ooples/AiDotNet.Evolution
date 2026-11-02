import time
import numpy as np
from scipy.optimize import linprog


def _val(h):
    n = len(h)
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _fix_sum(h, target):
    for _ in range(100):
        r = h.sum() - target
        if abs(r) < 1e-13:
            break
        free = (h > 1e-12) if r > 0 else (h < 1 - 1e-12)
        c = free.sum()
        if c == 0:
            break
        h = np.clip(h - np.where(free, r / c, 0.0), 0, 1)
    return h


def _jac(h, N, idx, lags):
    M = idx[None, :] - lags[:, None]
    v1 = (M >= 0) & (M < N)
    P = idx[None, :] + lags[:, None]
    v2 = (P >= 0) & (P < N)
    A = np.where(v1, 1 - h[np.clip(M, 0, N - 1)], 0.0)
    B = np.where(v2, h[np.clip(P, 0, N - 1)], 0.0)
    return A - B


def _opt(h, deadline, tr, N):
    target = N / 2
    h = _fix_sum(h, target)
    cur = _val(h)
    idx = np.arange(N)
    lags = np.arange(-(N - 1), N)
    c = np.zeros(N + 1)
    c[-1] = 1
    Aeq = np.zeros((1, N + 1))
    Aeq[0, :N] = 1
    while time.time() < deadline and tr > 1e-7:
        C = np.correlate(h, 1 - h, mode="full")
        J = _jac(h, N, idx, lags)
        A = np.hstack([J, -np.ones((2 * N - 1, 1))])
        lo = np.maximum(-tr, -h)
        hi = np.minimum(tr, 1 - h)
        bounds = list(zip(lo, hi)) + [(None, None)]
        remaining = max(1.0, deadline + 2 - time.time())
        try:
            res = linprog(c, A_ub=A, b_ub=-C, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": remaining})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            tr *= 0.5
            continue
        new = _fix_sum(np.clip(h + res.x[:N], 0, 1), target)
        v = _val(new)
        if v < cur - 1e-13:
            h, cur = new, v
            tr = min(tr * 1.3, 0.2)
        else:
            tr *= 0.5
    return h, cur


def _refine(h, N, end, trs):
    cur = _val(h)
    while time.time() < end and trs > 1e-4:
        h2, v2 = _opt(h, end, trs, N)
        if v2 < cur - 1e-12:
            h, cur = h2, v2
        else:
            trs *= 0.4
    return h, cur


def construct():
    t0 = time.time()
    N = 200
    target = N / 2
    best = np.full(N, 0.5)
    bestv = _val(best)
    try:
        n1 = 100
        cands = []
        for seed in (1, 2, 3, 4, 5, 6):
            rng = np.random.default_rng(seed)
            h = np.clip(0.5 + 0.35 * rng.standard_normal(n1), 0, 1)
            h, v = _opt(h, time.time() + 2, 0.1, n1)
            cands.append((v, h))
        cands.sort(key=lambda x: x[0])
        cur, h = cands[0]
        h, cur = _refine(h, n1, t0 + 15, 0.05)
        h = np.repeat(h, 2)
        h, cur = _refine(h, N, t0 + 50, 0.03)
        h = _fix_sum(np.clip(h, 0, 1), target)
        if abs(h.sum() - target) < 1e-9 * N and _val(h) < bestv:
            best = h
    except Exception:
        pass
    return [float(x) for x in best]