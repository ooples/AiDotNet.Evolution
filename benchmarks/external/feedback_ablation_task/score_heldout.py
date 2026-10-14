"""The ablation's primary endpoint: accuracy on inputs the evaluator never shows.

Run by the driver, never by the search. The inputs deliberately extend past the visible range in
both directions, so a candidate that memorised the twelve visible pairs - or fitted a polynomial
only where it was shown points - scores here exactly as badly as it deserves.

Usage: score_heldout.py <candidate.py>   ->   one JSON object on standard output.
"""
import json
import sys


def hidden_rule(x):
    return (x ** 3 + 7 * x) % 101


# Every integer in the range except the six the evaluator shows. A candidate that memorised those six
# scores zero here; one that recovered the rule scores one. The range is wide enough that a polynomial
# fitted only near the visible points does not survive either.
VISIBLE_INPUTS = (-4, -1, 2, 5, 9, 13)
HELD_OUT = [x for x in range(-30, 31) if x not in VISIBLE_INPUTS]


def score(source):
    namespace = {}
    try:
        exec(compile(source, "<candidate>", "exec"), namespace)
    except Exception as error:
        return {"heldout_accuracy": 0.0, "compiled": False, "detail": type(error).__name__ + ": " + str(error)[:300]}

    solve = namespace.get("solve")
    if not callable(solve):
        return {"heldout_accuracy": 0.0, "compiled": True, "detail": "no callable solve(x)"}

    correct = 0
    for x in HELD_OUT:
        try:
            if solve(x) == hidden_rule(x):
                correct += 1
        except Exception:
            pass
    return {"heldout_accuracy": correct / len(HELD_OUT), "compiled": True, "held_out_points": len(HELD_OUT)}


if __name__ == "__main__":
    with open(sys.argv[1], encoding="utf-8") as handle:
        print(json.dumps(score(handle.read())))
