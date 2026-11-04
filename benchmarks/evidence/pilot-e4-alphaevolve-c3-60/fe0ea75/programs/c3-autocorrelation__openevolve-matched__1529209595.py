import time
import numpy as np


def _val(h):
    n = len(h)
    s = np.sum(h)
    if abs(s) < 1e-300:
        return float("inf")
    return 2.0 * n * np.max(np.convolve(h, h)) / (s * s)


def construct():
    t0 = time.time()
    deadline = t0 + 52.0
    n = 200
    rng = np.random.default_rng(0)
    best = np.ones(n) / n
    bestv = _val(best)
    try:
        from scipy.optimize import linprog

        h = np.abs(best + rng.normal(0, 0.1 / n, n))
        h /= h.sum()
        cur = _val(h)
        if cur < bestv:
            best, bestv = h.copy(), cur
        r = 0.3 / n
        m = 2 * n - 1
        cvec = np.zeros(n + 1)
        cvec[-1] = 1.0
        Aeq = np.zeros((1, n + 1))
        Aeq[0, :n] = 1.0
        ridx = np.arange(n)[:, None] + np.arange(n)[None, :]
        cidx = np.broadcast_to(np.arange(n)[None, :], (n, n))
        last = 0.0
        while time.time() + last * 1.5 < deadline and r > 1e-12:
            ts = time.time()
            c = np.convolve(h, h)
            S = np.abs(h).sum()
            keep = np.nonzero(c >= c.max() - 4.0 * r * S - 2.0 * n * r * r)[0]
            M = np.zeros((m, n))
            M[ridx, cidx] = h[:, None]
            A = np.hstack([2.0 * M[keep], -np.ones((len(keep), 1))])
            bounds = [(-r, r)] * n + [(None, None)]
            rem = max(1.0, deadline - time.time())
            try:
                res = linprog(cvec, A_ub=A, b_ub=-c[keep], A_eq=Aeq,
                              b_eq=[0.0], bounds=bounds, method="highs",
                              options={"time_limit": float(rem)})
            except Exception:
                break
            last = time.time() - ts
            if res.status != 0 or res.x is None:
                r *= 0.5
                continue
            hn = h + res.x[:n]
            s = hn.sum()
            if abs(s) < 1e-12:
                r *= 0.5
                continue
            hn = hn / s
            v = _val(hn)
            if v < cur - 1e-13:
                h, cur = hn, v
                r *= 1.3
                if cur < bestv:
                    best, bestv = h.copy(), cur
            else:
                r *= 0.5
    except Exception:
        pass
    return [float(x) for x in best]