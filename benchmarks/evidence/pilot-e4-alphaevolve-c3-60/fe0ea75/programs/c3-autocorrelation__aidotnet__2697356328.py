import time
import numpy as np
from scipy.optimize import minimize, linprog
from scipy.signal import fftconvolve


def _val(h):
    n = len(h)
    return 2.0 * n * np.max(np.convolve(h, h)) / (np.sum(h) ** 2)


def _make_obj(p):
    def f(h):
        s = h.sum()
        if s <= 1e-9:
            return 1e9, np.zeros_like(h)
        c = np.maximum(fftconvolve(h, h), 0.0)
        m = c.max()
        if m <= 0:
            return 1e9, np.zeros_like(h)
        q = c / m
        qp1 = q ** (p - 1)
        S = np.sum(qp1 * q)
        F = m * S ** (1.0 / p) / s ** 2
        g = S ** (1.0 / p - 1.0) * qp1 / s ** 2
        gh = 2.0 * fftconvolve(g, h[::-1], mode="valid")
        gh = gh - 2.0 * F / s
        return F, gh
    return f


def _run(h, ps, iters, deadline):
    best = h.copy()
    bestv = _val(h)
    x = h.copy()
    for p in ps:
        if time.time() > deadline:
            break
        try:
            res = minimize(_make_obj(p), x, jac=True, method="L-BFGS-B",
                           options={"maxiter": iters, "maxfun": iters * 2,
                                    "ftol": 1e-16, "gtol": 1e-13})
            x = res.x
        except Exception:
            break
        s = x.sum()
        if s <= 0:
            break
        x = x / s
        v = _val(x)
        if v < bestv:
            best, bestv = x.copy(), v
    return best, bestv


def _lp(h, deadline, d0=0.01):
    n = len(h)
    best = h / h.sum()
    bv = _val(best)
    d = d0 * np.abs(best).max()
    cvec = np.zeros(n + 1)
    cvec[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    A = np.zeros((2 * n - 1, n + 1))
    A[:, n] = -1.0
    while time.time() < deadline - 2.0 and d > 1e-10:
        for j in range(n):
            A[j:j + n, j] = 2 * best
        b = np.convolve(best, best)
        bounds = [(best[i] - d, best[i] + d) for i in range(n)]
        bounds.append((None, None))
        try:
            res = linprog(cvec, A_ub=A, b_ub=b, A_eq=Aeq, b_eq=[1.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": max(1.0, deadline - time.time() - 1.0)})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            d *= 0.5
            continue
        hn = res.x[:n]
        if hn.sum() <= 0:
            d *= 0.5
            continue
        hn = hn / hn.sum()
        v = _val(hn)
        if v < bv - 1e-14:
            best, bv = hn, v
            d *= 1.3
        else:
            d *= 0.5
    return best, bv


def construct():
    t0 = time.time()
    rng = np.random.default_rng(0)
    n = 75
    h = 1 + 0.3 * rng.random(n)
    h /= h.sum()
    best, bestv = h, _val(h)
    try:
        first_ps = [4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048]
        b, v = _run(h, first_ps, 1500, t0 + 8)
        cur = b
        if v < bestv:
            best, bestv = b, v
        stages = [
            ([64, 256, 1024, 4096], 2500, t0 + 17),
            ([128, 512, 2048, 8192], 3200, t0 + 30),
            ([256, 1024, 4096, 16384], 3500, t0 + 43),
        ]
        for ps, it, dl in stages:
            if time.time() > dl - 2:
                continue
            up = np.repeat(cur, 2)
            up /= up.sum()
            b2, v2 = _run(up, ps, it, dl)
            cur = b2
            if v2 < bestv or (len(b2) > len(best) and v2 < bestv * 1.0005):
                best, bestv = b2, v2
        if time.time() - t0 < 46 and len(best) <= 700:
            c2, cv = _lp(best, t0 + 58, 0.005)
            if cv < bestv:
                best, bestv = c2, cv
    except Exception:
        pass
    return [float(v) for v in best]