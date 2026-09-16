"""Retrospective paired diagnostics for the authored US-16 fixtures; no superiority gate."""

import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path
import statistics

from analyze import interval, require


def analyze(report):
    tasks = ("ShiftedQuadratic1", "SeparatedBasins1")
    methods = ("Uniform", "Adaptive", "UniformRestart", "AdaptiveRestart")
    timed = report.get("Protocol") == "fixed-adaptive-islands-pilot-v2"
    require((timed or report.get("Protocol") == "fixed-adaptive-islands-pilot-v1") and report.get("AllValid") is True, "Invalid campaign.")
    seeds, budget = report["Seeds"], report["EvaluatorCallCap"]
    require(type(seeds) is int and 1 <= seeds <= 30 and type(budget) is int and 32 <= budget <= 512, "Invalid budget or seeds.")
    runs = report["Runs"]
    require(len(runs) == len(tasks) * len(methods) * seeds, "Missing or additional runs.")
    indexed = {}
    for run in runs:
        key = (run["Task"], run["Method"], run["Seed"])
        require(key[0] in tasks and key[1] in methods and type(key[2]) is int and 0 <= key[2] < seeds and key not in indexed, "Unplanned or duplicate run.")
        require(run["Status"] == "completed" and math.isfinite(run["FinalQuality"]) and 0.5 <= run["FinalQuality"] <= 1 and run["EvaluatorCalls"] == budget
                and run["Spent"]["cost_units"] == budget and run["Unknown"] == 0 and run["MaximumViolated"] is False, "Failed run or accounting.")
        require(run["StateHash"] == run["ReplayStateHash"] and (run["ResumeStateHash"] is None or run["ResumeStateHash"] == run["StateHash"]), "Replay mismatch.")
        require((run["ResumeStateHash"] is not None) == (run["Seed"] == 0), "Missing or unexpected resume check.")
        if timed:
            require(type(run.get("ElapsedMilliseconds")) in (int, float) and math.isfinite(run["ElapsedMilliseconds"])
                    and run["ElapsedMilliseconds"] > 0, "Invalid timing.")
        indexed[key] = run
    summaries, contrasts = [], []
    for task in tasks:
        for seed in range(seeds):
            require(len({indexed[task, method, seed]["InitialPopulationHash"] for method in methods}) == 1, "Unpaired initialization.")
        for method in methods:
            values = [indexed[task, method, seed]["FinalQuality"] for seed in range(seeds)]
            summaries.append(dict(Task=task, Method=method, Mean=statistics.fmean(values), Median=statistics.median(values),
                                  Worst=min(values), Best=max(values), Reached095=sum(value >= 0.95 for value in values)))
            if timed:
                elapsed = [indexed[task, method, seed]["ElapsedMilliseconds"] for seed in range(seeds)]
                summaries[-1].update(MedianElapsedMilliseconds=statistics.median(elapsed), MeanElapsedMilliseconds=statistics.fmean(elapsed),
                                     EvaluatorCallsPerRun=budget, SuccessRate095=sum(value >= 0.95 for value in values) / seeds)
        for left, right in (("Adaptive", "Uniform"), ("AdaptiveRestart", "UniformRestart"),
                            ("UniformRestart", "Uniform"), ("AdaptiveRestart", "Adaptive")):
            differences = [indexed[task, left, seed]["FinalQuality"] - indexed[task, right, seed]["FinalQuality"] for seed in range(seeds)]
            contrasts.append(dict(Task=task, Contrast=left + " - " + right, MeanDifference=statistics.fmean(differences),
                                  PairedPercentile95=interval([differences], 10000, 42, 0.05, False)))
    return dict(SourceRevision=report["SourceRevision"], BootstrapSamples=10000, BootstrapSeed=42, Summaries=summaries, Contrasts=contrasts,
                Interpretation="Retrospective diagnostic, absolute quality differences, 30 paired seeds in the recorded campaign. Unadjusted intervals for authored tasks, not a preregistered gate, held-out generalization or competitor claim. No task-family resampling or best-method selection.")


def verify_raw(report, raw):
    """Reconcile exported summaries with measurements, not merely an intact gzip file."""
    for field in ("Protocol", "Seeds", "EvaluatorCallCap", "AllValid", "AssemblyVersion", "AssemblySha256"):
        require(report[field] == raw[field], "Raw campaign metadata mismatch.")
    require(report["SourceRevision"] in raw["AssemblyVersion"], "Raw revision mismatch.")
    require(len(report["Runs"]) == len(raw["Runs"]), "Raw run count mismatch.")
    timed = report["Protocol"].endswith("v2")
    if timed:
        for field in ("WarmupEvaluatorCalls", "Runtime", "OperatingSystem", "ProcessorCount", "TimingProtocol"):
            require(report[field] == raw[field], "Raw timing metadata mismatch.")
        require(raw["WarmupEvaluatorCalls"] == 256, "Invalid warmup accounting.")
    for summary, run in zip(report["Runs"], raw["Runs"]):
        for field in ("Task", "Method", "Seed", "Status", "InitialPopulationHash", "EvaluatorCalls", "Proposals",
                      "FinalQuality", "StateHash", "ReplayStateHash", "ResumeStateHash", "Statistics"):
            require(summary[field] == run[field], "Raw summary mismatch: " + field)
        resources = run["Resources"]
        for field in ("Spent", "Unknown", "MaximumViolated"):
            require(summary[field] == resources[field], "Raw accounting mismatch.")
        require(all(value == 0 for value in resources["Reserved"].values()), "Outstanding reservation.")
        receipts = resources["Receipts"]
        require(len(receipts) == run["EvaluatorCalls"] and resources["Admitted"] == resources["Settled"] == len(receipts), "Missing settled receipts.")
        require(len({receipt["OperationId"] for receipt in receipts}) == len(receipts), "Duplicate receipt.")
        require(all(receipt["Charged"]["Amounts"] == {"cost_units": 1} and not receipt["ExceededMaximum"] for receipt in receipts), "Invalid objective charge.")
        require(resources["Spent"] == {"cost_units": len(receipts)}, "Receipt total mismatch.")
        measurements = run["Measurements"]
        require(len(measurements) == run["EvaluatorCalls"], "Missing raw measurements.")
        best, previous_id, previous_time = -math.inf, -1, 0
        for measurement in measurements:
            x, quality = measurement["X"], measurement["Quality"]
            require(type(x) in (int, float) and math.isfinite(x) and 0 <= x <= 1, "Invalid genome.")
            expected = 1 - (x - 0.8) ** 2 if run["Task"] == "ShiftedQuadratic1" else max(0.5 - 10 * (x - 0.2) ** 2, 1 - 60 * (x - 0.8) ** 2)
            require(math.isfinite(quality) and math.isclose(quality, expected, abs_tol=1e-12, rel_tol=1e-12), "Incorrect objective value.")
            require(type(measurement["EvaluationId"]) is int and measurement["EvaluationId"] > previous_id, "Duplicate/unordered evaluation.")
            previous_id = measurement["EvaluationId"]
            best = max(best, quality)
            require(measurement["BestQuality"] == best, "Incorrect best-quality curve.")
            if timed:
                stamp = measurement["ElapsedMilliseconds"]
                require(math.isfinite(stamp) and previous_time <= stamp <= run["ElapsedMilliseconds"], "Invalid measurement timing.")
                previous_time = stamp
        require(run["FinalQuality"] == best, "Final quality disagrees with raw measurements.")
        if timed:
            require(summary["ElapsedMilliseconds"] == run["ElapsedMilliseconds"], "Raw elapsed time mismatch.")


def load_verified(path):
    report = json.loads(path.read_text(encoding="utf-8-sig"))
    require(report["RawFile"] == "raw.json.gz", "Unexpected raw evidence path.")
    compressed = (path.parent / report["RawFile"]).read_bytes()
    require(hashlib.sha256(compressed).hexdigest() == report["GzipSha256"], "Raw evidence hash mismatch.")
    content = gzip.decompress(compressed)
    require(hashlib.sha256(content).hexdigest() == report["RawSha256"], "Raw evidence hash mismatch.")
    verify_raw(report, json.loads(content))
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("summary", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = json.dumps(analyze(load_verified(args.summary)), indent=2)
    if args.output:
        with args.output.open("x", encoding="utf-8") as stream:
            stream.write(result + "\n")
    else:
        print(result)


if __name__ == "__main__":
    main()
