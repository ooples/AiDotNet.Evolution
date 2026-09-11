"""Failure-aware paired reporting for the numeric development protocol; no third-party packages."""

import argparse
import hashlib
import json
import math
from pathlib import Path
import random
import re
import statistics
import sys


def require(condition, message):
    if not condition:
        raise ValueError(message)


def integer(value, minimum, maximum):
    return type(value) is int and minimum <= value <= maximum


def finite(value, minimum=0):
    try:
        return type(value) in (int, float) and math.isfinite(value) and value >= minimum
    except OverflowError:
        return False


def names(values, maximum):
    return (isinstance(values, list) and 1 <= len(values) <= maximum
            and all(isinstance(v, str) and re.fullmatch(r"[A-Za-z0-9_.-]{1,80}", v) for v in values)
            and len(set(values)) == len(values))


def validate_plan(plan):
    fields = {"SchemaVersion", "Purpose", "SourceRevision", "Protocol", "Tasks", "Methods", "SeedCount",
              "Budget", "PrimaryMethod", "Comparators", "BootstrapSamples", "BootstrapSeed", "ResampleTasks", "ConfidenceLevel"}
    require(isinstance(plan, dict) and set(plan) == fields, "Missing or unknown plan field.")
    require(integer(plan["SchemaVersion"], 1, 1) and plan["Purpose"] == "retrospective-development", "Only retrospective development analysis is supported.")
    require(isinstance(plan["SourceRevision"], str) and re.fullmatch(r"[0-9a-f]{40}", plan["SourceRevision"]), "Pin the complete source revision.")
    require(plan["Protocol"] in ("numeric-development-v3-diagonal-cma", "numeric-development-v4-external"), "Unsupported experiment protocol.")
    require(isinstance(plan["Tasks"], list) and all(isinstance(t, dict) and set(t) == {"Name", "Scale"} for t in plan["Tasks"]), "Invalid tasks.")
    require(names([t["Name"] for t in plan["Tasks"]], 256) and all(finite(t["Scale"]) and 1e-12 <= t["Scale"] <= 1e12 for t in plan["Tasks"]), "Invalid task names or scales.")
    require(names(plan["Methods"], 16) and names(plan["Comparators"], 15), "Invalid methods/comparators.")
    require(plan["PrimaryMethod"] in plan["Methods"] and set(plan["Comparators"]).issubset(plan["Methods"])
            and plan["PrimaryMethod"] not in plan["Comparators"], "Invalid primary comparison.")
    require(integer(plan["SeedCount"], 1, 1000) and integer(plan["Budget"], 8, 1_000_000), "Invalid fixed run count or budget.")
    require(len(plan["Tasks"]) * len(plan["Methods"]) * plan["SeedCount"] <= 100_000, "Scheduled-run bound exceeded.")
    require(integer(plan["BootstrapSamples"], 200, 10000) and integer(plan["BootstrapSeed"], 0, 2**32 - 1), "Invalid bootstrap configuration.")
    require(type(plan["ResampleTasks"]) is bool and finite(plan["ConfidenceLevel"]) and 0.8 <= plan["ConfidenceLevel"] < 1, "Invalid interval configuration.")
    draws = len(plan["Comparators"]) * len(plan["Tasks"]) * plan["SeedCount"] * plan["BootstrapSamples"] * 2
    require(draws <= 20_000_000, "Bootstrap work exceeds the 20-million paired-draw bound.")


def percentile(sorted_values, probability):
    index = probability * (len(sorted_values) - 1)
    lower = int(index)
    upper = min(lower + 1, len(sorted_values) - 1)
    return sorted_values[lower] + (sorted_values[upper] - sorted_values[lower]) * (index - lower)


def interval(groups, samples, seed, alpha, resample_tasks):
    """Each value is an already-paired run difference; never resample methods independently."""
    rng = random.Random(seed)
    estimates = []
    for _ in range(samples):
        task_ids = [rng.randrange(len(groups)) for _ in groups] if resample_tasks else range(len(groups))
        estimates.append(statistics.fmean(statistics.fmean(group[rng.randrange(len(group))] for _ in group)
                                          for group in (groups[i] for i in task_ids)))
    estimates.sort()
    return [percentile(estimates, alpha / 2), percentile(estimates, 1 - alpha / 2)]


def trajectory(run, budget):
    samples = run.get("Samples")
    complete = isinstance(samples, list) and len(samples) == run.get("Proposals")
    if not complete:
        return False, []
    points, cost, attempts, previous, previous_id = [], 0, 0, math.inf, -1
    for sample in samples:
        if not isinstance(sample, dict):
            return False, []
        identity, loss = sample.get("EvaluationId"), sample.get("BestLoss")
        if (not integer(identity, 0, 2**63 - 1) or identity <= previous_id or not finite(sample.get("CostUnits"))
                or not integer(sample.get("Attempts"), 0, 1_000_000) or not finite(loss) or loss > previous):
            return False, []
        previous_id = identity
        cost += sample["CostUnits"]
        attempts += sample["Attempts"]
        previous = loss
        points.append({"CostUnits": cost, "BestLoss": loss})
    complete = (attempts == run.get("EvaluatorCalls") and cost == run.get("Resources", {}).get("Spent", {}).get("cost_units")
                and cost <= budget and (not points or points[-1]["BestLoss"] == run.get("FinalLoss")))
    return complete, points if complete else []


def analyze(campaign, plan):
    validate_plan(plan)
    require(isinstance(campaign, dict) and campaign.get("SchemaVersion") == 2, "Unsupported campaign schema.")
    for key in ("SourceRevision", "Protocol", "Budget"):
        require(campaign.get(key) == plan[key], "Campaign differs from plan: " + key)
    require(campaign.get("Partition") == "development", "Do not relabel development fixtures as held-out evidence.")
    require(campaign.get("Seeds") == plan["SeedCount"] and campaign.get("Methods") == plan["Methods"], "Fixed sample/method plan mismatch.")
    require(integer(campaign.get("Seeds"), 1, 1000) and integer(campaign.get("TaskCount"), 1, 256)
            and campaign.get("InitialPopulation") == 8 and campaign.get("Dimensions") == 8, "Invalid numeric campaign metadata.")
    tasks, methods = [t["Name"] for t in plan["Tasks"]], plan["Methods"]
    require(campaign.get("TaskCount") == len(tasks), "Fixed task plan mismatch.")
    external_protocol = plan["Protocol"] == "numeric-development-v4-external"
    if external_protocol:
        require(type(campaign.get("WorkingTreeSmoke")) is bool and isinstance(campaign.get("EvaluatorBinarySha256"), str)
                and re.fullmatch(r"[0-9a-f]{64}", campaign["EvaluatorBinarySha256"]), "Missing external binary/smoke provenance.")
    raw_runs = campaign.get("Runs")
    require(isinstance(raw_runs, list) and len(raw_runs) <= len(tasks) * len(methods) * plan["SeedCount"], "Invalid campaign run count.")
    indexed = {}
    for run in raw_runs:
        require(isinstance(run, dict), "Invalid run.")
        task, method, seed = run.get("Task"), run.get("Method"), run.get("Seed")
        require(task in tasks and method in methods and integer(seed, 0, plan["SeedCount"] - 1), "Unplanned run cannot be silently dropped.")
        key = (task, method, seed)
        require(key not in indexed, "Duplicate task/method/seed: timing repetitions are not independent runs.")
        indexed[key] = run

    rows, summaries, progress = [], [], []
    by_key = {}
    for task_definition in plan["Tasks"]:
        task, scale = task_definition["Name"], task_definition["Scale"]
        for seed in range(plan["SeedCount"]):
            hashes = {indexed[(task, method, seed)].get("InitialPopulationHash") for method in methods if (task, method, seed) in indexed}
            require(len(hashes) <= 1 and all(isinstance(h, str) and re.fullmatch(r"[0-9a-f]{64}", h) for h in hashes), "Paired initial populations differ or lack identities.")
        for method in methods:
            group = []
            for seed in range(plan["SeedCount"]):
                key = (task, method, seed)
                run = indexed.get(key)
                if run is None:
                    row = dict(Task=task, Method=method, Seed=seed, Status="missing", Utility=0.0, FinalLoss=None,
                               EvaluatorCalls=None, Resources=None, TrajectoryComplete=False, Curve=[])
                else:
                    resources = run.get("Resources", {})
                    require(isinstance(resources, dict) and isinstance(resources.get("Spent"), dict)
                            and isinstance(resources.get("Reserved"), dict), "Missing independent resource counters.")
                    require(integer(run.get("EvaluatorCalls"), 0, 1_000_000) and integer(run.get("Proposals"), 0, 10_000_000)
                            and all(finite(v) for v in resources["Spent"].values())
                            and all(finite(v) for v in resources["Reserved"].values())
                            and integer(resources.get("Unknown"), 0, 10_000_000)
                            and type(resources.get("MaximumViolated")) is bool, "Invalid work counters.")
                    external = external_protocol and method == "ScipyDifferentialEvolutionMatched8"
                    converged = external and run.get("StopReason") == "converged"
                    calls = run["EvaluatorCalls"]
                    within_budget = 8 <= calls <= plan["Budget"] if converged else calls == plan["Budget"]
                    terminal_valid = not external_protocol or run.get("StopReason") == "evaluation-cap" or converged
                    external_valid = True
                    if external:
                        manifest = run.get("EvaluatorManifest") or {}
                        external_valid = (all(integer(run.get(key), 8, plan["Budget"]) and run[key] == calls
                                              for key in ("IndependentEvaluatorCalls", "ControllerDispatches", "OptimizerNfev"))
                                          and run.get("UnknownWork") is False and isinstance(manifest, dict)
                                          and manifest.get("AssemblySha256") == campaign["EvaluatorBinarySha256"]
                                          and manifest.get("InitialPopulationHash") == run.get("InitialPopulationHash")
                                          and manifest.get("Task") == task and manifest.get("Seed") == seed
                                          and manifest.get("Budget") == plan["Budget"])
                    valid = (run.get("Status") == "completed" and finite(run.get("FinalLoss"))
                             and within_budget and terminal_valid and external_valid and resources["Spent"].get("cost_units") == calls
                             and resources["Spent"].get("proposal_calls") == run["Proposals"] - campaign["InitialPopulation"]
                             and all(v == 0 for v in resources["Reserved"].values()) and resources.get("Unknown") == 0
                             and resources.get("MaximumViolated") is False)
                    complete, curve = trajectory(run, plan["Budget"])
                    # Stable for very large losses; all failed/incomplete/missing runs have utility zero.
                    utility = scale / (scale + run["FinalLoss"]) if valid else 0.0
                    row = dict(Task=task, Method=method, Seed=seed, Status="completed" if valid else "failed-or-incomplete",
                               ReportedStatus=run.get("Status"), Utility=utility, FinalLoss=run.get("FinalLoss") if finite(run.get("FinalLoss")) else None,
                               EvaluatorCalls=run["EvaluatorCalls"], Resources={k: v for k, v in resources.items() if k != "Receipts"},
                               TrajectoryComplete=complete, Curve=curve)
                    if external_protocol:
                        row.update(StopReason=run.get("StopReason"), Error=run.get("Error"),
                                   TerminalCarryForward=bool(valid and converged and calls < plan["Budget"]))
                    if external:
                        row.update({key: run.get(key) for key in ("IndependentEvaluatorCalls", "ControllerDispatches", "OptimizerNfev", "UnknownWork")})
                rows.append(row)
                by_key[key] = row
                group.append(row)
            completed = [r["FinalLoss"] for r in group if r["Status"] == "completed"]
            utilities = [r["Utility"] for r in group]
            summaries.append(dict(Task=task, Method=method, Scheduled=len(group), Completed=len(completed),
                                  FailedOrMissing=len(group) - len(completed), MeanUtility=statistics.fmean(utilities),
                                  UtilityVariance=statistics.variance(utilities) if len(utilities) > 1 else None,
                                  MedianLossCompletedOnly=statistics.median(completed) if completed else None,
                                  IncompleteTrajectories=sum(not r["TrajectoryComplete"] for r in group),
                                  RecordedEvaluatorCalls=sum(r["EvaluatorCalls"] or 0 for r in group),
                                  UnknownWorkRuns=sum(r["EvaluatorCalls"] is None for r in group)))
            for at in sorted({8, max(8, plan["Budget"] // 4), max(8, plan["Budget"] // 2), plan["Budget"]}):
                known = []
                for row in group:
                    points = [p for p in row["Curve"] if p["CostUnits"] <= at]
                    if row["TrajectoryComplete"] and points and (row["Curve"][-1]["CostUnits"] >= at or row.get("TerminalCarryForward", False)):
                        known.append(points[-1]["BestLoss"])
                progress.append(dict(Task=task, Method=method, CostUnits=at, KnownRuns=len(known), MissingRuns=len(group) - len(known),
                                     MedianLossKnownOnly=statistics.median(known) if known else None))

    comparisons = []
    alpha = 1 - plan["ConfidenceLevel"]
    for index, comparator in enumerate(plan["Comparators"]):
        groups = [[by_key[(task, plan["PrimaryMethod"], seed)]["Utility"] - by_key[(task, comparator, seed)]["Utility"]
                   for seed in range(plan["SeedCount"])] for task in tasks]
        comparisons.append(dict(Primary=plan["PrimaryMethod"], Comparator=comparator,
                                MeanPairedUtilityDifference=statistics.fmean(map(statistics.fmean, groups)),
                                Interval=interval(groups, plan["BootstrapSamples"], plan["BootstrapSeed"] + index,
                                                  alpha / len(plan["Comparators"]), plan["ResampleTasks"]),
                                Tasks=[dict(Task=task, MeanPairedUtilityDifference=statistics.fmean(group),
                                            ExploratoryInterval=interval([group], plan["BootstrapSamples"], plan["BootstrapSeed"] + index,
                                                                         alpha, False)) for task, group in zip(tasks, groups)]))
    return dict(SchemaVersion=1, Purpose=plan["Purpose"], ConfirmatoryEligible=False, SourceRevision=plan["SourceRevision"],
                Endpoint="mean task-balanced paired difference in scale/(scale+final loss); failures have utility 0",
                ConfidenceLevel=plan["ConfidenceLevel"], ResampleTasks=plan["ResampleTasks"], BootstrapSamples=plan["BootstrapSamples"],
                IntervalMethod="paired percentile bootstrap; nominal Bonferroni tails across aggregate comparisons",
                Caveats=["Retrospective development analysis; no preregistration, power guarantee, release gate or competitor superiority claim.",
                         "Protocol-v4 external convergence is valid below the cap; its final measured incumbent carries forward without invented measurements.",
                         "Timing samples and trajectory points are not independent search runs.",
                         "Intervals are approximate; small or degenerate samples can understate uncertainty.",
                         "Completed-only losses and known-only curves are descriptive, not failure-inclusive comparison endpoints.",
                         "Task resampling does not make a purposively selected suite representative."],
                Summaries=summaries, Comparisons=comparisons, Progress=progress,
                Runs=[dict({k: v for k, v in row.items() if k != "Curve"}, TrajectoryPoints=len(row["Curve"])) for row in rows])


def markdown(report):
    def number(value):
        return "unknown" if value is None else format(value, ".6g")
    lines = ["# Numeric development analysis", "", "Source: `" + report["SourceRevision"] + "`.", "",
             "Retrospective only. No confirmatory or competitive-win claim.", "",
             "Endpoint: " + report["Endpoint"] + ".", "",
             f"Intervals use {report['BootstrapSamples']} paired bootstrap replicates, with {report['ConfidenceLevel']:.0%} nominal family-wise confidence after adjusting aggregate comparisons.",
             "Tasks are " + ("resampled as clusters." if report["ResampleTasks"] else "fixed; intervals describe seed variability on this suite only."), "",
             "## Paired effects", "",
             "Positive differences favor the primary method. Failed, incomplete and missing runs remain in the denominator.", "",
             "| Primary | Comparator | Utility difference | Adjusted bootstrap interval |", "| --- | --- | ---: | --- |"]
    for comparison in report["Comparisons"]:
        low, high = comparison["Interval"]
        lines.append(f"| {comparison['Primary']} | {comparison['Comparator']} | {number(comparison['MeanPairedUtilityDifference'])} | [{number(low)}, {number(high)}] |")
    lines += ["", "## Every task and method", "", "Loss medians below are completed-only; utility includes every scheduled run.", "",
              "| Task | Method | Completed / planned | Median loss | Mean utility | Incomplete traces |", "| --- | --- | ---: | ---: | ---: | ---: |"]
    for row in report["Summaries"]:
        lines.append(f"| {row['Task']} | {row['Method']} | {row['Completed']} / {row['Scheduled']} | {number(row['MedianLossCompletedOnly'])} | {number(row['MeanUtility'])} | {row['IncompleteTrajectories']} |")
    lines += ["", "## Failed or missing runs", ""]
    failed = [r for r in report["Runs"] if r["Status"] != "completed"]
    lines.extend(f"- {r['Task']} / {r['Method']} / seed {r['Seed']}: {r['Status']}; recorded evaluator calls {r['EvaluatorCalls']}." for r in failed)
    if not failed:
        lines.append("None.")
    lines += ["", "## Interpretation limits", ""] + ["- " + caveat for caveat in report["Caveats"]]
    lines += ["", "Task-level exploratory intervals, budget-indexed progress, independent resource counters and every run are in `report.json`.",
              "Full unaggregated trajectories remain in the original input, identified by its SHA-256 in the report.", ""]
    return "\n".join(lines)


def load_json(path, limit):
    require(path.stat().st_size <= limit, "Input exceeds its file-size bound.")
    raw = path.read_bytes()
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "Duplicate JSON field: " + key)
            result[key] = value
        return result
    def constant(value):
        raise ValueError("Non-finite JSON number: " + value)
    return json.loads(raw, object_pairs_hook=unique, parse_constant=constant), hashlib.sha256(raw).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()
    plan, plan_hash = load_json(args.plan, 1024 * 1024)
    campaign, input_hash = load_json(args.input, 256 * 1024 * 1024)
    report = analyze(campaign, plan)
    report.update(InputSha256=input_hash, PlanSha256=plan_hash, PythonVersion=sys.version.split()[0])
    args.output_dir.mkdir(parents=False, exist_ok=False)
    (args.output_dir / "report.json").write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    (args.output_dir / "report.md").write_text(markdown(report), encoding="utf-8")
    (args.output_dir / "analysis-plan.json").write_text(json.dumps(plan, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Analyzed {len(report['Runs'])} scheduled runs; confirmatory eligible: false.")


if __name__ == "__main__":
    main()
