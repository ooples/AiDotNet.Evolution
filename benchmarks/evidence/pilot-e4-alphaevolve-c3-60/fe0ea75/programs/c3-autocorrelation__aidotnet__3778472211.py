import time
import numpy as np
from scipy.optimize import linprog
from scipy import sparse


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    return 2 * len(h) * abs(np.max(np.convolve(h, h))) / s ** 2


def _slp(h, deadline, r0):
    n = len(h)
    h = h * (n / h.sum())
    best = _score(h)
    r = r0
    cost = np.zeros(n + 1)
    cost[-1] = 1.0
    Aeq = sparse.csr_matrix(np.concatenate([np.ones(n), [0.0]])[None, :])
    ar = np.arange(n)
    while time.time() < deadline - 0.4 and r > 1e-8:
        c = np.convolve(h, h)
        mx = c.max()
        margin = 2 * r * np.abs(h).sum() + r * r * n
        ks = np.nonzero(c >= mx - 2 * margin)[0]
        idx = ks[:, None] - ar[None, :]
        valid = (idx >= 0) & (idx < n)
        D = np.where(valid, 2 * h[np.clip(idx, 0, n - 1)], 0.0)
        A = sparse.csr_matrix(np.hstack([D, -np.ones((len(ks), 1))]))
        bounds = [(-r, r)] * n + [(None, None)]
        remaining = max(0.4, deadline - time.time())
        try:
            res = linprog(cost, A_ub=A, b_ub=-c[ks], A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": float(remaining)})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r *= 0.5
            continue
        cand = h + res.x[:n]
        if abs(cand.sum()) < 1e-9:
            r *= 0.5
            continue
        cand = cand * (n / cand.sum())
        sc = _score(cand)
        if sc < best - 1e-13:
            best = sc
            h = cand
            r = min(r * 1.3, 2.0)
        else:
            r *= 0.5
    return h, best


def construct():
    start = time.time()
    total = 50.0
    best_h = np.ones(16)
    best_s = _score(best_h)
    try:
        rng = np.random.default_rng(1)
        n0 = 40
        cand_h, cand_s = None, float("inf")
        for k in range(8):
            h0 = np.abs(1 + 0.4 * rng.standard_normal(n0))
            h, s = _slp(h0, time.time() + 1.4, 0.3)
            if s < cand_s:
                cand_h, cand_s = h, s
        if cand_s < best_s:
            best_s, best_h = cand_s, cand_h.copy()
        h = cand_h
        sizes = [80, 160, 320]
        fracs = [0.18, 0.3, 1.0]
        for n, f in zip(sizes, fracs):
            now = time.time()
            if now - start > total - 2:
                break
            h = np.interp(np.linspace(0, 1, n),
                          np.linspace(0, 1, len(h)), h)
            budget = (total - (now - start)) * f
            h, s = _slp(h, now + budget, 0.12)
            if s < best_s:
                best_s = s
                best_h = h.copy()
            else:
                h = best_h.copy() if len(best_h) == n else h
        now = time.time()
        if total - (now - start) > 1.5 and len(best_h) == 320:
            h, s = _slp(best_h.copy(), start + total, 0.01)
            if s < best_s:
                best_s = s
                best_h = h.copy()
    except Exception:
        pass
    return [float(x) for x in best_h]