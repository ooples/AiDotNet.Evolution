"""US-17 (#35): the paired reading of `benchmarks/EvolutionRepair`'s rows.

The harness runs the same scripted proposals under `MaxRepairs = 0` and `MaxRepairs = 2`; this pairs
them by seed and puts a bootstrap interval on each difference, with `analyze.interval` doing the
resampling so the pilot and this share one estimator.

What the instrument can and cannot say is part of the output, not a footnote: `BrokenFirstReplyRate`
is an input to the harness, so `ValidProposalRateDifference` measures *recovery of broken proposals*,
not how often a real model proposes a broken patch. `TokensPerValidCandidate` is deliberately absent -
the scripted client reports a fixed 100/50 tokens per call, so any token figure here is a restatement
of the call count rather than a measurement.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze import interval  # noqa: E402

PROTOCOL = "repair-ablation-v1"
SCHEMA = "evolution-repair-ablation-v1"
BOOTSTRAP_SAMPLES = 2000
BOOTSTRAP_SEED = 17
ALPHA = 0.05
NO_REPAIR, REPAIR = 0, 2


def paired(rows):
    """Per-seed (no-repair, repair) pairs, refusing anything the harness did not report in both arms."""
    arms = {}
    for row in rows:
        arms.setdefault(int(row["MaxRepairs"]), {})[int(row["Seed"])] = row
    if set(arms) != {NO_REPAIR, REPAIR}:
        raise ValueError("The rows must carry exactly the two declared repair budgets, 0 and 2.")
    seeds = sorted(set(arms[NO_REPAIR]) & set(arms[REPAIR]))
    if len(seeds) != len(arms[NO_REPAIR]) or len(seeds) != len(arms[REPAIR]):
        raise ValueError("Every seed must appear under both arms, or the pairing is not paired.")
    return [(arms[NO_REPAIR][seed], arms[REPAIR][seed]) for seed in seeds]


def difference(pairs, value, seeds):
    """The mean paired difference and its bootstrap interval, resampling seeds as the unit."""
    deltas = [value(repair) - value(none) for none, repair in pairs]
    mean = sum(deltas) / len(deltas)
    bounds = interval([[delta] for delta in deltas], BOOTSTRAP_SAMPLES, BOOTSTRAP_SEED, ALPHA, resample_tasks=True)
    return {"Mean": mean, "LowerBound95": bounds[0], "UpperBound95": bounds[1], "Seeds": seeds,
            "PerSeed": deltas}


def analyse(report):
    if report.get("Protocol") != PROTOCOL:
        raise ValueError("Unexpected protocol: " + str(report.get("Protocol")))
    pairs = paired(report["Rows"])
    seeds = len(pairs)
    for none, repair in pairs:
        if none["Proposals"] != repair["Proposals"]:
            raise ValueError("A pair must run the same number of proposals under both arms.")
        if repair["Valid"] < none["Valid"]:
            raise ValueError("Repair recovered fewer proposals than no repair, which the mechanism cannot do.")
    return {
        "Schema": SCHEMA,
        "Protocol": PROTOCOL,
        "Seeds": seeds,
        "ProposalsPerSeed": report["ProposalsPerSeed"],
        # Stated with the result, because it is the reason the lift is not a model-validity measurement.
        "BrokenFirstReplyRate": report["BreakRate"],
        "ValidProposalRatePoints": difference(pairs, lambda row: row["ValidRate"] * 100, seeds),
        "CallsPerValidCandidate": difference(pairs, lambda row: row["CallsPerValid"], seeds),
        "ModelCalls": difference(pairs, lambda row: float(row["ModelCalls"]), seeds),
        "NoRepairValidRate": sum(none["ValidRate"] for none, _ in pairs) / seeds,
        "RepairValidRate": sum(repair["ValidRate"] for _, repair in pairs) / seeds,
        "TokensMeasured": False,
    }


def lines(summary):
    out = ["seeds=%d proposals/seed=%d broken-first-reply rate=%.2f (an input, not a measurement)"
           % (summary["Seeds"], summary["ProposalsPerSeed"], summary["BrokenFirstReplyRate"])]
    for key, unit in (("ValidProposalRatePoints", "points"), ("CallsPerValidCandidate", "calls/valid"),
                      ("ModelCalls", "calls")):
        row = summary[key]
        out.append("%-26s %+.4f %-12s CI95 [%+.4f, %+.4f]"
                   % (key, row["Mean"], unit, row["LowerBound95"], row["UpperBound95"]))
    out.append("valid-proposal rate: %.4f without repair, %.4f with it"
               % (summary["NoRepairValidRate"], summary["RepairValidRate"]))
    return out


def render(summary):
    return json.dumps(summary, indent=1) + "\n"


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: repair_ablation.py <rows.json>")
    report = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    summary = analyse(report)
    print("\n".join(lines(summary)))
    return summary


if __name__ == "__main__":
    main()
