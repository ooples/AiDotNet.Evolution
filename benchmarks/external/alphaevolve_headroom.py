"""How far a floor gets on AlphaEvolve's published analytic constructions, with no model.

    python alphaevolve_headroom.py [--iterations 20000] [--output rows.json]

`benchmarks/evidence/alphaevolve-math/` transcribes 36 published problems and says plainly that it
is a target list, not a result: nothing in this repository claims to reach any of those figures. The
v1 claim needs the distance to them measured, and measured the same way the symbolic-regression
family already is - by a deliberately weak search, so a task the floor already solves cannot carry a
claim about a better searcher, and a task it cannot touch is where one would show.

Scope, stated rather than implied: the six problems of section B whose candidate is a vector of
reals - c1, c2, c3, c3prime (c3') and c5, which take a height vector, and c4-uncertainty-hermite,
which takes a signed coefficient vector. Every one of them is a key of `IN_SCOPE`, and the test requires this list
and that dictionary to agree. The other 30 of the 36 take tensor decompositions, point sets,
triangles, sphere centres or integer sets; each is listed out of scope with its reason, derived from
the manifest rather than written down, so none can be silently dropped. The objective is
always the module's own independent verifier - the same one whose self-test reproduces the published
value - so a candidate cannot be scored by the thing that produced it.

**No model, no spend.** The floor is seeded coordinate descent with restarts: no gradients, no
tuning, no engine. It is a floor, and a weak one on purpose.
"""
from __future__ import annotations

import argparse
import json
import pathlib
import random

import numpy as np

import alphaevolve_math as ae

def free(heights):
    """No constraint beyond what the verifier itself imposes - c4 takes a SIGNED coefficient vector."""
    return heights


def nonnegative(heights):
    """The height-vector problems' only constraint: every coordinate at or above zero.

    This belongs to the problem and not to the search, which is why it is a repair. The clamp used to
    live inside `descend`, where it silently applied to c4 as well and refused half of that
    problem's feasible set before the verifier ever saw a candidate.
    """
    return np.clip(np.asarray(heights, dtype=np.float64), 0.0, None)


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


# Every problem whose candidate is a vector of reals. `cap` bounds the iterations for a verifier too
# slow to run at the full budget - c4's is exact rational arithmetic - and the effective budget is
# recorded per row, so a capped problem cannot read as though it had the whole budget.
VECTOR = (8, 12, 16, 24, 32, 50)
IN_SCOPE = {
    "c1-autocorrelation": dict(objective=lambda v: ae.c1(v), repair=nonnegative, direction="minimize",
                               published=1.5053, lengths=VECTOR, cap=None),
    "c2-autocorrelation": dict(objective=lambda v: ae.c2(v), repair=nonnegative, direction="maximize",
                               published=0.8962, lengths=VECTOR, cap=None),
    # c3 and c3' are the SIGNED versions of c1 - `_heights(value, nonnegative=False)` - and that is
    # the whole difference between them. Repaired to non-negative they returned c1's number to the
    # last bit, because conv(h, h) >= 0 makes all three peaks the same quantity, so the study was
    # reporting one measurement against three published targets. They get the free repair.
    "c3-autocorrelation": dict(objective=lambda v: ae.c3(v), repair=free, direction="minimize",
                               published=1.4557, lengths=VECTOR, cap=None),
    "c3prime-autocorrelation": dict(objective=lambda v: ae.c3(v, True), repair=free,
                                    direction="minimize", published=1.4688, lengths=VECTOR, cap=None),
    "c5-erdos-overlap": dict(objective=lambda v: ae.c5(v), repair=half_sum_in_unit_box,
                             direction="minimize", published=0.380924, lengths=(8, 12, 16, 24, 32),
                             cap=None),
    "c4-uncertainty-hermite": dict(objective=lambda v: ae.hermite_bound(v), repair=free,
                                   direction="minimize", published=0.3521, lengths=(1, 2, 3),
                                   cap=1000),
}

# Why each transcribed problem the floor does not touch is not touched. The partition is checked
# against the manifest rather than listed by hand: the first version of this file named 13
# exclusions and the notebook transcribes 36, so 18 tensor problems and both c4 problems were
# missing from it - and one of the two turned out to be in scope.
TENSOR = ("the candidate is a rank-R decomposition - three factor matrices over a ring - and not a "
          "vector of reals")
GEOMETRY = ("the candidate is a point set, triangle, hexagon or set of sphere centres, whose shape "
            "and feasibility differ per problem")
INTEGER_SET = ("the candidate is a set of INTEGERS with minimum 0, so a float vector is refused by "
               "its own verifier, and a repair that rounded would quietly change the problem")
UNCHECKED_ADMISSIBILITY = (
    "its verifier computes the bound from a construction it ASSUMES admissible and never checks that "
    "assumption: a random vector of twelve positive reals scores 0.101685 against the published "
    "0.3216, so minimising it rewards an inadmissible candidate rather than a better one. Scoring a "
    "search against this would manufacture a win over AlphaEvolve out of an invalid construction")
BY_ID = {
    "c6-sums-differences-2003": INTEGER_SET,
    "c6-sums-differences-54265": INTEGER_SET,
    "c4-uncertainty-laguerre": UNCHECKED_ADMISSIBILITY,
}


def out_of_scope(manifest_path):
    """Every transcribed problem not in scope, with its reason. Raises if the partition is not whole."""
    manifest = json.loads(pathlib.Path(manifest_path).read_text(encoding="utf-8"))
    rows = []
    for problem in manifest["rows"]:
        identity = problem["id"]
        if identity in IN_SCOPE:
            continue
        reason = BY_ID.get(identity, TENSOR if problem["kind"] == "tensor" else GEOMETRY)
        rows.append({"Problem": identity, "Kind": problem["kind"], "Reason": reason})
    if len(rows) + len(IN_SCOPE) != manifest["problems"]:
        raise ValueError(
            "The partition covers %d problems and the manifest transcribes %d: a problem in neither "
            "list is one silently dropped from the inventory."
            % (len(rows) + len(IN_SCOPE), manifest["problems"]))
    missing = sorted(set(IN_SCOPE) - {problem["id"] for problem in manifest["rows"]})
    if missing:
        raise ValueError("In scope but not transcribed, so there is no published value to be behind: "
                         + ", ".join(missing))
    return rows


MANIFEST = pathlib.Path(__file__).resolve().parents[1] / "evidence/alphaevolve-math/manifest.json"
SEEDS = (1, 2, 3, 4, 5)
PROTOCOL = "alphaevolve-headroom-v4"

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
        trial[position] = trial[position] + rng.gauss(0, step)
        repaired = repair(trial)
        if repaired is not None:
            value = objective(repaired)
            if value is not None and wins(value, best):
                best, heights = value, repaired
    return best


def measure(iterations, manifest_path=None):
    excluded = out_of_scope(manifest_path or MANIFEST)
    rows = []
    for identity, spec in IN_SCOPE.items():
        direction, published = spec["direction"], spec["published"]
        budget = iterations if spec["cap"] is None else min(iterations, spec["cap"])
        wins = better(direction)
        best, best_length = None, None
        for length in spec["lengths"]:
            for seed in SEEDS:
                value = descend(spec["objective"], spec["repair"], direction, length, seed, budget)
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
            "Iterations": budget, "Capped": budget < iterations,
            "Seeds": len(SEEDS), "Lengths": list(spec["lengths"]),
        })
    return {"Protocol": PROTOCOL, "Source": ae.SOURCE_REPOSITORY, "SourceCommit": ae.SOURCE_COMMIT,
            "RequestedIterations": iterations, "Transcribed": len(rows) + len(excluded),
            "InScope": sorted(IN_SCOPE), "OutOfScope": excluded, "Rows": rows}


def lines(report):
    out = ["floor vs published, %d of %d transcribed problems in scope (%d out of scope, each with a reason)"
           % (len(report["Rows"]), report["Transcribed"], len(report["OutOfScope"]))]
    for row in report["Rows"]:
        out.append("%-26s %-8s published %-12s floor %-12s gap %+.2f%%  %s%s"
                   % (row["Problem"], row["Direction"], ("%.6g" % row["Published"]),
                      ("%.6g" % row["FloorBest"]), 100 * row["GapFraction"],
                      "FLOOR REACHES IT" if row["FloorReachesPublished"] else "room to compete",
                      " (capped at %d iterations)" % row["Iterations"] if row["Capped"] else ""))
    for row in report["OutOfScope"]:
        out.append("  out of scope  %-28s %s" % (row["Problem"], row["Reason"][:96]))
    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--iterations", type=int, default=20000)
    parser.add_argument("--output")
    parser.add_argument("--manifest", default=str(MANIFEST))
    arguments = parser.parse_args()
    report = measure(arguments.iterations, arguments.manifest)
    print("\n".join(lines(report)))
    if arguments.output:
        with open(arguments.output, "x", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(report, indent=1) + "\n")
        print("wrote " + arguments.output)
    return report


if __name__ == "__main__":
    main()
