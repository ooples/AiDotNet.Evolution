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
    require(report.get("Protocol") == "fixed-adaptive-islands-pilot-v1" and report.get("AllValid") is True, "Invalid campaign.")
    seeds, budget = report["Seeds"], report["EvaluatorCallCap"]
    require(type(seeds) is int and 1 <= seeds <= 30 and type(budget) is int and 32 <= budget <= 512, "Invalid budget or seeds.")
    runs = report["Runs"]
    require(len(runs) == len(tasks) * len(methods) * seeds, "Missing or additional runs.")
    indexed = {}
    for run in runs:
        key = (run["Task"], run["Method"], run["Seed"])
        require(key[0] in tasks and key[1] in methods and type(key[2]) is int and 0 <= key[2] < seeds and key not in indexed, "Unplanned or duplicate run.")
        require(run["Status"] == "completed" and math.isfinite(run["FinalQuality"]) and run["EvaluatorCalls"] == budget
                and run["Spent"]["cost_units"] == budget and run["Unknown"] == 0 and run["MaximumViolated"] is False, "Failed run or accounting.")
        require(run["StateHash"] == run["ReplayStateHash"] and (run["ResumeStateHash"] is None or run["ResumeStateHash"] == run["StateHash"]), "Replay mismatch.")
        require((run["ResumeStateHash"] is not None) == (run["Seed"] == 0), "Missing or unexpected resume check.")
        indexed[key] = run
    summaries, contrasts = [], []
    for task in tasks:
        for seed in range(seeds):
            require(len({indexed[task, method, seed]["InitialPopulationHash"] for method in methods}) == 1, "Unpaired initialization.")
        for method in methods:
            values = [indexed[task, method, seed]["FinalQuality"] for seed in range(seeds)]
            summaries.append(dict(Task=task, Method=method, Mean=statistics.fmean(values), Median=statistics.median(values),
                                  Worst=min(values), Best=max(values), Reached095=sum(value >= 0.95 for value in values)))
        for left, right in (("Adaptive", "Uniform"), ("AdaptiveRestart", "UniformRestart"),
                            ("UniformRestart", "Uniform"), ("AdaptiveRestart", "Adaptive")):
            differences = [indexed[task, left, seed]["FinalQuality"] - indexed[task, right, seed]["FinalQuality"] for seed in range(seeds)]
            contrasts.append(dict(Task=task, Contrast=left + " - " + right, MeanDifference=statistics.fmean(differences),
                                  PairedPercentile95=interval([differences], 10000, 42, 0.05, False)))
    return dict(SourceRevision=report["SourceRevision"], BootstrapSamples=10000, BootstrapSeed=42, Summaries=summaries, Contrasts=contrasts,
                Interpretation="Retrospective diagnostic, absolute quality differences, 30 paired seeds in the recorded campaign. Unadjusted intervals for authored tasks, not a preregistered gate, held-out generalization or competitor claim. No task-family resampling or best-method selection.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("summary", type=Path)
    args = parser.parse_args()
    report = json.loads(args.summary.read_text(encoding="utf-8"))
    require(report["RawFile"] == "raw.json.gz", "Unexpected raw evidence path.")
    raw = args.summary.parent / report["RawFile"]
    for opener, expected in ((open, report["GzipSha256"]), (gzip.open, report["RawSha256"])):
        digest = hashlib.sha256()
        with opener(raw, "rb") as stream:
            for chunk in iter(lambda: stream.read(65536), b""):
                digest.update(chunk)
        require(digest.hexdigest() == expected, "Raw evidence hash mismatch.")
    print(json.dumps(analyze(report), indent=2))


if __name__ == "__main__":
    main()
