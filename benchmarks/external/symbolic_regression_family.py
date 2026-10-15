"""V1-04d symbolic regression: train/validation/test splits with a sealed test split, and NMSE.

The story (#113) asks for held-out scoring so accuracy claims generalise beyond the search set.
Two things make that true rather than asserted, and both live here:

- **The test split is sealed by hash.** `seal` digests the test rows, and `score` refuses a split
  whose rows no longer match the seal it carries. A search that reaches into the test data, or a
  harness that regenerates it with a different seed, is a refusal and not a better number.
- **NMSE is computed here, not by the candidate.** A candidate supplies only `predict(*xs)`. This
  module builds the data from a seed with a closed form, calls the candidate, and reduces. The
  candidate never sees the test rows and never reports its own score.

NMSE is the mean squared error over the variance of the held-out targets, so 0 is exact, 1 is as
good as predicting the mean, and above 1 is worse than that. Variance, not mean square, because a
task with a large offset would otherwise flatter every candidate.

**This is not the LLM-SRBench subset, and does not claim to be.** `deep-symbolic-mathematics/llm-srbench`
declares no license, so it is not pinned into this repository or its CI; that is a decision for the
project owner, not this module. The tasks below are a small in-repo family with the same shape -
closed-form ground truth, held-out splits - so the instrument is testable now and a licensed subset
can be dropped in behind the same two functions later.
"""
from __future__ import annotations

import hashlib
import json
import math
import random

FAMILY = "symbolic-regression"
DEV_SEEDS = (5000, 5001, 5002)
TEST_SEED_OFFSET = 7919  # a prime, so the test grid never coincides with a train grid

# name -> (variables, closed form, sampling range per variable)
TASKS = {
    "damped_oscillator": dict(
        variables=("t",),
        formula=lambda t: math.exp(-0.3 * t) * math.sin(2.0 * t),
        ranges=((0.0, 6.0),),
        description="exp(-0.3 t) sin(2 t)"),
    "rational_gain": dict(
        variables=("x",),
        formula=lambda x: (3.0 * x + 1.0) / (x * x + 2.0),
        ranges=((-3.0, 3.0),),
        description="(3x + 1) / (x^2 + 2)"),
    "two_variable_ratio": dict(
        variables=("x", "y"),
        formula=lambda x, y: (x * y) / (1.0 + x * x + y * y),
        ranges=((-2.0, 2.0), (-2.0, 2.0)),
        description="xy / (1 + x^2 + y^2)"),
}


def _rows(task, seed, count):
    """Builds `count` rows for `task` from `seed`. Deterministic, and independent of row order."""
    specification = TASKS[task]
    stream = random.Random(seed)
    rows = []
    for _ in range(count):
        xs = tuple(stream.uniform(low, high) for low, high in specification["ranges"])
        rows.append((xs, specification["formula"](*xs)))
    return rows


def seal(rows):
    """Digests rows to 16 significant digits, so a seal survives a round trip through JSON."""
    payload = [[["%.16e" % value for value in xs], "%.16e" % y] for xs, y in rows]
    return hashlib.sha256(json.dumps(payload, separators=(",", ":")).encode()).hexdigest()


def build(task, seed, train=60, validation=20, test=40):
    """Returns a split whose test rows are drawn from a different seed and sealed by hash.

    The test seed is offset by a prime rather than being `seed + 1`, because consecutive seeds of
    the same generator are a common way to accidentally share a grid between train and test.
    """
    if task not in TASKS: raise KeyError("Unknown task: " + task)
    if min(train, validation, test) < 4: raise ValueError("Each split needs at least 4 rows.")
    held_out = _rows(task, seed + TEST_SEED_OFFSET, test)
    return {
        "family": FAMILY,
        "task": task,
        "seed": seed,
        "description": TASKS[task]["description"],
        "variables": list(TASKS[task]["variables"]),
        "train": _rows(task, seed, train),
        "validation": _rows(task, seed + 1, validation),
        "test": held_out,
        "test_seal": seal(held_out),
    }


def nmse(predict, rows):
    """Mean squared error over the variance of `rows`' targets. Non-finite predictions score inf."""
    targets = [y for _, y in rows]
    mean = sum(targets) / len(targets)
    variance = sum((y - mean) ** 2 for y in targets) / len(targets)
    if variance <= 0: raise ValueError("Degenerate rows: the targets have no variance to normalise by.")
    total = 0.0
    for xs, y in rows:
        try:
            prediction = float(predict(*xs))
        except Exception:  # noqa: BLE001 - a candidate that raises scores worst, it does not abort the run
            return math.inf
        if not math.isfinite(prediction): return math.inf
        total += (prediction - y) ** 2
    return (total / len(rows)) / variance


def score(predict, split):
    """Scores a candidate on every split, refusing a test split that no longer matches its seal."""
    if seal(split["test"]) != split["test_seal"]:
        raise ValueError("The test split does not match its seal, so its score would not be held out.")
    return {
        "task": split["task"],
        "seed": split["seed"],
        "train_nmse": nmse(predict, split["train"]),
        "validation_nmse": nmse(predict, split["validation"]),
        "test_nmse": nmse(predict, split["test"]),
        "test_seal": split["test_seal"],
    }
