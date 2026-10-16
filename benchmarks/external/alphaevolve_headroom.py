"""How far a floor gets on AlphaEvolve's published analytic constructions, with no model.

    python alphaevolve_headroom.py [--iterations 20000] [--output rows.json]

`benchmarks/evidence/alphaevolve-math/` transcribes 36 published problems and says plainly that it
is a target list, not a result: nothing in this repository claims to reach any of those figures. The
v1 claim needs the distance to them measured, and measured the same way the symbolic-regression
family already is - by a deliberately weak search, so a task the floor already solves cannot carry a
claim about a better searcher, and a task it cannot touch is where one would show.

Scope, stated rather than implied: the five height-vector problems of section B (c1, c2, c3, c3' and c5). The rest of the 36 take point sets, triangles or sphere centres, whose candidate shapes differ
per problem; they are listed as out of scope here rather than silently dropped. The objective is
always the module's own independent verifier - the same one whose self-test reproduces the published
value - so a candidate cannot be scored by the thing that produced it.

**No model, no spend.** The floor is seeded coordinate descent with restarts: no gradients, no
tuning, no engine. It is a floor, and a weak one on purpose.
"""
from __future__ import annotations

import argparse
import json
import random

import numpy as np

import alphaevolve_math as ae

def free(heights):
    """No constraint beyond what the verifier itself imposes."""
    return heights


def half_sum_in_unit_box(heights):
    """c5's feasible set: 0 <= f <= 1 with the sum exactly n/2.

    A random vector satisfies that with probability zero, so without this repair the floor produced
    no scorable candidate at all and the harness refused - correctly, because a floor that cannot
    enter the feasible set measures nothing. Clipping then rescaling to the required sum lands inside
    it; the rescale can push a value back over 1, so it is iterated and the result is checked by the
    verifier rather than assumed.
    """
    clipped = np.clip(np.asarray(heights, dtype=np.float64), 0.0, 1.0)
    target = clipped.size / 2
    for _ in range(64):
        total = clipped.sum()
        if abs(total - target) <= 1e-12 * clipped.size:
            return clipped
        room = (1.0 - clipped) if total < target else clipped
        slack = room.sum()
        if slack <= 0:
            return None
        clipped = np.clip(clipped + room * ((target - total) / slack), 0.0, 1.0)
    return None


# id -> (verifier, repair, direction, published, candidate lengths to try)
IN_SCOPE = {
    "c1-autocorrelation": (lambda v: ae.c1(v), free, "minimize", 1.5053, (8, 12, 16, 24, 32, 50)),
    "c2-autocorrelation": (lambda v: ae.c2(v), free, "maximize", 0.8962, (8, 12, 16, 24, 32, 50)),
    "c3-autocorrelation": (lambda v: ae.c3(v), free, "minimize", 1.4557, (8, 12, 16, 24, 32, 50)),
    "c3prime-autocorrelation": (lambda v: ae.c3(v, True), free, "minimize", 1.4688, (8, 12, 16, 24, 32, 50)),
    "c5-erdos-overlap": (lambda v: ae.c5(v), half_sum_in_unit_box, "minimize", 0.380924, (8, 12, 16, 24, 32)),
}
# c6 takes a set of INTEGERS with minimum 0, not a height vector: a float candidate is refused by
# its verifier, so it belongs here with the other candidate shapes rather than behind a repair
# that would quietly turn it into a different problem.
OUT_OF_SCOPE = ("c6-sums-differences-2003", "c6-sums-differences-54265", "hexagons-11", "hexagons-12", "maxmin-ratio-2d-16", "maxmin-ratio-3d-14",
                "heilbronn-triangle-11", "heilbronn-convex-13", "heilbronn-convex-14", "kissing-11",
                "circles-square-26", "circles-square-32", "circles-rectangle-21")
SEEDS = (1, 2, 3, 4, 5)
PROTOCOL = "alphaevolve-headroom-v2"

# The step schedule is absolute rather than a fraction of the budget, which is what makes the floor
# MONOTONE IN BUDGET: an N-iteration run draws exactly the prefix a longer run draws and takes the
# same steps at the same indices, and acceptance is greedy, so more iterations can only find a
# better candidate. Tied to `iterations` it was not monotone, and raising the budget from 8,000 to
# 20,000 moved c1 the wrong way (1.6561 -> 1.65784). That matters more than a tidier schedule: a
# floor that improves when run longer cannot be made to look weak by choosing its budget, so the gap
# reported here is a lower bound on the room above the floor and not an artefact of one setting.
STAGE = 4000
STAGE_HALVINGS = 5


def better(direction):
    return (lambda a, b: a < b) if direction == "minimize" else (lambda a, b: a > b)


def descend(objective, repair, direction, length, seed, iterations):
    """Seeded coordinate descent with a shrinking step. The floor: no gradients, no tuning."""
    wins = better(direction)
    rng = random.Random(seed)
    start = repair(np.array([rng.uniform(0.2, 1.0) for _ in range(length)]))
    if start is None:
        return None
    heights, best = start, objective(start)
    if best is None:
        return None
    for index in range(iterations):
        step = 0.25 * 0.5 ** min(index // STAGE, STAGE_HALVINGS)
        position = rng.randrange(length)
        trial = heights.copy()
        trial[position] = max(0.0, trial[position] + rng.gauss(0, step))
        repaired = repair(trial)
        if repaired is not None:
            value = objective(repaired)
            if value is not None and wins(value, best):
                best, heights = value, repaired
    return best


def measure(iterations):
    rows = []
    for identity, (objective, repair, direction, published, lengths) in IN_SCOPE.items():
        wins = better(direction)
        best, best_length = None, None
        for length in lengths:
            for seed in SEEDS:
                value = descend(objective, repair, direction, length, seed, iterations)
                if value is not None and (best is None or wins(value, best)):
                    best, best_length = value, length
        if best is None:
            raise ValueError("The floor produced no scorable candidate for " + identity)
        # The gap the published figure still holds over the floor, as a fraction of the published
        # value, oriented so positive always means "the floor is behind".
        gap = (best - published) / abs(published) if direction == "minimize" else (published - best) / abs(published)
        rows.append({
            "Problem": identity, "Direction": direction, "Published": published,
            "FloorBest": best, "FloorLength": best_length, "GapFraction": gap,
            "FloorReachesPublished": not (gap > 0),
            "Iterations": iterations, "Seeds": len(SEEDS), "Lengths": list(lengths),
        })
    return {"Protocol": PROTOCOL, "Source": ae.SOURCE_REPOSITORY, "SourceCommit": ae.SOURCE_COMMIT,
            "InScope": sorted(IN_SCOPE), "OutOfScope": list(OUT_OF_SCOPE), "Rows": rows}


def lines(report):
    out = ["floor vs published, %d problems in scope of %d transcribed (%d out of scope: other candidate shapes)"
           % (len(report["Rows"]), len(report["Rows"]) + len(report["OutOfScope"]), len(report["OutOfScope"]))]
    for row in report["Rows"]:
        out.append("%-26s %-8s published %-12s floor %-12s gap %+.2f%%  %s"
                   % (row["Problem"], row["Direction"], ("%.6g" % row["Published"]),
                      ("%.6g" % row["FloorBest"]), 100 * row["GapFraction"],
                      "FLOOR REACHES IT" if row["FloorReachesPublished"] else "room to compete"))
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--iterations", type=int, default=20000)
    parser.add_argument("--output")
    arguments = parser.parse_args()
    report = measure(arguments.iterations)
    print("\n".join(lines(report)))
    if arguments.output:
        with open(arguments.output, "x", encoding="utf-8") as handle:
            handle.write(json.dumps(report, indent=1) + "\n")
        print("wrote " + arguments.output)
    return report


if __name__ == "__main__":
    main()
