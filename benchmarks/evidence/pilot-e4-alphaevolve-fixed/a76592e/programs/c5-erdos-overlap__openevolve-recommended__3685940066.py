import time
import numpy as np


def _val(h):
    n = len(h)
    return 2 * np.max(np.correlate(h, 1 - h, mode="full")) / n


def _jac(h, sel=None):
    n = len(h)
    g = 1 - h
    K = (np.arange(2 * n - 1) - (n - 1)) if sel is None else (sel - (n - 1))
    K = K[:, None]
    P = np.arange(n)[None, :]
    q = P - K
    ok = (q >= 0) & (q < n)
    q2 = P + K
    ok2 = (q2 >= 0) & (q2 < n)
    J = np.where(ok, g[np.clip(q, 0, n - 1)], 0.0)
    J -= np.where(ok2, h[np.clip(q2, 0, n - 1)], 0.0)
    return J


def _fix(h, n):
    h = np.clip(h, 0, 1)
    for _ in range(30):
        h = np.clip(h + (n / 2 - h.sum()) / n, 0, 1)
    return h


def _slp(h, linprog, t_end, r0=0.1, filt=False):
    n = len(h)
    cur = _val(h)
    r = r0
    while time.time() < t_end and r > 1e-7:
        remaining = t_end - time.time()
        if remaining < 0.3:
            break
        C = np.correlate(h, 1 - h, mode="full")
        if filt:
            margin = 1.5 * r * n
            sel = np.where(C >= C.max() - margin)[0]
        else:
            sel = np.arange(len(C))
        J = _jac(h, sel)
        c = np.zeros(n + 1)
        c[-1] = 1
        A = np.hstack([J, -np.ones((len(sel), 1))])
        lo = np.maximum(-r, -h)
        hi = np.minimum(r, 1 - h)
        bounds = list(zip(lo, hi)) + [(None, None)]
        Aeq = np.zeros((1, n + 1))
        Aeq[0, :n] = 1
        try:
            res = linprog(c, A_ub=A, b_ub=-C[sel], A_eq=Aeq, b_eq=[0],
                          bounds=bounds, method="highs",
                          options={"time_limit": remaining})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r /= 2
            continue
        new = _fix(h + res.x[:n], n)
        if abs(new.sum() - n / 2) > 1e-10 * n:
            r /= 2
            continue
        v = _val(new)
        if v < cur - 1e-13:
            h, cur = new, v
            r = min(r * 1.3, 0.2)
        else:
            r *= 0.6
    return h, cur


def construct():
    n = 50
    best = np.full(n, 0.5)
    bestv = 0.5
    try:
        from scipy.optimize import linprog
        t0 = time.time()
        h_best, v_best = None, 1e9
        for s in range(4):
            rng = np.random.default_rng(s)
            h = _fix(0.5 + 0.3 * (rng.random(n) - 0.5), n)
            h, v = _slp(h, linprog, time.time() + 1.5, r0=0.1)
            if v < v_best:
                h_best, v_best = h, v
        h = h_best
        if v_best < bestv and abs(h.sum() - n / 2) < 1e-9 * n:
            best, bestv = h.copy(), v_best
        stages = [(12, 0.05, False), (28, 0.02, True), (53, 0.01, True)]
        for tend, r0, filt in stages:
            n = 2 * n
            h = _fix(np.repeat(h, 2), n)
            h, v = _slp(h, linprog, t0 + tend, r0=r0, filt=filt)
            if v < bestv and abs(h.sum() - n / 2) < 1e-9 * n:
                best, bestv = h.copy(), v
    except Exception:
        pass
    return [float(x) for x in best]