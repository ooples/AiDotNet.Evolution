import numpy as np
import time


def _val(h):
    n = len(h)
    return 2.0 * np.max(np.correlate(h, 1 - h, "full")) / n


def _fix_sum(h, target):
    h = np.clip(h, 0, 1)
    for _ in range(50):
        r = target - h.sum()
        if abs(r) < 1e-13:
            break
        room = (1 - h) if r > 0 else h
        tot = room.sum()
        if tot <= 0:
            break
        h = h + np.sign(r) * min(abs(r), tot) * room / tot
        h = np.clip(h, 0, 1)
    return h


def _slp(h, deadline, delta, linprog):
    N = len(h)
    ks = np.arange(2 * N - 1)
    s = ks - (N - 1)
    m = np.arange(N)
    M, S = np.meshgrid(m, s)
    i1 = M - S
    v1 = (i1 >= 0) & (i1 < N)
    i2 = M + S
    v2 = (i2 >= 0) & (i2 < N)
    i1c = np.clip(i1, 0, N - 1)
    i2c = np.clip(i2, 0, N - 1)
    cost = np.zeros(N + 1)
    cost[-1] = 1
    Aeq = np.hstack([np.ones((1, N)), np.zeros((1, 1))])
    cur = _val(h)
    best_h, best = h.copy(), cur
    while time.time() < deadline and delta > 1e-7:
        g = 1 - h
        c = np.correlate(h, g, "full")
        J = np.where(v1, g[i1c], 0.0) - np.where(v2, h[i2c], 0.0)
        lo = np.maximum(-delta, -h)
        hi = np.minimum(delta, 1 - h)
        A = np.hstack([J, -np.ones((2 * N - 1, 1))])
        try:
            res = linprog(cost, A_ub=A, b_ub=-c, A_eq=Aeq, b_eq=[0.0],
                          bounds=list(zip(lo, hi)) + [(None, None)],
                          method="highs")
        except Exception:
            break
        if res.status != 0:
            delta *= 0.5
            continue
        hn = _fix_sum(h + res.x[:N], N / 2)
        v = _val(hn)
        if v < cur - 1e-12:
            h, cur = hn, v
            if cur < best:
                best, best_h = cur, h.copy()
        else:
            delta *= 0.5
    return best_h, best


def _resample(h, M):
    N = len(h)
    x = (np.arange(N) + 0.5) / N
    y = (np.arange(M) + 0.5) / M
    return _fix_sum(np.interp(y, x, h), M / 2)


def construct():
    t0 = time.time()
    try:
        from scipy.optimize import linprog
        N = 40
        starts = []
        for seed in range(6):
            rng = np.random.default_rng(seed)
            h = np.clip(0.5 + 0.25 * rng.standard_normal(N), 0, 1)
            h = _fix_sum(h, N / 2)
            h, v = _slp(h, time.time() + 1.5, 0.2, linprog)
            starts.append((v, h))
        starts.sort(key=lambda x: x[0])
        h, v = starts[0][1], starts[0][0]
        h, v = _slp(h, t0 + 11, 0.03, linprog)
        overall, overall_h = v, h.copy()
        stages = [(60, 20, 0.01), (90, 30, 0.006),
                  (130, 40, 0.004), (180, 52, 0.003)]
        for M, dl, delta in stages:
            hs = _resample(h, M)
            h2, v2 = _slp(hs, t0 + dl, delta, linprog)
            h = h2
            if v2 < overall and v2 < 0.5:
                overall, overall_h = v2, h2.copy()
        out = _fix_sum(overall_h, len(overall_h) / 2)
        if _val(out) < 0.5:
            return [float(x) for x in out]
    except Exception:
        pass
    return [0.5] * 16