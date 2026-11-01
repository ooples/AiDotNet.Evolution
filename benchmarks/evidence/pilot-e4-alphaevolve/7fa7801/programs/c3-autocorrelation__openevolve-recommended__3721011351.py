import time
import numpy as np


def _score(h):
    s = h.sum()
    if abs(s) < 1e-12:
        return float("inf")
    return 2 * len(h) * np.max(np.abs(np.convolve(h, h))) / s ** 2


def _run(h, ps, lr, deadline, state, decay=0.7):
    n = len(h)
    m = np.zeros(n)
    v = np.zeros(n)
    step = 0
    t_start = time.time()
    slice_t = (deadline - t_start) / len(ps)
    for idx, p in enumerate(ps):
        end = t_start + slice_t * (idx + 1)
        while time.time() < end:
            step += 1
            S = h.sum()
            c = np.convolve(h, h)
            ac = np.abs(c)
            cm = ac.max() + 1e-30
            r = ac / cm
            rp = r ** (p - 1)
            Mn = (np.sum(rp * r)) ** (1.0 / p)
            M = cm * Mn
            w = np.sign(c) * rp / (Mn ** (p - 1))
            corr = np.correlate(w, h, mode="valid")
            g = (2 * corr) / S ** 2 - 2 * M / S ** 3
            g = g / (np.linalg.norm(g) + 1e-30)
            m = 0.9 * m + 0.1 * g
            v = 0.999 * v + 0.001 * g * g
            mh = m / (1 - 0.9 ** step)
            vh = v / (1 - 0.999 ** step)
            h = h - lr * mh / (np.sqrt(vh) + 1e-8)
            sm = h.sum()
            if sm > 1e-9:
                h = h * (n / sm)
            if step % 20 == 0:
                val = _score(h)
                if val < state["v"]:
                    state["v"] = val
                    state["h"] = h.copy()
        lr *= decay
    return h


def construct():
    t0 = time.time()
    total = 53.0
    rng = np.random.default_rng(0)
    n = 50
    h = np.ones(n) + 0.1 * rng.standard_normal(n)
    state = {"v": _score(np.ones(16)), "h": np.ones(16)}
    sizes = [50, 100, 200, 400, 800]
    fracs = [0.08, 0.12, 0.18, 0.24, 0.28]  # sums to 0.90, rest for polish
    lrs = [0.02, 0.01, 0.006, 0.004, 0.003]
    ps = [16, 64, 256, 1024, 4096, 16384]
    acc = 0.0
    for i, sz in enumerate(sizes):
        if i > 0:
            hb = np.array(state["h"], dtype=float)
            if len(hb) * 2 == sz:
                h = np.repeat(hb, 2)
            else:
                h = np.repeat(h, 2)
            h = h * (len(h) / h.sum())
        acc += fracs[i]
        deadline = t0 + total * acc
        h = _run(h, ps, lrs[i], deadline, state)
        val = _score(h)
        if val < state["v"]:
            state["v"] = val
            state["h"] = h.copy()
    hb = np.array(state["h"], dtype=float)
    hb = hb * (len(hb) / hb.sum())
    _run(hb, [4096, 16384], 0.0008, t0 + total, state, decay=0.5)
    return [float(a) for a in state["h"]]