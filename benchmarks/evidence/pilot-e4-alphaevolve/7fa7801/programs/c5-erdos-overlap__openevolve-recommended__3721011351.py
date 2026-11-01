import time
import numpy as np


def _score(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1.0 - h, mode="full")) / n


def _project(x, n):
    lo, hi = -2.0, 2.0
    half = n / 2.0
    for _ in range(40):
        mid = 0.5 * (lo + hi)
        s = np.clip(x - mid, 0.0, 1.0).sum()
        if s > half:
            lo = mid
        else:
            hi = mid
    return np.clip(x - 0.5 * (lo + hi), 0.0, 1.0)


def _fix_sum(h):
    n = len(h)
    h = np.clip(h, 0.0, 1.0)
    for _ in range(100):
        diff = n / 2.0 - h.sum()
        if abs(diff) < 1e-12:
            break
        room = (1.0 - h) if diff > 0 else h
        tot = room.sum()
        if tot <= 0:
            break
        h = np.clip(h + diff * room / tot, 0.0, 1.0)
    return h


def _slp(h, t0, limit):
    from scipy.optimize import linprog
    n = len(h)
    jj = np.arange(n)[None, :]
    kk = np.arange(2 * n - 1)[:, None]
    d = kk - (n - 1)
    i1 = jj + d
    v1 = (i1 >= 0) & (i1 < n)
    i2 = jj - d
    v2 = (i2 >= 0) & (i2 < n)
    i1c = np.clip(i1, 0, n - 1)
    i2c = np.clip(i2, 0, n - 1)
    tr = 0.02
    best = _score(h)
    while time.time() - t0 < limit and tr > 1e-6:
        u = 1.0 - h
        C = np.correlate(h, u, mode="full")
        J = np.where(v1, u[i1c], 0.0) - np.where(v2, h[i2c], 0.0)
        sel = np.where(C > C.max() - 0.15 * n * 0.5 * 0.05 - 1e-9 * 0 - 5.0 * tr * 2)[0]
        if len(sel) == 0:
            break
        A = np.hstack([J[sel], -np.ones((len(sel), 1))])
        b = -C[sel]
        c = np.zeros(n + 1)
        c[-1] = 1.0
        lb = np.maximum(-tr, -h)
        ub = np.minimum(tr, 1.0 - h)
        bounds = list(zip(lb, ub)) + [(None, None)]
        Aeq = np.zeros((1, n + 1))
        Aeq[0, :n] = 1.0
        try:
            res = linprog(c, A_ub=A, b_ub=b, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs")
        except Exception:
            break
        if res.status != 0:
            tr *= 0.5
            continue
        hn = _fix_sum(h + res.x[:n])
        s = _score(hn)
        if s < best - 1e-12:
            best = s
            h = hn
            tr = min(tr * 1.3, 0.05)
        else:
            tr *= 0.5
    return h


def construct():
    n = 500
    base = [0.5] * 16
    t0 = time.time()
    limit = 26.0
    rng = np.random.default_rng(1)
    x = _project(0.5 + 0.1 * rng.standard_normal(n), n)
    best_h = x.copy()
    best = _score(x)
    m = np.zeros(n)
    v = np.zeros(n)
    it = 0
    max_it = 60000
    try:
        while it < max_it:
            el = time.time() - t0
            if el > limit:
                break
            it += 1
            frac = max(it / max_it, el / limit)
            beta = 40.0 * (4000.0 ** frac)
            lr = 0.015 * (1.0 - frac) ** 2.0 + 0.00001
            u = 1.0 - x
            C = np.correlate(x, u, mode="full") * (2.0 / n)
            z = beta * (C - C.max())
            w = np.exp(z)
            w /= w.sum()
            gh = np.correlate(w, u[::-1], mode="valid")
            r = np.correlate(w, x, mode="valid")
            g = (gh - r[::-1]) * 2.0
            m = 0.9 * m + 0.1 * g
            v = 0.999 * v + 0.001 * g * g
            mh = m / (1 - 0.9 ** it)
            vh = v / (1 - 0.999 ** it)
            x = _project(x - lr * mh / (np.sqrt(vh) + 1e-12), n)
            if it % 3 == 0:
                s = _score(x)
                if s < best:
                    best = s
                    best_h = x.copy()
    except Exception:
        pass

    s = _score(x)
    if s < best:
        best, best_h = s, x.copy()
    h = _fix_sum(best_h)
    try:
        h2 = _slp(h.copy(), t0, 50.0)
        h2 = _fix_sum(h2)
        if abs(h2.sum() - n / 2.0) < 1e-9 * n and _score(h2) < _score(h):
            h = h2
    except Exception:
        pass
    if abs(h.sum() - n / 2.0) < 1e-9 * n and _score(h) < 0.5:
        return [float(a) for a in h]
    return base