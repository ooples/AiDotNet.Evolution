"""Minimize 2n*max(conv(h,h))/sum(h)^2: Adam warm start + active-set sequential LP."""
import time


def _val(np, g):
    s = g.sum()
    if abs(s) < 1e-12:
        return float("inf")
    return 2 * len(g) * np.convolve(g, g).max() / s ** 2


def construct():
    try:
        import numpy as np
    except Exception:
        return [1.0] * 16

    t0 = time.time()
    n = 400
    best = np.ones(n)
    bestv = _val(np, best)
    try:
        rng = np.random.default_rng(0)
        x = 1.0 + 0.3 * rng.random(n)
        m = np.zeros(n)
        v = np.zeros(n)
        b1, b2 = 0.9, 0.999
        budget = 12.0
        it = 0
        while True:
            el = time.time() - t0
            if el > budget:
                break
            frac = el / budget
            it += 1
            beta = 30.0 * (1000.0 / 30.0) ** frac
            lr = 0.02 * (1 - frac) + 0.0005
            a = x * x
            s = a.sum()
            h = a / s
            c = np.convolve(h, h) * (2 * n)
            val = c.max()
            if val < bestv:
                bestv = val
                best = h.copy()
            w = np.exp(beta * (c - val))
            w /= w.sum()
            g = 4 * n * np.correlate(w, h, "valid")
            ga = (g - np.dot(g, h)) / s
            gx = ga * 2 * x
            m = b1 * m + (1 - b1) * gx
            v = b2 * v + (1 - b2) * gx * gx
            x = x - lr * (m / (1 - b1 ** it)) / (np.sqrt(v / (1 - b2 ** it)) + 1e-12)
    except Exception:
        pass

    try:
        from scipy.optimize import linprog
        g0 = best / best.sum() * n
        cur = _val(np, g0)
        delta = 0.03
        cost = np.zeros(n + 1)
        cost[-1] = 1.0
        Aeq = np.zeros((1, n + 1))
        Aeq[0, :n] = 1.0
        jj = np.arange(n)[None, :]
        while time.time() - t0 < 53 and delta > 1e-8:
            c0 = np.convolve(g0, g0)
            mx = c0.max()
            margin = 6.0 * delta * np.abs(g0).sum() + 1e-9
            rows = np.nonzero(c0 >= mx - margin)[0]
            idx = rows[:, None] - jj
            valid = (idx >= 0) & (idx < n)
            A = np.where(valid, g0[np.clip(idx, 0, n - 1)], 0.0)
            Aub = np.hstack([(4.0 / n) * A, -np.ones((len(rows), 1))])
            bub = (2.0 / n) * c0[rows]
            bounds = [(g0[i] - delta, g0[i] + delta) for i in range(n)] + [(None, None)]
            remaining = max(1.0, 55 - (time.time() - t0))
            try:
                res = linprog(cost, A_ub=Aub, b_ub=bub, A_eq=Aeq, b_eq=[float(n)],
                              bounds=bounds, method="highs",
                              options={"time_limit": remaining})
            except Exception:
                break
            if res.status != 0 or res.x is None:
                delta *= 0.5
                continue
            g1 = res.x[:n]
            nv = _val(np, g1)
            if nv < cur:
                g0, cur = g1, nv
                delta *= 1.5
            else:
                delta *= 0.6
        if cur < bestv and np.all(np.isfinite(g0)):
            best = g0 / g0.sum()
            bestv = cur
    except Exception:
        pass

    if bestv < 2.0 and np.all(np.isfinite(best)):
        return [float(t) for t in best]
    return [1.0] * 16