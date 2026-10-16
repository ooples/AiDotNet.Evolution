"""US-23 (#41): the paired reading of `benchmarks/EvolutionWarmStart`'s rows.

The story's metric is "tokens for a warm run to reach cold final quality, <= 50%". Tokens need a
model and a paid campaign; on a numeric objective the evaluation is the unit the budget is spent in,
so this measures the same shape in evaluations and says so rather than relabelling one as the other.

Two readings are kept apart on purpose. A seed where the **restored state already met the bar** cost
nothing to reach it - the strongest reuse outcome there is - and averaging those zeros into the ratio
would flatter the mechanism on exactly the seeds where it did most. They are counted separately, and
the ratio interval is computed over the seeds that still had work to do.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze import interval  # noqa: E402

SCHEMA = "evolution-warm-start-v1"
PROTOCOL = "warm-start-v1"
BOOTSTRAP_SAMPLES = 2000
BOOTSTRAP_SEED = 41
ALPHA = 0.05
TARGET = 0.50


def difference(values, unit):
    mean = sum(values) / len(values)
    bounds = interval([[value] for value in values], BOOTSTRAP_SAMPLES, BOOTSTRAP_SEED, ALPHA, resample_tasks=True)
    return {"Mean": mean, "LowerBound95": bounds[0], "UpperBound95": bounds[1], "Seeds": len(values), "Unit": unit}


def analyse(report):
    if report.get("Protocol") != PROTOCOL:
        raise ValueError("Unexpected protocol: " + str(report.get("Protocol")))
    rows = report["Rows"]
    if not rows:
        raise ValueError("No rows to read.")
    for row in rows:
        if row["ColdEvaluationsToBar"] <= 0:
            raise ValueError("A cold run that never reached its own final quality cannot set a bar.")
        if row["BarMetOnResume"] != (row["WarmEvaluationsAfterResume"] == 0):
            raise ValueError("A row claims the bar was met on resume but charges evaluations for it, or the reverse.")
        expected = row["WarmEvaluationsAfterResume"] / row["ColdEvaluationsToBar"]
        if abs(row["EvaluationRatio"] - expected) > 1e-12:
            raise ValueError("The reported ratio is not the one the two counts produce.")
    worked = [row for row in rows if not row["BarMetOnResume"]]
    unreached = [row for row in rows if not row["WarmReachedBar"]]
    return {
        "Schema": SCHEMA,
        "Protocol": PROTOCOL,
        "Seeds": len(rows),
        "Budget": report["Budget"],
        "PrefixBudget": report["Prefix"],
        "SeedsBarMetOnResume": len(rows) - len(worked),
        "SeedsWarmNeverReachedBar": len(unreached),
        # Over the seeds that still had work to do after the resume.
        "EvaluationRatio": difference([row["EvaluationRatio"] for row in worked], "ratio") if worked else None,
        "WorstSeedRatio": max(row["EvaluationRatio"] for row in rows),
        "SeedsWithinTarget": sum(1 for row in rows if row["EvaluationRatio"] <= TARGET),
        "Target": TARGET,
        "TokensMeasured": False,
    }


def lines(summary):
    ratio = summary["EvaluationRatio"]
    out = ["seeds=%d budget=%d prefix=%d" % (summary["Seeds"], summary["Budget"], summary["PrefixBudget"])]
    if ratio is not None:
        out.append("evaluations to the cold bar, warm/cold %.4f CI95 [%.4f, %.4f] over %d seeds that needed work"
                   % (ratio["Mean"], ratio["LowerBound95"], ratio["UpperBound95"], ratio["Seeds"]))
    out.append("%d of %d seeds met the bar on resume at no cost; worst seed %.4f; %d of %d within the %.2f target"
               % (summary["SeedsBarMetOnResume"], summary["Seeds"], summary["WorstSeedRatio"],
                  summary["SeedsWithinTarget"], summary["Seeds"], summary["Target"]))
    if summary["SeedsWarmNeverReachedBar"]:
        out.append("%d seed(s) never reached the bar and are charged everything they spent"
                   % summary["SeedsWarmNeverReachedBar"])
    return out


def render(summary):
    return json.dumps(summary, indent=1) + "\n"


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: warm_start.py <rows.json>")
    summary = analyse(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8")))
    print("\n".join(lines(summary)))
    return summary


if __name__ == "__main__":
    main()
