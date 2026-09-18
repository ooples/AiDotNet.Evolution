"""Pre-data decisions for the warm head-to-head; no provider or task execution."""
import hashlib
import math
import random
import statistics

from docker_sandbox import encode
from warm_panel import DEFINITIONS

TRACKS = [("controlled", "aidotnet"), ("controlled", "openevolve"),
          ("controlled", "one-shot"), ("controlled", "single-parent"),
          ("native-bounded", "aidotnet"), ("native-bounded", "openevolve")]
PROFILES = [("uniform", "default"), ("best", "best")]
CONTRASTS = [("controlled", "openevolve"), ("controlled", "one-shot"),
             ("controlled", "single-parent"), ("native-bounded", "openevolve")]


def digest(value):
    return hashlib.sha256(encode(value)).hexdigest()


def grid(phase, seeds, profiles):
    if phase not in ("development", "selection", "final") or not seeds or len(set(seeds)) != len(seeds):
        raise ValueError("Invalid study phase/seeds")
    if any(type(s) is not int or not 0 <= s < 2**32 for s in seeds):
        raise ValueError("Invalid search seeds")
    if set(profiles) != {"aidotnet", "openevolve"} or profiles["aidotnet"] not in ("uniform", "best") or profiles["openevolve"] not in ("default", "best"):
        raise ValueError("Invalid frozen profiles")
    rows = []
    for seed in seeds:
        block = []
        for task, definition in DEFINITIONS.items():
            if definition["partition"] != phase:
                continue
            for evolution, oe in PROFILES if phase == "development" else [(profiles["aidotnet"], profiles["openevolve"])]:
                block.append(dict(task=task, seed=seed, evolution_profile=evolution, openevolve_profile=oe,
                                  tracks=[list(t) for t in TRACKS if phase != "development" or t[0] == "native-bounded"]))
        random.Random(seed).shuffle(block)
        rows.extend(block)
    return rows


def call_cap(rows, iterations=4):
    return sum(1 if method == "one-shot" else iterations for row in rows for _, method in row["tracks"])


def choose_profiles(rows):
    """Equal task/search-seed weight; retain default on an exact tie."""
    chosen = {}
    for method, key, options in (("aidotnet", "evolution_profile", ("uniform", "best")),
                                  ("openevolve", "openevolve_profile", ("default", "best"))):
        scores, cells = {}, {}
        for option in options:
            selected = [r for r in rows if r[key] == option]
            cells[option] = {(r["task"], r["seed"]) for r in selected}
            if not selected or len(cells[option]) != len(selected):
                raise ValueError("Missing/duplicated tuning cells")
            scores[option] = statistics.mean(math.log(next(p["speedup"] for p in r["pairs"] if p["method"] == method)) for r in selected)
        if cells[options[0]] != cells[options[1]]:
            raise ValueError("Unequal tuning grids")
        chosen[method] = max(options, key=lambda p: scores[p])
    return chosen


def power_requirement(rows, *, effect=0.20, alpha=0.05, power=0.8):
    """Conservative pilot-variance planning, NOT a guarantee of achieved power.

    Use the largest selection task/contrast SD, its 95% chi-square upper bound,
    and Bonferroni over 3 final tasks x 4 contrasts. Final inference uses paired
    search-level t intervals. Within-search container repeats are never n.
    """
    from scipy.stats import chi2, norm, t
    deviations = []
    tasks = [task for task, definition in DEFINITIONS.items() if definition["partition"] == "selection"]
    for task in tasks:
        cells = [r for r in rows if r["task"] == task]
        if len(cells) < 4 or len({r["seed"] for r in cells}) != len(cells):
            raise ValueError("Need at least four independent selection searches per task")
        for mode, competitor in CONTRASTS:
            logs = []
            for row in cells:
                pairs = {(p["mode"], p["method"]): p for p in row["pairs"]}
                logs.append(math.log(pairs[(mode, competitor)]["speedup"] / pairs[(mode, "aidotnet")]["speedup"]))
            degrees = len(logs) - 1
            deviations.append(statistics.stdev(logs) * math.sqrt(degrees / chi2.ppf(.05, degrees)))
    sigma = max(deviations)
    delta = -math.log(1 - effect)
    for n in range(12, 10001):
        if n >= ((t.ppf(1-alpha/24, n-1) + norm.ppf(power)) * sigma / delta) ** 2:
            return dict(searches_per_task=n, upper_pilot_sd=sigma, relative_effect=effect,
                        planned_power=power, family_alpha=alpha, comparisons=12,
                        limitation="Pilot variance may not transfer to unseen families; planning is not proof")
    raise ValueError("Required search count exceeds supported plan")
