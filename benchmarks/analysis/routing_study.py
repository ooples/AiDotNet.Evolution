"""US-19 (#37): the router read against the best single method it is supposed to beat.

`portfolio_study.py` already runs the registered comparison - every method on a development
partition, which *selects* the best static comparator per family, then every method again on a
confirmation partition, which *measures* against it. This takes that selection and pairs the
adaptive router against the chosen comparator by (family, seed), with bootstrap intervals from
`analyze.interval`.

Two properties of the instrument, stated with the numbers:

- **The comparator is selected on development and the claim file records it**, so it is not a method
  chosen after seeing the confirmation rows. `BestStatic` below is copied from the study's own claim.
- **Calls, not tokens.** These are numeric, program-shaped and kernel-shaped objectives with no model
  in the loop. `ObjectiveCalls` is the unit; a token figure would be an invention.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze import interval  # noqa: E402

SCHEMA = "evolution-routing-study-v1"
ROUTER = "adaptive-archive"
BOOTSTRAP_SAMPLES = 2000
BOOTSTRAP_SEED = 19
ALPHA = 0.05


def difference(deltas, unit):
    mean = sum(deltas) / len(deltas)
    bounds = interval([[delta] for delta in deltas], BOOTSTRAP_SAMPLES, BOOTSTRAP_SEED, ALPHA, resample_tasks=True)
    return {"Mean": mean, "LowerBound95": bounds[0], "UpperBound95": bounds[1], "Pairs": len(deltas), "Unit": unit}


def paired(bundle, family, comparator):
    rows = {(row["Method"], row["Seed"]): row for row in bundle["Rows"] if row["Family"] == family}
    seeds = sorted({seed for method, seed in rows if method == ROUTER})
    pairs = []
    for seed in seeds:
        router, static = rows.get((ROUTER, seed)), rows.get((comparator, seed))
        if router is None or static is None:
            raise ValueError("Family %s seed %s is missing an arm." % (family, seed))
        if router["Status"] != "completed" or static["Status"] != "completed":
            raise ValueError("Family %s seed %s did not complete under both arms." % (family, seed))
        pairs.append((router, static))
    if not pairs:
        raise ValueError("No seed carries the router arm for family " + family)
    return pairs


def analyse(bundle):
    best = bundle["BestStatic"]
    families = []
    for family in sorted(best):
        comparator = best[family]
        pairs = paired(bundle, family, comparator)
        # The criterion is a ratio against the comparator, so it is computed per seed and then
        # averaged - not as a ratio of two averages, which would hide a seed that went badly.
        families.append({
            "Family": family,
            "Comparator": comparator,
            "QualityDifference": difference([router["Quality"] - static["Quality"] for router, static in pairs], "quality"),
            "ObjectiveCallRatio": difference(
                [router["ObjectiveCalls"] / static["ObjectiveCalls"] for router, static in pairs], "ratio"),
            "RouterCalls": sum(router["ObjectiveCalls"] for router, _ in pairs) / len(pairs),
            "ComparatorCalls": sum(static["ObjectiveCalls"] for _, static in pairs) / len(pairs),
        })
    return {
        "Schema": SCHEMA,
        "Router": ROUTER,
        "SourceSha256": bundle["SourceSha256"],
        "PlanHash": bundle["PlanHash"],
        "DevelopmentHash": bundle["DevelopmentHash"],
        "BestStatic": best,
        "Families": families,
        "TokensMeasured": False,
    }


def lines(summary):
    out = ["router=%s comparator selected on development: %s"
           % (summary["Router"], json.dumps(summary["BestStatic"], sort_keys=True))]
    for family in summary["Families"]:
        quality, ratio = family["QualityDifference"], family["ObjectiveCallRatio"]
        out.append("%-8s vs %-12s quality %+.4f CI95 [%+.4f, %+.4f]  calls x%.4f CI95 [%.4f, %.4f] (%.1f vs %.1f)"
                   % (family["Family"], family["Comparator"], quality["Mean"], quality["LowerBound95"],
                      quality["UpperBound95"], ratio["Mean"], ratio["LowerBound95"], ratio["UpperBound95"],
                      family["RouterCalls"], family["ComparatorCalls"]))
    return out


def render(summary):
    return json.dumps(summary, indent=1) + "\n"


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: routing_study.py <rows-compact.json>")
    summary = analyse(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8")))
    print("\n".join(lines(summary)))
    return summary


if __name__ == "__main__":
    main()
