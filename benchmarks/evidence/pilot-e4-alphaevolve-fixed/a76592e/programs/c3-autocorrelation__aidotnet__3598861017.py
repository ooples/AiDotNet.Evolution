import time
import numpy as np
from scipy.optimize import minimize
from scipy.signal import fftconvolve


def _val(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    return 2 * len(h) * abs(np.max(np.convolve(h, h))) / (s * s)


class _Timeout(Exception):
    pass


def construct():
    t0 = time.time()
    deadline = 55.0
    rng = np.random.default_rng(0)
    h = 1.0 + 0.3 * rng.standard_normal(100)
    best_h = np.ones(16)
    best_v = _val(best_h)

    def make_fun(n, beta, state):
        big = n >= 400

        def fun(x):
            if time.time() - t0 > deadline:
                raise _Timeout()
            state["x"] = x.copy()
            S = x.sum()
            hp = x / S
            c = 2 * n * (fftconvolve(hp, hp) if big else np.convolve(hp, hp))
            z = beta * c
            m = z.max()
            e = np.exp(z - m)
            se = e.sum()
            L = (m + np.log(se)) / beta
            gc = 2 * n * e / se
            if big:
                gp = 2 * fftconvolve(gc, hp[::-1], "valid")
            else:
                gp = 2 * np.correlate(gc, hp, "valid")
            return L, (gp - gp @ hp) / S

        return fun

    def consider(hx, n):
        nonlocal best_v, best_h
        if hx is None or not np.all(np.isfinite(hx)) or abs(hx.sum()) < 1e-9:
            return
        hh = hx / hx.sum() * n
        v = _val(hh)
        if v < best_v:
            best_v, best_h = v, hh.copy()

    stages = [100, 200, 400, 800, 1600, 3200]
    try:
        for si, n in enumerate(stages):
            if si > 0:
                h = np.repeat(h, 2)
            if si == 0:
                betas = [20, 60, 200, 600, 2000, 6000, 20000, 60000, 200000]
            elif si < 3:
                betas = [2000, 20000, 200000]
            elif n < 3200:
                betas = [6000, 20000, 60000, 200000, 600000, 2000000]
            else:
                betas = [6000, 20000, 60000, 200000, 600000, 2000000,
                         5000000, 20000000, 60000000]
            mi = 3000 if n >= 3200 else (2000 if n >= 1600 else 1500)
            for beta in betas:
                if time.time() - t0 > deadline:
                    raise _Timeout()
                state = {}
                try:
                    res = minimize(make_fun(n, beta, state), h, jac=True,
                                   method="L-BFGS-B",
                                   options={"maxiter": mi,
                                            "maxfun": int(mi * 1.25),
                                            "maxcor": 50,
                                            "ftol": 1e-15,
                                            "gtol": 1e-12})
                    hx = res.x
                except _Timeout:
                    consider(state.get("x"), n)
                    raise
                if np.all(np.isfinite(hx)) and abs(hx.sum()) > 1e-9:
                    h = hx / hx.sum() * n
                    consider(h, n)
    except Exception:
        pass

    return [float(v) for v in best_h]