"""Is there headroom on the symbolic-regression dev tasks, without any model calls?

V1-04d (#113) asks for headroom demonstrated on dev data only. The ground truth is in the family
module, so "a perfect candidate exists" is true by construction and demonstrates nothing. What has to
be shown is that a search can find a candidate materially better than the seed **while never seeing
the test split** - which is also what proves the instrument discriminates rather than rewarding noise.

The search here is a deliberately dumb, seeded enumeration over a fixed grammar: no model, no
gradients, no tuning. It is a floor, not this project's searcher, and a weak floor is the point. If a
random enumeration already closes most of the gap on a task, that task cannot support a claim about a
better searcher; if it closes little, the task has headroom left to compete over.

Selection uses train and validation only. The test split is scored **once**, at the end, through
`family.score`, which refuses a split whose seal no longer matches - so the reported held-out number
cannot have steered the search that produced it.
"""
from __future__ import annotations

import argparse
import json
import math
import random

import symbolic_regression_family as family

# Candidate shape: sum of up to THREE terms, each a coefficient times a basis function of one
# variable, plus a constant. Small enough to enumerate honestly, expressive enough that the three
# dev tasks are not all equally far away.
BASIS = (
    ("1", lambda v: 1.0),
    ("v", lambda v: v),
    ("v^2", lambda v: v * v),
    ("v^3", lambda v: v * v * v),
    ("sin(v)", math.sin),
    ("cos(v)", math.cos),
    ("exp(-v)", lambda v: math.exp(-v) if -700.0 < v < 700.0 else math.inf),
    ("1/(1+v^2)", lambda v: 1.0 / (1.0 + v * v)),
)
COEFFICIENTS = (-3.0, -2.0, -1.0, -0.5, -0.3, 0.3, 0.5, 1.0, 2.0, 3.0)
TERMS = 3


def _sample(stream, arity):
    """Draws one candidate: a list of (coefficient, basis index, variable index) plus an offset."""
    terms = [(stream.choice(COEFFICIENTS), stream.randrange(len(BASIS)), stream.randrange(arity))
             for _ in range(stream.randint(1, TERMS))]
    return terms, stream.choice((-1.0, -0.5, 0.0, 0.5, 1.0))


def _render(terms, offset, variables):
    parts = ["%g*%s" % (coefficient, BASIS[basis][0].replace("v", variables[index]))
             for coefficient, basis, index in terms]
    return " + ".join(parts) + (" + %g" % offset if offset else "")


def _predict(terms, offset):
    def predict(*xs):
        total = offset
        for coefficient, basis, index in terms:
            total += coefficient * BASIS[basis][1](xs[index])
        return total
    return predict


def search(task, seed, budget):
    """Returns the best candidate by train+validation NMSE. Never reads the test split."""
    split = family.build(task, seed)
    arity = len(split["variables"])
    stream = random.Random(seed * 31 + 17)
    best = None
    for _ in range(budget):
        terms, offset = _sample(stream, arity)
        predict = _predict(terms, offset)
        try:
            selection = (family.nmse(predict, split["train"]) + family.nmse(predict, split["validation"])) / 2.0
        except ValueError:
            continue
        if not math.isfinite(selection):
            continue
        if best is None or selection < best[0]:
            best = (selection, terms, offset)
    return split, best


def main():
    parser = argparse.ArgumentParser(description="Dev-data headroom for the symbolic-regression family.")
    parser.add_argument("--budget", type=int, default=20000, help="candidates enumerated per task and seed")
    parser.add_argument("--output")
    parser.add_argument("--noise-seeds", type=int, default=0,
                        help="also sweep this many consecutive seeds per task and report the spread")
    arguments = parser.parse_args()
    if not 0 <= arguments.noise_seeds <= 64:
        raise SystemExit("--noise-seeds must be between 0 and 64.")
    if not 100 <= arguments.budget <= 2_000_000:
        raise SystemExit("--budget must be between 100 and 2,000,000.")

    rows = []
    for task in family.TASKS:
        for seed in family.DEV_SEEDS:
            split, best = search(task, seed, arguments.budget)
            targets = [y for _, y in split["test"]]
            mean = sum(targets) / len(targets)
            # The seed any searcher has to beat: predict the training mean. Scored the same way.
            baseline = family.score(lambda *_: mean, split)
            found = family.score(_predict(best[1], best[2]), split)
            rows.append({
                "task": task, "seed": seed, "budget": arguments.budget,
                "expression": _render(best[1], best[2], split["variables"]),
                "selection_nmse": best[0],
                "baseline_test_nmse": baseline["test_nmse"],
                "found_test_nmse": found["test_nmse"],
                "gap_closed": 1.0 - found["test_nmse"] / baseline["test_nmse"],
                "test_seal": split["test_seal"],
            })
            print(json.dumps(rows[-1]), flush=True)

    # The spread a gate on this family would have to resolve. Measured the same way as the rows
    # above, over consecutive seeds from the first dev seed, so a reader can see how much of a
    # between-arm difference would be seed noise rather than a result. It is NOT a pre-registration
    # input: it is one arm against itself, and an arm that calls a model may be noisier still.
    noise = {}
    if arguments.noise_seeds:
        for task in family.TASKS:
            found = []
            for index in range(arguments.noise_seeds):
                seed = family.DEV_SEEDS[0] + index
                split, best = search(task, seed, arguments.budget)
                found.append(family.score(_predict(best[1], best[2]), split)["test_nmse"])
            found.sort()
            noise[task] = {
                "seeds": arguments.noise_seeds, "min": found[0], "max": found[-1],
                "median": found[len(found) // 2],
                "spread": found[-1] - found[0],
                "values": found,
            }
            print("%-20s test NMSE over %d seeds: %.3f to %.3f (spread %.3f)" % (
                task, arguments.noise_seeds, found[0], found[-1], found[-1] - found[0]), flush=True)

    payload = {
        "story": "V1-04d (#113): dev-data headroom, no model calls",
        "noise": noise,
        "noise_reading": ("One arm against itself across consecutive seeds. A between-arm difference "
                          "smaller than this spread is seed noise, not a result. Not a "
                          "pre-registration input: an arm that calls a model may be noisier."),
        "family": family.FAMILY,
        "searcher": "seeded random enumeration over a fixed 8-function basis, up to 3 terms; a floor, not this project's searcher",
        "selection": "train and validation only; the test split is scored once, at the end, through family.score",
        "rows": rows,
    }
    for task in family.TASKS:
        closed = [row["gap_closed"] for row in rows if row["task"] == task]
        print("%-20s gap closed on test: min %+.3f median %+.3f max %+.3f" % (
            task, min(closed), sorted(closed)[len(closed) // 2], max(closed)))
    if arguments.output:
        with open(arguments.output, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, indent=2)
        print("Wrote " + arguments.output)
    return 0


# Guarded, so importing this module cannot run the enumeration as a side effect of the import.
if __name__ == "__main__":
    raise SystemExit(main())
