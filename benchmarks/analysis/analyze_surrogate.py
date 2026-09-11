"""Paired retrospective diagnostics for the bounded numeric surrogate pilot; no dependencies."""

import argparse
import json
from pathlib import Path
import re
import statistics

from analyze import finite, integer, interval, load_json, require


METHODS = ("Ordinary", "UniformPool", "SurrogatePool", "ValidatedPool")
TASKS = ("ShiftedQuadratic4", "RippledQuadratic4")


def analyze(report):
    require(isinstance(report, dict) and report.get("SchemaVersion") == 2
            and report.get("Kind") == "compact-surrogate-example-evidence"
            and report.get("Protocol") == "synthetic-surrogate-example-v3-cost-ratios", "Unsupported evidence schema.")
    require(isinstance(report.get("SourceRevision"), str) and re.fullmatch(r"[0-9a-f]{40}", report["SourceRevision"]), "Pin source revision.")
    require(integer(report.get("Seeds"), 1, 20) and finite(report.get("BaseCostCap"))
            and 16 <= report["BaseCostCap"] <= 256, "Invalid campaign bounds.")
    prices = report.get("EvaluationUnitCosts")
    require(prices in ([1], [0.1, 1, 10]), "Invalid evaluator tariff schedule.")
    rows = report.get("Runs")
    require(isinstance(rows, list) and len(rows) == len(TASKS) * len(METHODS) * report["Seeds"] * len(prices), "Missing scheduled runs.")
    indexed = {}
    failed = []
    for row in rows:
        require(isinstance(row, dict) and row.get("Task") in TASKS and row.get("Method") in METHODS
                and integer(row.get("Seed"), 0, report["Seeds"] - 1) and row.get("EvaluationUnitCost") in prices, "Unplanned run.")
        key = row["Task"], row["EvaluationUnitCost"], row["Method"], row["Seed"]
        require(key not in indexed, "Duplicate scheduled run.")
        require(row.get("CostCap") == report["BaseCostCap"] * row["EvaluationUnitCost"], "Unmatched cost cap.")
        require(isinstance(row.get("InitialPopulationHash"), str) and re.fullmatch(r"[0-9a-f]{64}", row["InitialPopulationHash"]), "Missing initialization identity.")
        resources = row.get("Resources") if isinstance(row.get("Resources"), dict) else {}
        spent = resources.get("Spent", {}).get("cost_units") if isinstance(resources.get("Spent"), dict) else None
        stages = row.get("StageCostUnits", {})
        completed = (row.get("Status") == "completed" and resources.get("Unknown") == 0
                     and resources.get("Reserved") == {"cost_units": 0} and resources.get("DroppedReceipts") == 0
                     and integer(resources.get("Admitted"), 1, 4096) and resources.get("Settled") == resources["Admitted"]
                     and resources.get("MaximumViolated") is False and finite(spent) and spent <= row["CostCap"]
                     and finite(row.get("FinalLoss")) and row["FinalLoss"] <= 8
                     and integer(row.get("EvaluatorCalls"), 8, 512) and row.get("MeasurementCount") == row["EvaluatorCalls"]
                     and isinstance(stages, dict) and all(finite(value) for value in stages.values())
                     and abs(sum(stages.values()) - spent) <= 1e-8
                     and abs(stages.get("Evaluation", -1) - row["EvaluatorCalls"] * row["EvaluationUnitCost"]) <= 1e-8)
        indexed[key] = (row, row["FinalLoss"] if completed else 8.0)
        if not completed:
            failed.append({"Task": key[0], "EvaluationUnitCost": key[1], "Method": key[2], "Seed": key[3],
                           "Status": row.get("Status"), "PenaltyLoss": 8.0})
    summaries, comparisons = [], []
    for task in TASKS:
        for price in prices:
            for seed in range(report["Seeds"]):
                require(len({indexed[(task, price, method, seed)][0]["InitialPopulationHash"] for method in METHODS}) == 1,
                        "Paired initial populations differ.")
            for method in METHODS:
                run_rows = [indexed[(task, price, method, seed)] for seed in range(report["Seeds"])]
                losses = [loss for _, loss in run_rows]
                calls = [row["EvaluatorCalls"] for row, _ in run_rows if integer(row.get("EvaluatorCalls"), 0, 512)]
                costs = [row.get("Resources", {}).get("Spent", {}).get("cost_units") for row, _ in run_rows
                         if isinstance(row.get("Resources"), dict) and isinstance(row["Resources"].get("Spent"), dict)]
                costs = [cost for cost in costs if finite(cost)]
                summaries.append({"Task": task, "EvaluationUnitCost": price, "Method": method, "Runs": len(run_rows),
                                  "MeanPenalizedLoss": statistics.fmean(losses), "MedianPenalizedLoss": statistics.median(losses),
                                  "WorstPenalizedLoss": max(losses),
                                  "ReportedEvaluatorCallRows": len(calls), "MeanReportedEvaluatorCalls": statistics.fmean(calls) if calls else None,
                                  "ReportedSpentCostRows": len(costs), "MeanReportedSpentCostUnits": statistics.fmean(costs) if costs else None})
            for comparator in METHODS[:-1]:
                differences = [indexed[(task, price, "ValidatedPool", seed)][1] - indexed[(task, price, comparator, seed)][1]
                               for seed in range(report["Seeds"])]
                comparisons.append({"Task": task, "EvaluationUnitCost": price, "Primary": "ValidatedPool", "Comparator": comparator,
                                    "PairedSeeds": report["Seeds"], "MeanPairedLossDifference": statistics.fmean(differences),
                                    "PairedPercentile95Interval": interval([differences], 10000, 42, 0.05, False),
                                    "PrimaryWins": sum(value < 0 for value in differences), "Ties": differences.count(0),
                                    "PrimaryLosses": sum(value > 0 for value in differences)})
    return {"SchemaVersion": 1, "Purpose": "retrospective-surrogate-development", "SourceRevision": report["SourceRevision"],
            "FullTraceSha256": report.get("FullTraceSha256"), "ScheduledRuns": len(rows), "FailedOrInvalidRuns": failed,
            "Interpretation": "Negative paired loss difference favors ValidatedPool. Equal total caps only within each tariff. "
                              "10000 paired seed bootstrap draws, seed 42, unadjusted 95% percentile intervals; no task resampling. "
                              "Failed/incomplete/invalid runs retain worst-support loss 8; none are dropped. "
                              "Two authored deterministic fixtures, synthetic work tariffs, no matched tuning or competitor superiority claim.",
            "Summaries": summaries, "Comparisons": comparisons}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("summary", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report, digest = load_json(args.summary, 4 * 1024 * 1024)
    result = analyze(report)
    result["InputSummarySha256"] = digest
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, indent=2, allow_nan=False)
        output.write("\n")
    print(f"Analyzed {result['ScheduledRuns']} scheduled runs, retaining {len(result['FailedOrInvalidRuns'])} failed/invalid rows.")


if __name__ == "__main__":
    main()
