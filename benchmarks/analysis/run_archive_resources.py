"""Fresh-process, fixed-plan archive resource/quality experiment; standard library only."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import subprocess
import sys
import time
from analyze import interval, load_json, require

METHODS = ("SparseGrid", "FixedCentroid")
TASKS = ("Quadratic", "Rippled")


def dump(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as output:
        json.dump(value, output, indent=2, allow_nan=False)
        output.write("\n")


def plan(revision, smoke=False):
    require(re.fullmatch(r"[0-9a-f]{40}", revision) or (smoke and revision == "working-tree-smoke"), "Pin source for a primary campaign.")
    return {"SchemaVersion": 1, "Protocol": "archive-resource-development-v1", "SourceRevision": revision,
            "Purpose": "authored-development-smoke" if smoke else "fixed-plan-authored-development",
            "Tasks": list(TASKS), "Methods": list(METHODS), "Dimensions": [12, 20], "SeedCount": 2 if smoke else 32,
            "EvaluationBudget": 32 if smoke else 256, "PeakResidentBudgetMiB": 256,
            "EliteCapacity": 32, "MaximumGridCells": 10_000_000, "WorkerTimeoutSeconds": 60,
            "Phases": ["primary", "replay"], "BootstrapSamples": 10000, "BootstrapSeed": 20260911,
            "PrimaryEndpoint": "Per-seed mean across the four task/dimension contexts of FixedCentroid minus SparseGrid common-reference utility; zero utility for failed/over-budget cases.",
            "MemoryEndpoint": "Same declared 256 MiB observed process-lifetime peak resident budget through setup/search/projection, not equal actual bytes or an OS hard limit.",
            "Order": "Sequential fresh child per case; AB/BA alternates by seed, reversed in replay; no rerun-until-pass or optional seed extension.",
            "Limitations": "Authored tasks and frozen uniform Voronoi sites, not fitted CVT or representative held-out superiority. RSS includes startup/shared pages; CPU/wall/allocation comparisons are descriptive on a non-exclusive host. Only the pooled quality endpoint has a predeclared bootstrap interval; per-context effects are descriptive. 32/64D default grid support failures are separate contract tests, not pooled as quality wins."}


def validate_case(report, specification, configuration):
    task, dimension, seed, method = specification
    require(report["Protocol"] == "archive-resource-case-development-v1" and report["SchemaVersion"] == 2, "Worker protocol mismatch.")
    require(report["SourceRevision"] == configuration["SourceRevision"], "Worker source label mismatch.")
    require(report["Budget"] == configuration["EvaluationBudget"] and report["Dimensions"] == dimension and report["Seeds"] == 1 and report["SelectedSeed"] == seed, "Case budget/dimensions/seed mismatch.")
    require(report["EliteCapacity"] == 32 and report["MaximumGridCells"] == configuration["MaximumGridCells"], "Archive capacity/guard mismatch.")
    require(len(report["Runs"]) == 1, "Expected exactly one physical worker case.")
    row = report["Runs"][0]
    require((row["Task"], row["Method"], row["Seed"]) == (task + str(dimension), method, seed), "Case identity mismatch.")
    measure = report["Measurement"]
    cap = configuration["PeakResidentBudgetMiB"] * 1024 * 1024
    require(measure["PeakResidentBudgetBytes"] == cap and type(measure["PeakResidentBytes"]) is int and measure["PeakResidentBytes"] > 0, "Missing measured RSS or mismatched cap.")
    require(type(measure["MemoryBudgetExceeded"]) is bool and measure["MemoryBudgetExceeded"] == (measure["PeakResidentBytes"] > cap), "Memory gate mismatch.")
    if configuration["SourceRevision"] != "working-tree-smoke":
        require(measure["CoreInformationalVersion"].endswith("+" + configuration["SourceRevision"]), "Built core is not the pinned revision.")
    for key in ("ElapsedMilliseconds", "CpuMilliseconds", "AllocatedBytes"):
        require(type(measure[key]) in (int, float) and math.isfinite(measure[key]) and measure[key] >= 0, "Invalid resource measurement.")
    for key in ("WorkerSha256", "CoreSha256"):
        require(re.fullmatch(r"[0-9a-f]{64}", measure[key]) is not None, "Missing binary hash.")
    calls = row["EvaluatorCalls"]
    require(type(calls) is int and 0 <= calls <= configuration["EvaluationBudget"], "Physical evaluation cap violated.")
    resources = row["Resources"]
    require(resources["Spent"].get("cost_units", 0) == calls and resources["Unknown"] == 0 and not resources["MaximumViolated"], "Evaluator ledger mismatch.")
    require(all(value == 0 for value in resources["Reserved"].values()), "Unsettled worker accounting.")
    require(0 <= resources["Spent"].get("proposal_calls", 0) <= configuration["EvaluationBudget"] * 4, "Proposal cap violated.")
    require(len(row["Samples"]) == row["Proposals"], "Proposal trace is incomplete.")
    require(sum(sample["Attempts"] for sample in row["Samples"]) == calls and sum(sample["CostUnits"] for sample in row["Samples"]) == calls, "Trace omits physical work.")
    require(0 <= row["ReferenceUtility"] <= 1 and 0 <= row["OccupiedSearchCells"] <= 32 and 0 <= row["OccupiedReferenceCells"] <= 32, "Invalid quality/retention bounds.")
    if row["Status"] == "completed":
        require(not measure["MemoryBudgetExceeded"] and calls == configuration["EvaluationBudget"], "Completed case exceeded a resource cap or stopped early.")
        require(resources["Spent"].get("proposal_calls", 0) == row["Proposals"] - 8, "Proposal work is not charged.")
        projection = row["Projection"]
        require(projection["SourceDefinitionHash"] == row["ArchiveDefinitionHash"] and projection["SourceEliteCount"] == row["OccupiedSearchCells"], "Projection source provenance mismatch.")
        require(projection["RetainedEliteCount"] == row["OccupiedReferenceCells"] and projection["CollisionDiscardedEliteCount"] == row["OccupiedSearchCells"] - row["OccupiedReferenceCells"], "Projection discards are missing.")
        require(projection["TargetVersion"] > projection["SourceVersion"], "Projection version did not advance.")
    else:
        require(row["Status"] in ("failed", "incomplete", "configuration-failed", "memory-budget-exceeded") and row["ReferenceUtility"] == 0, "Failures must be retained with zero utility.")
    if measure["MemoryBudgetExceeded"]:
        require(row["Status"] == "memory-budget-exceeded", "Memory overrun hidden as another status.")
    return row


def quality_fingerprint(report):
    # Deliberately excludes physical process IDs/timing/RSS; replay still executes and pays for work.
    return hashlib.sha256(json.dumps(report["Runs"], sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()


def summarize(configuration, records):
    expected = {(task, dimension, seed, method, phase) for phase in configuration["Phases"] for task in configuration["Tasks"]
                for dimension in configuration["Dimensions"] for seed in range(configuration["SeedCount"]) for method in configuration["Methods"]}
    indexed = {}
    for record in records:
        key = tuple(record["Case"]) + (record["Phase"],)
        require(key in expected and key not in indexed, "Unexpected or duplicate scheduled case.")
        indexed[key] = record
    require(set(indexed) == expected, "Missing scheduled cases must not disappear.")
    paired = []
    binary_pairs = set()
    reference_hashes = {}
    replay_equal = True
    unknown_calls = 0
    measured_calls = {phase: 0 for phase in configuration["Phases"]}
    for record in records:
        if record.get("Report") is None:
            unknown_calls += 1
            continue
        report = record["Report"]
        row = validate_case(report, record["Case"], configuration)
        measured_calls[record["Phase"]] += row["EvaluatorCalls"]
        binary_pairs.add((report["Measurement"]["WorkerSha256"], report["Measurement"]["CoreSha256"]))
        dimension = record["Case"][1]
        reference_hashes.setdefault(dimension, set()).add(report["ReferenceDefinition"]["DefinitionHash"])
    require(len(binary_pairs) <= 1, "Campaign mixed worker/core binaries.")
    require(all(len(values) == 1 for values in reference_hashes.values()), "Reporting geometry changed inside a context.")
    for task in configuration["Tasks"]:
        for dimension in configuration["Dimensions"]:
            for seed in range(configuration["SeedCount"]):
                pair = [indexed[(task, dimension, seed, method, "primary")] for method in METHODS]
                reports = [record.get("Report") for record in pair]
                if all(reports):
                    require(reports[0]["Runs"][0]["InitialPopulationHash"] == reports[1]["Runs"][0]["InitialPopulationHash"], "Unpaired prior information.")
                utilities = [report["Runs"][0]["ReferenceUtility"] if report else 0 for report in reports]
                paired.append({"Task": task, "Dimensions": dimension, "Seed": seed, "Difference": utilities[1] - utilities[0]})
                for method in METHODS:
                    first = indexed[(task, dimension, seed, method, "primary")].get("Report")
                    second = indexed[(task, dimension, seed, method, "replay")].get("Report")
                    replay_equal &= bool(first and second and quality_fingerprint(first) == quality_fingerprint(second))
    effects = [statistics.mean(item["Difference"] for item in paired if item["Seed"] == seed) for seed in range(configuration["SeedCount"])]
    confidence = interval([effects], configuration["BootstrapSamples"], configuration["BootstrapSeed"], 0.05, False)
    failed = sum(record.get("Report") is None or record["Report"]["Runs"][0]["Status"] != "completed" for record in records)
    return {"ScheduledCases": len(expected), "FailedOrIncompleteCases": failed, "UnknownPhysicalCallCases": unknown_calls,
            "MeasuredPhysicalEvaluationsByPhase": measured_calls, "QualityReplayEqual": replay_equal,
            "PrimaryEffect": statistics.mean(effects), "PrimaryPairedSeedBootstrap95": confidence,
            "SeedEffects": effects, "ContextPairs": paired,
            "Inference": "Fixed-context, seed-block percentile bootstrap for the single pooled endpoint; not task-population or competitor generalization. Smoke intervals are not inferential evidence.",
            "ResourceComparisons": resource_comparisons(configuration, records)}


def resource_comparisons(configuration, records):
    result = []
    for task in configuration["Tasks"]:
        for dimension in configuration["Dimensions"]:
            for method in METHODS:
                reports = [record["Report"] for record in records if record.get("Report") and record["Phase"] == "primary"
                           and record["Case"][0:2] == [task, dimension] and record["Case"][3] == method]
                if reports:
                    result.append({"Task": task, "Dimensions": dimension, "Method": method, "MeasuredCases": len(reports),
                        "CompletedCases": sum(report["Runs"][0]["Status"] == "completed" for report in reports),
                        **{"Median" + metric: statistics.median(report["Measurement"][metric] for report in reports)
                           for metric in ("PeakResidentBytes", "ElapsedMilliseconds", "CpuMilliseconds", "AllocatedBytes")}})
    return result


def run(configuration, worker, output):
    output.mkdir(parents=True, exist_ok=False)
    dump(output / "plan.json", configuration)  # Freeze before launching any scheduled measurement.
    records = []
    for phase in configuration["Phases"]:
        for task in configuration["Tasks"]:
            for dimension in configuration["Dimensions"]:
                for seed in range(configuration["SeedCount"]):
                    methods = METHODS if (seed + (phase == "replay")) % 2 == 0 else METHODS[::-1]
                    for method in methods:
                        spec = [task, dimension, seed, method]
                        name = f"{phase}-{task}-{dimension}-{seed}-{method}"
                        report_path = output / (name + ".json")
                        started = time.monotonic()
                        record = {"Phase": phase, "Case": spec, "ReportFile": report_path.name, "Report": None}
                        with (output / (name + ".stdout.log")).open("xb") as stdout, (output / (name + ".stderr.log")).open("xb") as stderr:
                            try:
                                process = subprocess.Popen(["dotnet", str(worker), "--archive-resource-case", task, method, str(seed), str(dimension),
                                    str(configuration["EvaluationBudget"]), str(configuration["PeakResidentBudgetMiB"]), configuration["SourceRevision"], str(report_path)],
                                    stdout=stdout, stderr=stderr, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                                record["OwnedProcessId"] = process.pid
                                try:
                                    record["ExitCode"] = process.wait(timeout=configuration["WorkerTimeoutSeconds"])
                                except subprocess.TimeoutExpired:
                                    process.kill()  # Only the exact child this runner started.
                                    process.wait()
                                    record["Failure"] = "worker-timeout; physical calls unknown unless a valid report was committed"
                            except OSError as error:
                                record["Failure"] = type(error).__name__
                        record["ParentElapsedSeconds"] = time.monotonic() - started
                        if report_path.exists() and report_path.stat().st_size <= 16 * 1024 * 1024:
                            try:
                                report, report_hash = load_json(report_path, 16 * 1024 * 1024)
                                row = validate_case(report, spec, configuration)
                                require(record.get("ExitCode") == (0 if row["Status"] == "completed" else 1), "Worker exit/report mismatch.")
                                require(report["Measurement"]["ProcessId"] == record["OwnedProcessId"], "Worker PID mismatch.")
                                record["Report"] = report
                                record["ReportSha256"] = report_hash
                            except (ValueError, KeyError, TypeError) as error:
                                record["Failure"] = f"invalid-report: {error}"
                        records.append(record)
                        dump(output / (name + ".record.json"), record)
                        if len(records) % 16 == 0:
                            print(f"{phase}: {len(records)} cumulative scheduled cases retained", flush=True)
    result = summarize(configuration, records)
    dump(output / "summary.json", result)
    return result


def verify_directory(output):
    configuration, _ = load_json(output / "plan.json", 64 * 1024)
    require(configuration == plan(configuration["SourceRevision"], configuration["Purpose"] == "authored-development-smoke"), "Unrecognized or modified frozen plan.")
    paths = sorted(output.glob("*.record.json"))
    require(len(paths) <= 512 and sum(path.stat().st_size for path in paths) <= 512 * 1024 * 1024, "Evidence exceeds its bounds.")
    records = []
    for path in paths:
        record, _ = load_json(path, 20 * 1024 * 1024)
        if record.get("Report") is not None:
            filename = record["ReportFile"]
            require(Path(filename).name == filename, "Report must remain inside the evidence directory.")
            raw, digest = load_json(output / filename, 16 * 1024 * 1024)
            require(digest == record["ReportSha256"] and raw == record["Report"], "Raw report differs from its retained record.")
        records.append(record)
    result = summarize(configuration, records)
    retained, _ = load_json(output / "summary.json", 1024 * 1024)
    require(result == retained, "Retained summary does not reproduce from raw cases.")
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--worker", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--revision")
    parser.add_argument("--verify", type=Path)
    parser.add_argument("--smoke", action="store_true")
    args = parser.parse_args()
    if args.verify:
        require(not args.worker and not args.output and not args.revision and not args.smoke, "Verification cannot launch a campaign.")
        result = verify_directory(args.verify)
    else:
        require(args.worker and args.output and args.revision, "Provide worker, output and source revision.")
        if not args.smoke:
            repo = Path(__file__).resolve().parents[2]
            current = subprocess.run(["git", "rev-parse", "HEAD"], cwd=repo, check=True, capture_output=True, text=True, timeout=10).stdout.strip()
            require(current == args.revision, "Primary campaign must use the checked-out source revision.")
            dirty = subprocess.run(["git", "status", "--porcelain", "--", "src", "benchmarks/AiDotNet.Evolution.Quality",
                "benchmarks/analysis/run_archive_resources.py", "benchmarks/analysis/analyze.py"], cwd=repo, check=True,
                capture_output=True, text=True, timeout=10).stdout
            require(not dirty.strip(), "Commit runtime/analysis sources before a primary campaign.")
        result = run(plan(args.revision, args.smoke), args.worker.resolve(strict=True), args.output.resolve())
    print(json.dumps({key: value for key, value in result.items() if key not in ("ContextPairs", "SeedEffects", "ResourceComparisons")}, indent=2))
    return 0 if result["FailedOrIncompleteCases"] == 0 and result["QualityReplayEqual"] else 1


if __name__ == "__main__":
    sys.exit(main())
