import time
import numpy as np


def _val(h):
    h = np.asarray(h, dtype=float)
    return 2 * len(h) * np.max(np.convolve(h, h)) / np.sum(h) ** 2


def _smooth(x0, p):
    from scipy.optimize import minimize

    def f(x):
        h = x * x
        s = h.sum()
        c = np.convolve(h, h)
        cm = c.max()
        if cm <= 0 or s <= 0:
            return 1e9, np.zeros_like(x)
        q = (c / cm) ** p
        Q = q.sum()
        val = np.log(Q) / p + np.log(cm) - 2 * np.log(s)
        g = (q / Q) / np.maximum(c, 1e-300)
        dh = 2 * np.correlate(g, h, mode="valid") - 2.0 / s
        return val, dh * 2 * x

    res = minimize(f, x0, jac=True, method="L-BFGS-B",
                   options={"maxiter": 400, "maxfun": 600})
    return res.x


def construct():
    t0 = time.time()
    fallback = [1.0] * 16
    try:
        n = 200
        rng = np.random.default_rng(0)
        x = np.sqrt(1 + 0.3 * rng.random(n))
        best = x * x
        best_v = _val(best)
        for p in [4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048]:
            if time.time() - t0 > 20:
                break
            x = _smooth(x, p)
            h = x * x
            v = _val(h)
            if v < best_v:
                best_v, best = v, h.copy()
        h = best / best.sum()
        try:
            from scipy.optimize import linprog
            m = 2 * n - 1
            delta = 0.05 / n
            cur = np.max(np.convolve(h, h))
            obj = np.zeros(n + 1)
            obj[-1] = 1
            A_eq = np.zeros((1, n + 1))
            A_eq[0, :n] = 1
            rows = np.arange(n)[:, None] + np.arange(n)[None, :]
            cols = np.broadcast_to(np.arange(n)[None, :], (n, n))
            while time.time() - t0 < 50 and delta > 1e-12:
                c = np.convolve(h, h)
                A = np.zeros((m, n + 1))
                # A[i+j, j] = 2*h[i]
                A[rows, cols] = np.broadcast_to(2 * h[:, None], (n, n))
                A[:, -1] = -1
                bounds = [(-delta, delta)] * n + [(None, None)]
                res = linprog(obj, A_ub=A, b_ub=-c, A_eq=A_eq, b_eq=[0],
                              bounds=bounds, method="highs",
                              options={"time_limit": 5})
                if res.status != 0 or res.x is None:
                    delta *= 0.5
                    continue
                hn = h + res.x[:n]
                s = hn.sum()
                if s <= 0:
                    delta *= 0.5
                    continue
                hn = hn / s
                new = np.max(np.convolve(hn, hn))
                if new < cur:
                    h, cur = hn, new
                    delta *= 1.3
                else:
                    delta *= 0.5
        except Exception:
            pass
        if _val(h) < _val(best):
            best = h
        if _val(best) < _val(fallback):
            return [float(v) for v in best]
    except Exception:
        pass
    return fallback