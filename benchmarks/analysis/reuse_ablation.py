"""US-18 (#36): the paired reading of the feature-ablation campaign's `baseline` and `novelty` arms.

The campaign in `benchmarks/AiDotNet.Evolution.Quality` already runs the whole fixed matrix on the
development partition; this takes the two arms that differ only in novelty detection and experience
reuse, pairs them by (family, seed), and puts a bootstrap interval on each difference using
`analyze.interval` so the pilot and this share one estimator.

Three things the instrument cannot do, stated here rather than in a footnote:

- **Tokens are not measured.** The ablation workloads are numeric, program-shaped and kernel-shaped
  objectives with no model in the loop, so there is no token to count. Evaluator calls are the unit.
- **The suppressed-proposal rate is not the generated-duplicate rate.** It counts proposals that
  never reached the evaluator because they repeated, or were too close to, something already seen.
  Whether an operator *generates* fewer duplicates is a property of the operator, which this
  mechanism does not change.
- **Reduced rows.** The campaign's own `development.json` is ~36 MB of per-evaluation observations;
  the committed bundle carries the fields the analysis reads plus the SHA-256 of the file they came
  from, so the reduction is checkable rather than trusted.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze import interval  # noqa: E402

SCHEMA = "evolution-reuse-ablation-v1"
BASELINE, NOVELTY = "baseline", "novelty"
BOOTSTRAP_SAMPLES = 2000
BOOTSTRAP_SEED = 18
ALPHA = 0.05


def paired(rows):
    arms = {}
    for row in rows:
        if row["Configuration"] in (BASELINE, NOVELTY):
            arms.setdefault((row["Family"], row["Seed"]), {})[row["Configuration"]] = row
    pairs = [value for _, value in sorted(arms.items()) if len(value) == 2]
    if not pairs:
        raise ValueError("No (family, seed) carries both the baseline and the novelty arm.")
    if len(pairs) != len(arms):
        raise ValueError("Some (family, seed) is missing an arm, so the pairing is not paired.")
    return pairs


def suppressed(row):
    """The share of proposals that never cost an evaluation: repeats and too-close proposals."""
    if row["Proposals"] <= 0:
        raise ValueError("A row with no proposals cannot carry a suppression rate.")
    return (row["Proposals"] - row["Calls"]) / row["Proposals"]


def difference(deltas, unit):
    mean = sum(deltas) / len(deltas)
    bounds = interval([[delta] for delta in deltas], BOOTSTRAP_SAMPLES, BOOTSTRAP_SEED, ALPHA, resample_tasks=True)
    return {"Mean": mean, "LowerBound95": bounds[0], "UpperBound95": bounds[1], "Pairs": len(deltas), "Unit": unit}


def analyse(bundle):
    pairs = paired(bundle["Rows"])
    for pair in pairs:
        for row in pair.values():
            if row["Status"] != "completed":
                raise ValueError("Every paired row must be a completed run.")
    reached = [pair for pair in pairs
               if pair[BASELINE]["CallsToTarget"] is not None and pair[NOVELTY]["CallsToTarget"] is not None]
    return {
        "Schema": SCHEMA,
        "SourceSha256": bundle["SourceSha256"],
        "RequestHash": bundle["RequestHash"],
        "Pairs": len(pairs),
        "Budget": bundle["Budget"],
        "SuppressedProposalRatePoints": difference(
            [(suppressed(pair[NOVELTY]) - suppressed(pair[BASELINE])) * 100 for pair in pairs], "points"),
        "QualityDifference": difference(
            [pair[NOVELTY]["Quality"] - pair[BASELINE]["Quality"] for pair in pairs], "quality"),
        # Reported on the subset where both arms reached the target, with the count, because a pair
        # that never reached it has no calls-to-target to difference.
        "CallsToTargetDifference": difference(
            [float(pair[NOVELTY]["CallsToTarget"] - pair[BASELINE]["CallsToTarget"]) for pair in reached], "calls")
            if reached else None,
        "PairsReachingTarget": len(reached),
        "BaselineSuppressedRate": sum(suppressed(pair[BASELINE]) for pair in pairs) / len(pairs),
        "NoveltySuppressedRate": sum(suppressed(pair[NOVELTY]) for pair in pairs) / len(pairs),
        "TokensMeasured": False,
        "GeneratedDuplicateRateMeasured": False,
    }


def lines(summary):
    out = ["pairs=%d budget=%d (families x seeds, baseline vs novelty)" % (summary["Pairs"], summary["Budget"])]
    for key in ("SuppressedProposalRatePoints", "QualityDifference", "CallsToTargetDifference"):
        row = summary[key]
        if row is None:
            out.append("%-28s no pair reached the target under both arms" % key)
            continue
        out.append("%-28s %+.4f %-8s CI95 [%+.4f, %+.4f] over %d pairs"
                   % (key, row["Mean"], row["Unit"], row["LowerBound95"], row["UpperBound95"], row["Pairs"]))
    out.append("suppressed-proposal rate: %.4f baseline, %.4f with novelty and reuse"
               % (summary["BaselineSuppressedRate"], summary["NoveltySuppressedRate"]))
    return out


def render(summary):
    return json.dumps(summary, indent=1) + "\n"


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: reuse_ablation.py <rows-compact.json>")
    summary = analyse(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8")))
    print("\n".join(lines(summary)))
    return summary


if __name__ == "__main__":
    main()
