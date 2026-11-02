import time
import numpy as np


def _val(h):
    s = h.sum()
    return 2 * len(h) * np.max(np.convolve(h, h)) / (s * s)


def construct():
    t0 = time.time()
    best = [1.0] * 16
    try:
        from scipy.optimize import minimize, linprog
        from scipy.signal import fftconvolve

        def fg(h, beta):
            n = len(h)
            S = h.sum()
            if abs(S) < 1e-12:
                return 1e9, np.zeros_like(h)
            c = fftconvolve(h, h)
            u = 2.0 * n * c / (S * S)
            mu = u.max()
            e = np.exp(beta * (u - mu))
            z = e.sum()
            F = mu + np.log(z) / beta
            q = e / z
            g = fftconvolve(q, h[::-1], mode="valid")
            grad = 4.0 * n * g / (S * S) - 2.0 * np.dot(q, u) / S
            return F, grad

        def lp(h, until):
            n = len(h)
            h = h / h.sum() * n
            cur = _val(h)
            r = 0.01
            while time.time() - t0 < until - 3 and r > 1e-6:
                c0 = np.convolve(h, h)
                idx = np.where(c0 > 0.94 * c0.max())[0]
                J = np.zeros((len(idx), n))
                for i, k in enumerate(idx):
                    j0 = max(0, k - n + 1)
                    j1 = min(n - 1, k)
                    js = np.arange(j0, j1 + 1)
                    J[i, js] = 2 * h[k - js]
                A = np.hstack([J, -np.ones((len(idx), 1))])
                b = -c0[idx]
                cost = np.zeros(n + 1)
                cost[-1] = 1.0
                Aeq = np.zeros((1, n + 1))
                Aeq[0, :n] = 1.0
                hm = np.abs(h).max()
                bounds = [(-r * hm, r * hm)] * n + [(None, None)]
                rem = min(8.0, until - (time.time() - t0))
                if rem < 1:
                    break
                try:
                    res = linprog(cost, A_ub=A, b_ub=b, A_eq=Aeq, b_eq=[0.0],
                                  bounds=bounds, method="highs",
                                  options={"time_limit": rem})
                except Exception:
                    break
                if res.status != 0 or res.x is None:
                    r *= 0.5
                    continue
                hn = h + res.x[:n]
                if abs(hn.sum()) < 1e-9:
                    r *= 0.5
                    continue
                hn = hn / hn.sum() * n
                v = _val(hn)
                if v < cur:
                    h, cur = hn, v
                    r = min(r * 1.3, 0.03)
                else:
                    r *= 0.5
            return h

        n = 200
        rng = np.random.default_rng(0)
        x = (np.arange(n) + 0.5) / n
        h0 = 1.0 / np.sqrt(np.maximum(x * (1 - x), 1e-2))
        h = h0 * (1 + 0.1 * rng.random(n))
        h = h / h.mean()
        best_v = _val(h)
        best = h.tolist()

        stages = [
            (1, [20, 40, 80, 160, 320, 640, 1280, 2560, 5120], 12, 0),
            (3, [640, 1280, 2560, 5120, 10240], 24, 36),
            (2, [2560, 5120, 10240, 20480], 40, 54),
        ]
        for rep, bs, end, lpend in stages:
            if rep > 1:
                h = np.repeat(h, rep)
            for beta in bs:
                if time.time() - t0 > end:
                    break
                res = minimize(fg, h, args=(float(beta),), jac=True,
                               method="L-BFGS-B",
                               options={"maxiter": 1000, "maxfun": 2000,
                                        "ftol": 1e-15, "gtol": 1e-12})
                hh = res.x
                s = hh.sum()
                if abs(s) > 1e-9:
                    hh = hh / s * len(hh)
                    h = hh
                    v = _val(h)
                    if v < best_v:
                        best_v = v
                        best = h.tolist()
            if lpend:
                h2 = lp(h.copy(), lpend)
                v = _val(h2)
                if v < _val(h):
                    h = h2
                if v < best_v:
                    best_v = v
                    best = h2.tolist()
    except Exception:
        pass
    return best