"""US-11 (#29): the paired reading of `benchmarks/EvolutionAutoMl`'s rows.

The harness tunes a real `RidgeRegression`'s hyperparameters with the engine and with exhaustive grid
search at **equal model fits**, selecting on validation only, and scores each once on a test split
sealed before either searcher started. This pairs them by seed and puts a bootstrap interval on the
test-MSE ratio.

What the numbers can support is narrow and worth saying out loud: a two-parameter space is small
enough that a grid *is* near-optimal, so parity here is the expected result and the honest claim.
A space too large to enumerate is where a searcher earns its keep, and this workload is not that.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from analyze import interval  # noqa: E402

SCHEMA = "evolution-automl-tuning-v1"
PROTOCOL = "automl-tuning-v1"
BOOTSTRAP_SAMPLES = 2000
BOOTSTRAP_SEED = 29
ALPHA = 0.05


def difference(values, unit):
    mean = sum(values) / len(values)
    bounds = interval([[value] for value in values], BOOTSTRAP_SAMPLES, BOOTSTRAP_SEED, ALPHA, resample_tasks=True)
    return {"Mean": mean, "LowerBound95": bounds[0], "UpperBound95": bounds[1], "Seeds": len(values), "Unit": unit}


def analyse(report):
    if report.get("Protocol") != PROTOCOL:
        raise ValueError("Unexpected protocol: " + str(report.get("Protocol")))
    rows = report["Rows"]
    if not rows:
        raise ValueError("No rows to pair.")
    for row in rows:
        if row["EngineFits"] != row["GridFits"]:
            raise ValueError("The two searchers were not given the same number of model fits.")
        if len(row["TestSeal"]) != 64:
            raise ValueError("Every row must carry the seal of the test split it was scored on.")
        if abs(row["TestMseRatio"] - row["EngineTestMse"] / row["GridTestMse"]) > 1e-12:
            raise ValueError("The reported ratio is not the one the two errors produce.")
    seals = {row["TestSeal"] for row in rows}
    if len(seals) != len(rows):
        raise ValueError("Two seeds share a sealed test split, so their splits are not independent.")
    ratios = [row["TestMseRatio"] for row in rows]
    return {
        "Schema": SCHEMA,
        "Protocol": PROTOCOL,
        "Seeds": len(rows),
        "Budget": report["Budget"],
        "Features": report["Features"],
        "Rows": {"Train": report["TrainRows"], "Validation": report["ValidationRows"], "Test": report["TestRows"]},
        # Below 1.0 means the engine's choice generalised better than the grid's.
        "TestMseRatio": difference(ratios, "ratio"),
        "SeedsEngineBetter": sum(1 for ratio in ratios if ratio < 1),
        "SeedsGridBetter": sum(1 for ratio in ratios if ratio > 1),
        "WorstSeedRatio": max(ratios),
        "BestSeedRatio": min(ratios),
        "EqualFitsPerSearcher": rows[0]["EngineFits"],
    }


def lines(summary):
    ratio = summary["TestMseRatio"]
    return [
        "seeds=%d equal fits per searcher=%d space=alpha x intercept" % (summary["Seeds"], summary["EqualFitsPerSearcher"]),
        "test MSE ratio engine/grid %.4f CI95 [%.4f, %.4f]" % (ratio["Mean"], ratio["LowerBound95"], ratio["UpperBound95"]),
        "engine better on %d seeds, grid better on %d; range %.4f to %.4f"
        % (summary["SeedsEngineBetter"], summary["SeedsGridBetter"], summary["BestSeedRatio"], summary["WorstSeedRatio"]),
    ]


def render(summary):
    return json.dumps(summary, indent=1) + "\n"


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: automl_tuning.py <rows.json>")
    summary = analyse(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8")))
    print("\n".join(lines(summary)))
    return summary


if __name__ == "__main__":
    main()
