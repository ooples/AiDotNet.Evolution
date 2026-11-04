import time
import numpy as np


def _value(h):
    s = h.sum()
    return 2 * len(h) * np.max(np.convolve(h, h)) / (s * s)


def _stage(h, deadline, d0):
    from scipy.optimize import linprog
    n = len(h)
    ar = np.arange(n)
    h = h / h.sum()
    best = h.copy()
    bestv = _value(h)
    delta = d0 / n
    obj = np.zeros(n + 1)
    obj[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    while time.time() < deadline - 0.2 and delta > 1e-10 / n:
        c = np.convolve(h, h)
        cmax = c.max()
        M = 2 * delta * np.abs(h).sum() + 2 * n * delta * delta
        ks = np.nonzero(c >= cmax - 2.2 * M)[0]
        idx = ks[:, None] - ar[None, :]
        valid = (idx >= 0) & (idx < n)
        C = np.where(valid, h[np.clip(idx, 0, n - 1)], 0.0)
        A = np.hstack([2 * C, -np.ones((len(ks), 1))])
        bounds = [(-delta, delta)] * n + [(None, None)]
        remaining = max(0.3, deadline - time.time())
        try:
            res = linprog(obj, A_ub=A, b_ub=-c[ks], A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": remaining})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = h + res.x[:n]
        s = hn.sum()
        if abs(s) < 1e-12:
            delta *= 0.5
            continue
        hn = hn / s
        v = _value(hn)
        if v < bestv - 1e-15:
            bestv = v
            best = hn.copy()
            h = hn
            delta *= 1.3
        else:
            delta *= 0.5
    return best, bestv


def construct():
    t0 = time.time()
    try:
        import scipy.optimize  # noqa: F401
    except Exception:
        return [1.0] * 16

    rng = np.random.default_rng(0)
    sizes = [30, 60, 100, 160]
    ends = [4.0, 14.0, 34.0, 56.0]
    h = np.ones(sizes[0]) * (1 + 0.1 * rng.random(sizes[0]))
    best = h / h.sum()
    bestv = _value(best)
    first = True
    for n, e in zip(sizes, ends):
        if len(h) != n:
            k = int(np.ceil(n / len(h)))
            h = np.repeat(h, k)
            # resample to exactly n by linear interpolation
            h = np.interp(np.linspace(0, len(h) - 1, n),
                          np.arange(len(h)), h)
        try:
            hb, vb = _stage(h, t0 + e, 0.2 if first else 0.03)
        except Exception:
            break
        first = False
        h = hb
        if np.isfinite(vb) and vb < bestv:
            bestv = vb
            best = hb.copy()
    return [float(x) for x in best]