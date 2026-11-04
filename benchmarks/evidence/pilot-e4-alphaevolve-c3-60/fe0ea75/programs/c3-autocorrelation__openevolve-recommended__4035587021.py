"""Multi-start smoothed-max L-BFGS (FFT gradients) multi-resolution up to n=800, then sequential LP polish (negative heights allowed)."""
import time
import numpy as np


def _val(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    return 2 * len(h) * abs(np.max(np.convolve(h, h))) / (s * s)


def _make(n, beta_rel, nfft):
    m = 2 * n - 1

    def fg(h):
        S = h.sum()
        if abs(S) < 1e-9:
            return 1e6, np.zeros(n)
        H = np.fft.rfft(h, nfft)
        c = np.fft.irfft(H * H, nfft)[:m]
        u = c / (S * S)
        um = u.max()
        beta = beta_rel / max(abs(um), 1e-12)
        e = np.exp(beta * (u - um))
        Z = e.sum()
        w = e / Z
        L = um + np.log(Z) / beta
        W = np.fft.rfft(w, nfft)
        corr = np.fft.irfft(W * np.conj(H), nfft)[:n]
        g = 2.0 * corr / (S * S) - 2.0 * np.dot(w, c) / S ** 3
        return 2 * n * L, 2 * n * g

    return fg


def _smooth(h, betas, minimize, t_end, iters=1000):
    n = len(h)
    nfft = 1
    while nfft < 2 * n:
        nfft *= 2
    bestv = _val(h)
    best = h.copy()
    for b in betas:
        if time.time() > t_end:
            break
        try:
            res = minimize(_make(n, b, nfft), h, jac=True, method="L-BFGS-B",
                           options={"maxiter": iters, "maxfun": iters * 2,
                                    "maxcor": 30, "ftol": 1e-15, "gtol": 1e-12})
        except Exception:
            break
        hn = res.x
        s = hn.sum()
        if not np.all(np.isfinite(hn)) or abs(s) < 1e-9:
            break
        hn = hn / s
        h = hn
        v = _val(hn)
        if v < bestv:
            bestv, best = v, hn.copy()
    return best, bestv


def _stage(h, t_end, linprog, delta0=0.1):
    n = len(h)
    h = h / h.sum()
    best = h.copy()
    bestv = _val(h)
    delta = delta0 / n
    cost = np.zeros(n + 1)
    cost[n] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    J = np.arange(n)
    while time.time() < t_end and delta > 1e-9 / n:
        c0 = np.convolve(h, h)
        mx = c0.max()
        marg = (2.0 * delta * np.abs(h).sum() + n * delta * delta) * 2.1
        idx = np.nonzero(c0 >= mx - marg)[0]
        K = idx[:, None] - J[None, :]
        valid = (K >= 0) & (K < n)
        A = np.zeros((len(idx), n + 1))
        A[:, :n] = np.where(valid, 2 * h[np.clip(K, 0, n - 1)], 0.0)
        A[:, n] = -1.0
        bounds = [(-delta, delta)] * n + [(None, None)]
        try:
            res = linprog(cost, A_ub=A, b_ub=-c0[idx], A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": max(0.5, t_end - time.time())})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            delta /= 2
            continue
        hn = h + res.x[:n]
        s = hn.sum()
        if abs(s) < 1e-12:
            delta /= 2
            continue
        hn = hn / s
        v = _val(hn)
        if v < bestv - 1e-13:
            bestv = v
            best = hn.copy()
            h = hn
            delta = min(delta * 1.5, 1.0 / n)
        else:
            delta /= 2
    return best, bestv


def construct():
    t0 = time.time()
    rng = np.random.default_rng(3)
    best = np.ones(30) / 30
    bestv = _val(best)
    try:
        from scipy.optimize import linprog, minimize
        betas = [30, 100, 300, 1000, 3000, 10000, 30000]
        cands = []
        for k in range(5):
            x = 1.0 + 0.3 * rng.standard_normal(50)
            x /= x.sum()
            x, v = _smooth(x, betas, minimize, t0 + 8, 600)
            cands.append((v, x))
            if time.time() - t0 > 8:
                break
        cands.sort(key=lambda t: t[0])
        v, x = cands[0]
        best, bestv = x.copy(), v
        cur = x
        for n, tend in [(100, 13), (200, 20), (400, 29), (800, 40)]:
            cur = np.repeat(cur, n // len(cur))
            cur = cur / cur.sum()
            if time.time() - t0 > tend:
                break
            bl = betas[2:] if n < 800 else [1000, 3000, 10000, 30000, 100000]
            cur, v = _smooth(cur, bl, minimize, t0 + tend, 1000)
            if np.isfinite(v) and v < bestv + 0.02:
                best, bestv = cur.copy(), v
            elif np.isfinite(v) and n < 800:
                best, bestv = cur.copy(), v
        if time.time() - t0 < 52:
            h2, v2 = _stage(best, t0 + 57, linprog, 0.1)
            if v2 < bestv:
                best, bestv = h2.copy(), v2
    except Exception:
        pass
    if bestv >= 2.0 or not np.isfinite(bestv):
        return [1.0] * 16
    return [float(x) for x in best]