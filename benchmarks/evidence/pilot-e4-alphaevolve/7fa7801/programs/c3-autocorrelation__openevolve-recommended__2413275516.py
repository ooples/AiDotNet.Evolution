import time
import numpy as np


def _value(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    n = len(h)
    c = np.convolve(h, h)
    return 2.0 * n * abs(c.max()) / s ** 2


def _stage(x, budget, p0, p1, lr0, state):
    t0 = time.time()
    n = len(x)
    size = 1 << (2 * n - 2).bit_length()
    m = np.zeros(n)
    v = np.zeros(n)
    b1, b2, eps = 0.9, 0.999, 1e-8
    it = 0
    while time.time() - t0 < budget:
        it += 1
        frac = min(1.0, (time.time() - t0) / budget)
        p = p0 * ((p1 / p0) ** frac)
        lr = lr0 * (1.0 - frac) ** 2 + lr0 * 0.03
        h = x * x
        F = np.fft.rfft(h, size)
        c = np.fft.irfft(F * F, size)[: 2 * n - 1]
        cmax = c.max()
        if cmax <= 0:
            x = np.abs(x) + 0.1
            continue
        r = np.clip(c / cmax, 0.0, None)
        rp1 = r ** (p - 1)
        S = (rp1 * r).sum()
        w = rp1 / (cmax * S)
        g_h = 2.0 * np.correlate(w, h, "valid")
        g_h = g_h - 2.0 / h.sum()
        g = g_h * 2.0 * x
        m = b1 * m + (1 - b1) * g
        v = b2 * v + (1 - b2) * g * g
        mh = m / (1 - b1 ** it)
        vh = v / (1 - b2 ** it)
        x = x - lr * mh / (np.sqrt(vh) + eps)
        if it % 20 == 0:
            x = x / np.mean(np.abs(x))
            val = _value(x * x)
            if val < state["v"]:
                state["v"] = val
                state["h"] = (x * x).copy()
    x = x / np.mean(np.abs(x))
    val = _value(x * x)
    if val < state["v"]:
        state["v"] = val
        state["h"] = (x * x).copy()
    return x


def _slp(h, deadline, state):
    from scipy.optimize import linprog

    n = len(h)
    h = h / h.mean()
    delta = 0.05
    cur = _value(h)
    K = 2 * n - 1
    cost = np.zeros(n + 1)
    cost[n] = 1.0
    Aeq = np.zeros((1, n + 1))
    Aeq[0, :n] = 1.0
    while time.time() < deadline and delta > 1e-6:
        c0 = np.convolve(h, h)
        A = np.zeros((K, n + 1))
        for i in range(n):
            A[i:i + n, i] = 2.0 * h
        A[:, n] = -1.0
        b = -c0
        lo = np.maximum(-h, -delta * h.max())
        hi = np.full(n, delta * h.max())
        bounds = [(lo[i], hi[i]) for i in range(n)] + [(0, None)]
        remaining = max(1.0, deadline - time.time())
        res = linprog(cost, A_ub=A, b_ub=b, A_eq=Aeq, b_eq=[0.0],
                      bounds=bounds, method="highs",
                      options={"time_limit": float(remaining)})
        if res.status != 0 or res.x is None:
            delta *= 0.5
            continue
        hn = np.clip(h + res.x[:n], 0, None)
        if hn.mean() <= 0:
            delta *= 0.5
            continue
        hn = hn / hn.mean()
        val = _value(hn)
        if val < cur:
            h, cur = hn, val
            if cur < state["v"]:
                state["v"] = cur
                state["h"] = h.copy()
        else:
            delta *= 0.5
    return h


def construct():
    t0 = time.time()
    rng = np.random.default_rng(0)
    n = 100
    x = 1.0 + 0.1 * rng.standard_normal(n)
    state = {"h": np.ones(n), "v": _value(np.ones(n))}
    stages = [
        (8.0, 8.0, 256.0, 0.02),
        (9.0, 64.0, 1024.0, 0.01),
        (9.0, 256.0, 2048.0, 0.005),
    ]
    try:
        for i, (b, p0, p1, lr) in enumerate(stages):
            if i > 0:
                x = np.repeat(x, 2)
                x = x / np.mean(np.abs(x))
            x = _stage(x, b, p0, p1, lr, state)
    except Exception:
        pass
    try:
        _slp(state["h"].copy(), t0 + 51.0, state)
    except Exception:
        pass
    return [float(a) for a in state["h"]]