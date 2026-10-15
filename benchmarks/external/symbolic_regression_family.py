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

**Residual risk, declared.** A candidate is a Python callable, so it runs in this process and could
monkeypatch `seal`, `nmse` or `math` and forge its own score. Two things bound that. The seal is
verified *before* any candidate code runs, so a tampered test split is refused by a check the
candidate has not yet had a chance to touch; and a real LLM candidate belongs behind the sandboxed
path this repo already has (`isolated_program_evaluator.py`, `ProgramSandboxOptions`), where it never
shares a process with the scorer. In-process scoring here is for the instrument's own tests and for a
trusted baseline searcher, and is not an isolation claim.

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
# The name benchmarks/analysis/release_gate.py's fixtures already use for this family, so a sealed
# partition written here lands in the slot the gate expects rather than a second one beside it.
GATE_FAMILY = "sr"
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


def build(task, seed, train=60, validation=20, test=400):
    """Returns a split whose test rows are drawn from a different seed and sealed by hash.

    The test seed is offset by a prime rather than being `seed + 1`, because consecutive seeds of
    the same generator are a common way to accidentally share a grid between train and test.

    **Why the held-out split is ten times the training split.** At 40 test rows the floor's test NMSE
    on `damped_oscillator` swung 1.133 across eight seeds - larger than the ~0.3 of headroom the task
    has, so no comparison on it could have meant anything. The spread is mostly sampling noise in the
    held-out draw, and it collapses with size: 1.133 at 40 rows, 0.305 at 120, 0.183 at 400, 0.166 at
    1200. 400 is where it flattens. What is left after that is the searcher's own seed-to-seed
    variation, which more seeds address and a bigger test split does not.
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


def partition(seeds_by_task):
    """Returns the bytes a campaign seals as its test partition, in the gate's own shape.

    `benchmarks/analysis/release_gate.py` treats a partition as opaque: it checks that the opened
    bytes hash to `TestPartitionHashes[family]` and that **no blob in the registration commit's tree
    has exactly those bytes** (`partition_was_visible`). Hence the warning below.

    **The seeds passed here must not be committed.** This module builds its rows from a seed with a
    closed form, so anyone holding the seed can reproduce the held-out data exactly. A partition
    built from `DEV_SEEDS`, or from any seed in the tree, would pass the gate's byte-visibility check
    while being public in substance - which is the failure the check exists to prevent. Choose the
    campaign's test seeds at registration time, seal these bytes into the registration, and keep both
    the seeds and the partition out of the repository until the bundle is opened.
    """
    if not seeds_by_task: raise ValueError("A test partition needs at least one task.")
    seeds = {}
    for task, task_seeds in seeds_by_task.items():
        if task not in TASKS: raise KeyError("Unknown task: " + task)
        values = list(task_seeds)
        if not values or any(type(seed) is not int for seed in values):
            raise ValueError("Each task needs at least one integer seed: " + task)
        if len(set(values)) != len(values): raise ValueError("Duplicate seed for task: " + task)
        seeds[task] = values
    payload = {"Family": GATE_FAMILY, "TestSeeds": {task: seeds[task] for task in sorted(seeds)}}
    return json.dumps(payload, separators=(",", ":"), sort_keys=True).encode()


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
    """Scores a candidate on every split, refusing a test split that no longer matches its seal.

    The seal is checked first, before `predict` is called even once: a candidate that tampers with
    the scorer cannot undo a refusal that has already happened.
    """
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
