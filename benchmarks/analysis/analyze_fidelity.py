"""Failure-inclusive paired diagnostics for the bounded fidelity fixtures; no external dependencies."""

import argparse
import gzip
import hashlib
import json
from pathlib import Path
import re
import statistics

from analyze import finite, integer, interval, load_json, require


def analyze(campaign):
    require(isinstance(campaign, dict), "Campaign must be an object.")
    trained = campaign.get("Protocol") == "trained-regression-fidelity-v1"
    require(trained or campaign.get("Protocol") == "synthetic-fidelity-example-v1", "Unsupported fidelity protocol.")
    tasks = ["AuthoredLinearRegression4"] if trained else ["Aligned", "LateImprover"]
    methods = ["FullCohort", "GreedyHalving", "ExploratoryHalving", "RestartOnlyHalving"] if trained else ["GreedyHalving", "ExploratoryHalving"]
    seeds = campaign.get("Seeds")
    require(integer(seeds, 1, 32) and campaign.get("CostCap") == (2100 if trained else 100), "Invalid schedule or cap.")
    version = campaign.get("AssemblyVersion")
    require(isinstance(version, str) and re.search(r"\+[0-9a-f]{40}$", version), "Pin the complete source revision.")
    rows = campaign.get("Runs")
    require(isinstance(rows, list) and len(rows) == len(tasks) * len(methods) * seeds, "Missing scheduled runs.")
    indexed, normalized = {}, []
    for row in rows:
        require(isinstance(row, dict) and row.get("Task") in tasks and row.get("Method") in methods and integer(row.get("Seed"), 0, seeds - 1), "Unplanned run.")
        key = row["Task"], row["Method"], row["Seed"]
        require(key not in indexed, "Duplicate scheduled run.")
        require(isinstance(row.get("InitialPopulationHash"), str) and re.fullmatch(r"[0-9a-f]{64}", row["InitialPopulationHash"]), "Missing cohort hash.")
        report = row.get("Report") if isinstance(row.get("Report"), dict) else {}
        resources = report.get("Resources") if isinstance(report.get("Resources"), dict) else {}
        spent = resources.get("Spent", {}).get("cost_units") if isinstance(resources.get("Spent"), dict) else None
        receipts = resources.get("Receipts", [])
        batches = report.get("Batches", [])
        structural = (isinstance(receipts, list) and isinstance(batches, list) and all(isinstance(batch, dict) and isinstance(batch.get("Measurements"), dict)
                      and isinstance(batch["Measurements"].get("Samples"), list) for batch in batches))
        samples = [sample for batch in batches for sample in batch["Measurements"]["Samples"]] if structural else []
        confirmed = [batch for batch in batches if batch.get("Purpose") == "Confirmation"] if structural else []
        quality = row.get("BestConfirmedQuality")
        expected_calls = 64 if trained and row["Method"] == "FullCohort" else 32
        expected_confirmed = 8 if trained and row["Method"] == "FullCohort" else 2
        initial_ids = report.get("InitialCandidateIds")
        completed = (row.get("Status") == "completed" and report.get("IsComplete") is True and report.get("StopReason") == "Completed"
                     and finite(quality) and quality <= 1 and finite(spent) and spent <= campaign["CostCap"]
                     and resources.get("Unknown") == 0 and resources.get("MaximumViolated") is False and resources.get("DroppedReceipts") == 0
                     and resources.get("Reserved") == {"cost_units": 0} and integer(resources.get("Admitted"), 1, 4096)
                     and resources.get("Settled") == resources["Admitted"] and structural and integer(row.get("EvaluatorCalls"), 1, 1024)
                     and row["EvaluatorCalls"] == expected_calls and isinstance(initial_ids, list) and len(initial_ids) == 8
                     and all(isinstance(identity, str) for identity in initial_ids) and len(set(initial_ids)) == 8
                     and len(samples) == row["EvaluatorCalls"] and len(receipts) == resources["Settled"] and len(confirmed) == expected_confirmed)
        if completed:
            try:
                completed = (len({sample["Context"]["SampleIdentity"] for sample in samples}) == len(samples)
                             and all(isinstance(sample["Context"]["SampleIdentity"], str) and sample["Status"] == "Completed" and sample["UnknownCost"] is False
                                     and finite(sample["Quality"]) and sample["Quality"] <= 1 and finite(sample["ChargedCostUnits"]) for sample in samples)
                             and all(batch["Candidate"]["Id"] in initial_ids and len(batch["Measurements"]["Samples"]) == 2
                                     and abs(statistics.fmean(sample["Quality"] for sample in batch["Measurements"]["Samples"]) - batch["Measurements"]["MeanQuality"]) < 1e-12 for batch in batches)
                             and abs(sum(sample["ChargedCostUnits"] for sample in samples) - report["ChargedCostUnits"]) < 1e-8
                             and abs(sum(receipt["Charged"]["Amounts"]["cost_units"] for receipt in receipts) - spent) < 1e-8
                             and abs(spent - report["ChargedCostUnits"] - 0.08) < 1e-8
                             and all(batch["Level"]["ResourceLevel"] == (64 if trained else 9) and all(prior is None for prior in batch["ResumedFromSampleIdentities"]) for batch in confirmed)
                             and max(batch["Measurements"]["MeanQuality"] for batch in confirmed) == quality
                             and any(batch["Candidate"]["Id"] == row.get("BestConfirmedGenome") and batch["Measurements"]["MeanQuality"] == quality for batch in confirmed))
                if trained:
                    observations = row["Measurements"]
                    by_sample = {sample["Context"]["SampleIdentity"]: sample for sample in samples}
                    completed = (completed and len(observations) == len(samples)
                                 and {value["SampleIdentity"] for value in observations} == {sample["Context"]["SampleIdentity"] for sample in samples}
                                 and sum(value["ActualEpochs"] for value in observations) == row["ExecutedEpochs"]
                                 and row["TrainingRowVisits"] == row["ExecutedEpochs"] * 128 and row["ValidationRowVisits"] == row["EvaluatorCalls"] * 64
                                 and abs(report["ChargedCostUnits"] - row["ExecutedEpochs"] - 0.25 * row["EvaluatorCalls"]) < 1e-8
                                 and all(integer(value["ActualEpochs"], 1, 64) and finite(value["MeanSquaredError"])
                                         and abs(value["Quality"] - 1 / (1 + value["MeanSquaredError"])) < 1e-12
                                         and value["Quality"] == by_sample[value["SampleIdentity"]]["Quality"]
                                         and by_sample[value["SampleIdentity"]]["ChargedCostUnits"] == value["ActualEpochs"] + 0.25
                                         and (value["Purpose"] != "Confirmation" or (value["ResumedFrom"] is None and value["ActualEpochs"] == 64)) for value in observations)
                                 and not ({value["DataIdentity"] for value in observations if value["Purpose"] == "Search"}
                                          & {value["DataIdentity"] for value in observations if value["Purpose"] == "Confirmation"}))
            except (KeyError, TypeError, ValueError, OverflowError):
                completed = False
        value = {"Task": key[0], "Method": key[1], "Seed": key[2], "Status": row.get("Status"), "AcceptedEvidence": bool(completed),
                 "PenalizedQuality": quality if completed else 0.0, "ReportedQuality": quality if finite(quality) else None,
                 "ReportedCostUnits": spent if finite(spent) else None, "InitialPopulationHash": row["InitialPopulationHash"]}
        for output_name, input_name in (("EvaluatorCalls", "EvaluatorCalls"), ("ResumedCalls", "ResumedCalls"), ("ConfirmationCalls", "ConfirmationCalls"),
                                        ("ExecutedWork", "ExecutedEpochs" if trained else "ExecutedSteps")):
            value[output_name] = row.get(input_name) if integer(row.get(input_name), 0, 10_000_000) else None
        normalized.append(value); indexed[key] = value
    summaries, comparisons = [], []
    for task in tasks:
        for seed in range(seeds):
            require(len({indexed[(task, method, seed)]["InitialPopulationHash"] for method in methods}) == 1, "Paired initial cohorts differ.")
        for method in methods:
            selected = [indexed[(task, method, seed)] for seed in range(seeds)]
            values = [row["PenalizedQuality"] for row in selected]
            costs = [row["ReportedCostUnits"] for row in selected if row["ReportedCostUnits"] is not None]
            summaries.append({"Task": task, "Method": method, "Runs": seeds, "FailedOrInvalid": sum(not row["AcceptedEvidence"] for row in selected),
                              "MeanPenalizedQuality": statistics.fmean(values), "MedianPenalizedQuality": statistics.median(values), "WorstPenalizedQuality": min(values),
                              "ReportedCostRows": len(costs), "MeanReportedCostUnits": statistics.fmean(costs) if costs else None})
        contrasts = [("ExploratoryHalving", "GreedyHalving")]
        if trained:
            contrasts += [("GreedyHalving", "FullCohort"), ("ExploratoryHalving", "FullCohort"), ("GreedyHalving", "RestartOnlyHalving")]
        for primary, control in contrasts:
            differences = [indexed[(task, primary, seed)]["PenalizedQuality"] - indexed[(task, control, seed)]["PenalizedQuality"] for seed in range(seeds)]
            comparisons.append({"Task": task, "Primary": primary, "Comparator": control, "PairedSeeds": seeds,
                                "MeanPairedQualityDifference": statistics.fmean(differences), "PairedPercentile95Interval": interval([differences], 10000, 42, 0.05, False),
                                "Wins": sum(value > 0 for value in differences), "Ties": differences.count(0), "Losses": sum(value < 0 for value in differences)})
    return {"SchemaVersion": 1, "Protocol": campaign["Protocol"], "SourceRevision": version[-40:], "ScheduledRuns": len(rows),
            "FailedOrInvalidRuns": sum(not row["AcceptedEvidence"] for row in normalized), "Runs": normalized, "Summaries": summaries, "Comparisons": comparisons,
            "Interpretation": "Positive paired quality differences favor primary. Failed/incomplete/invalid rows retain worst-support quality0; none dropped. "
                              "10000 paired seed bootstrap draws, seed42, unadjusted95% percentile intervals, no task resampling. "
                              "Retrospective authored fixtures and declared work tariffs; same caps/cohorts but finite brackets spend different amounts. "
                              "FullCohort also confirms more candidates. No representative superiority, wall-time speedup or deployment claim."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path); parser.add_argument("output_directory", type=Path)
    args = parser.parse_args()
    campaign, digest = load_json(args.input, 64 * 1024 * 1024)
    result = analyze(campaign)
    raw = args.input.read_bytes()
    require(hashlib.sha256(raw).hexdigest() == digest, "Raw input changed during analysis.")
    args.output_directory.mkdir(parents=True, exist_ok=False)
    compressed = args.output_directory / "raw.json.gz"
    with compressed.open("xb") as target:
        with gzip.GzipFile(filename="", mode="wb", fileobj=target, mtime=0) as archive:
            archive.write(raw)
    result["FullTraceSha256"] = digest
    result["RawGzipSha256"] = hashlib.sha256(compressed.read_bytes()).hexdigest()
    result["RawBytes"] = len(raw)
    result["AnalysisProgramSha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    # Canonical LF avoids Git EOL conversion invalidating byte-addressed evidence on Windows.
    with (args.output_directory / "analysis.json").open("x", encoding="utf-8", newline="\n") as output:
        json.dump(result, output, indent=2, allow_nan=False); output.write("\n")
    print(f"Retained {result['ScheduledRuns']} fidelity runs, including {result['FailedOrInvalidRuns']} failed/invalid rows, and complete compressed raw evidence.")


if __name__ == "__main__":
    main()
