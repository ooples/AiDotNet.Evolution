import time
import numpy as np


def _value(h):
    h = np.asarray(h, dtype=float)
    s = h.sum()
    if s == 0:
        return float("inf")
    n = len(h)
    return 2 * n * abs(np.convolve(h, h).max()) / s ** 2


def construct():
    t0 = time.time()
    n = 400
    best = np.ones(n) / n
    best_v = _value(best)
    try:
        from scipy.optimize import minimize, linprog

        rng = np.random.default_rng(0)

        def fg(x, p):
            h = x * x
            S = h.sum()
            hn = h / S
            c = np.convolve(hn, hn)
            cm = c.max()
            r = c / cm
            L = cm * (np.sum(r ** p)) ** (1.0 / p)
            g = (c / L) ** (p - 1)
            G = 2 * np.correlate(g, hn, "valid")
            dh = (G - np.dot(G, hn)) / S
            return L, dh * 2 * x

        bx, bv = None, float("inf")
        for k in range(10):
            if time.time() - t0 > 6 and bx is not None:
                break
            x0 = 1.0 + 0.3 * rng.random(50)
            for p in [4, 8, 16, 32, 64, 128, 256, 512]:
                res = minimize(fg, x0, args=(p,), jac=True, method="L-BFGS-B",
                               options={"maxiter": 300})
                x0 = res.x
            v = _value(x0 * x0)
            if v < bv:
                bv, bx = v, x0.copy()
        x = bx

        stages = [
            (50, [1024]),
            (100, [64, 256, 1024]),
            (200, [128, 512, 2048]),
            (400, [256, 1024, 2048, 4096]),
        ]
        for size, ps in stages:
            if len(x) != size:
                x = np.repeat(x, size // len(x))
                x = x * (1 + 0.01 * rng.standard_normal(size))
            for p in ps:
                if time.time() - t0 > 20:
                    break
                res = minimize(fg, x, args=(p,), jac=True, method="L-BFGS-B",
                               options={"maxiter": 500})
                x = res.x
                h = x * x
                if len(h) == n:
                    v = _value(h)
                    if v < best_v:
                        best_v = v
                        best = h / h.sum()

        def sg(h, beta):
            S = h.sum()
            hn = h / S
            c = np.convolve(hn, hn)
            cm = c.max()
            e = np.exp(beta * (c - cm))
            Z = e.sum()
            F = cm + np.log(Z) / beta
            w = e / Z
            G = 2 * np.correlate(w, hn, "valid")
            dh = (G - np.dot(G, hn)) / S
            return F * 2 * n, dh * 2 * n

        hs = best.copy()
        for b in [100, 300, 1000, 3000, 10000, 30000]:
            if time.time() - t0 > 28:
                break
            beta = b * 2 * n / 1.5
            res = minimize(sg, hs, args=(beta,), jac=True, method="L-BFGS-B",
                           options={"maxiter": 400})
            hc = res.x
            if abs(hc.sum()) < 1e-12:
                continue
            hc = hc / hc.sum()
            hs = hc
            v = _value(hc)
            if v < best_v:
                best_v = v
                best = hc.copy()

        h = best.copy()
        cur = best_v
        delta = 0.2 / n
        m = 2 * n - 1
        A_eq = np.zeros((1, n + 1))
        A_eq[0, :n] = 1.0
        cost = np.zeros(n + 1)
        cost[-1] = 1.0
        J = np.arange(n)
        limit = 52
        while time.time() - t0 < limit and delta > 1e-10:
            c = np.convolve(h, h)
            cm = c.max()
            margin = 4 * delta * np.abs(h).sum() + 2 * n * delta * delta
            rows = np.where(c >= cm - margin - 1e-12)[0]
            if len(rows) == 0:
                rows = np.arange(m)
            K = rows[:, None] - J[None, :]
            valid = (K >= 0) & (K < n)
            A = np.zeros((len(rows), n + 1))
            A[:, :n] = np.where(valid, 2 * h[np.clip(K, 0, n - 1)], 0.0)
            A[:, -1] = -1.0
            b = -c[rows]
            bounds = [(-delta, delta)] * n + [(None, None)]
            tl = max(1.0, min(6.0, 55 - (time.time() - t0)))
            res = linprog(cost, A_ub=A, b_ub=b, A_eq=A_eq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": tl})
            if res.x is None:
                delta *= 0.5
                continue
            step = res.x[:n]
            ok = False
            used = 1.0
            for sc in (1.0, 0.5):
                hn = h + sc * step
                s = hn.sum()
                if abs(s) < 1e-12:
                    continue
                hn = hn / s
                v = _value(hn)
                if v < cur:
                    h = hn
                    cur = v
                    ok = True
                    used = sc
                    break
            if ok:
                delta *= 1.4 if used == 1.0 else 1.1
            else:
                delta *= 0.6
        if cur < best_v:
            best_v = cur
            best = h
    except Exception:
        pass
    return [float(a) for a in best]