import time
import numpy as np


def _project(x, target):
    m = np.max(np.abs(x))
    lo, hi = -2.0 - m, 2.0 + m
    for _ in range(80):
        mid = 0.5 * (lo + hi)
        if np.clip(x - mid, 0.0, 1.0).sum() > target:
            lo = mid
        else:
            hi = mid
    return np.clip(x - 0.5 * (lo + hi), 0.0, 1.0)


def _value(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1.0 - h, mode="full")) / n


def _optimize(h, iters, lr, beta0, beta1, deadline):
    n = len(h)
    target = n / 2.0
    L = 1 << int(np.ceil(np.log2(2 * n)))
    h = _project(h, target)
    best_h, best_v = h.copy(), _value(h)
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2 = 0.9, 0.999
    for it in range(1, iters + 1):
        if time.time() > deadline:
            break
        frac = it / iters
        beta = beta0 + (beta1 - beta0) * frac
        g = 1.0 - h
        Fh = np.fft.rfft(h, L)
        Fg = np.fft.rfft(g, L)
        Fgr = np.fft.rfft(g[::-1], L)
        C = np.fft.irfft(Fh * Fgr, L)[:2 * n - 1]
        w = np.exp(beta * (C - C.max()) / n)
        w /= w.sum()
        Fw = np.fft.rfft(w, L)
        Fhr = np.fft.rfft(h[::-1], L)
        t1 = np.fft.irfft(Fw * Fg, L)[n - 1:2 * n - 1]
        t2 = np.fft.irfft(Fw * Fhr, L)[n - 1:2 * n - 1][::-1]
        grad = t1 - t2
        m = b1 * m + (1 - b1) * grad
        v = b2 * v + (1 - b2) * grad * grad
        mh = m / (1 - b1 ** it)
        vh = v / (1 - b2 ** it)
        step = lr * (1 - 0.9 * frac)
        h = _project(h - step * mh / (np.sqrt(vh) + 1e-12), target)
        if it % 10 == 0:
            val = _value(h)
            if val < best_v:
                best_v, best_h = val, h.copy()
    val = _value(h)
    if val < best_v:
        best_v, best_h = val, h.copy()
    return best_h, best_v


def _jac(h):
    n = len(h)
    one_m = 1.0 - h
    s = np.arange(-(n - 1), n)[:, None]
    m = np.arange(n)[None, :]
    a = m - s
    b = m + s
    J = np.where((a >= 0) & (a < n), one_m[np.clip(a, 0, n - 1)], 0.0)
    J -= np.where((b >= 0) & (b < n), h[np.clip(b, 0, n - 1)], 0.0)
    return J


def _slp(h, r, deadline):
    from scipy.optimize import linprog
    n = len(h)
    target = n / 2.0
    h = _project(h, target)
    f = _value(h)
    c = np.zeros(n + 1)
    c[-1] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    while r > 1e-9:
        rem = deadline - time.time()
        if rem < 1.5:
            break
        C = np.correlate(h, 1.0 - h, mode="full")
        J = _jac(h)
        A = np.hstack([J, -np.ones((J.shape[0], 1))])
        lo = np.maximum(-r, -h)
        hi = np.minimum(r, 1.0 - h)
        bounds = list(zip(lo, hi)) + [(None, None)]
        try:
            res = linprog(c, A_ub=A, b_ub=-C, A_eq=Aeq, b_eq=[0.0],
                          bounds=bounds, method="highs",
                          options={"time_limit": max(0.5, rem - 1.0)})
        except Exception:
            break
        if res.status != 0 or res.x is None:
            r /= 2
            continue
        hn = h + res.x[:n]
        if abs(hn.sum() - target) > 1e-10:
            hn = _project(hn, target)
        fn = _value(hn)
        if fn < f - 1e-13:
            h, f = hn, fn
            r = min(r * 1.5, 0.05)
        else:
            r /= 2
    return h, f


def construct():
    t0 = time.time()
    rng = np.random.default_rng(0)
    n = 100
    x = 0.5 + 0.05 * rng.standard_normal(n)

    stages = [
        (100, 12000, 0.01, 50.0, 4000.0, 5.0),
        (200, 10000, 0.0035, 450.0, 6000.0, 7.0),
        (400, 9000, 0.002, 1000.0, 10000.0, 10.0),
    ]
    best_h = x
    h = x
    cum = 0.0
    for size, iters, lr, b0, b1, dur in stages:
        cum += dur
        deadline = t0 + cum
        if len(h) != size:
            h = np.repeat(h, size // len(h))
        h, val = _optimize(h, iters, lr, b0, b1, deadline)
        best_h = h.copy()

    best_h = _project(best_h, len(best_h) / 2.0)
    best_v = _value(best_h)

    try:
        h2, f2 = _slp(best_h, 0.005, t0 + 50.0)
        if f2 < best_v and abs(h2.sum() - len(h2) / 2.0) < 1e-9 * len(h2):
            best_h, best_v = h2, f2
    except Exception:
        pass

    return [float(a) for a in best_h]