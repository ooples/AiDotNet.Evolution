"""Bounded retrospective pipeline analysis; timing repeats stay inside paired seed clusters."""
import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path
import re
import statistics

from analyze import finite, integer, interval, load_json, percentile, require

METHODS = ("Batch", "Continuous", "PipelineSerial", "PipelineConcurrent")
TASKS = ("Sphere", "Rugged")
PROFILES = ("ZeroLatency", "ProposalBound", "Mixed")


def execution_valid(row, replay=False):
    try:
        proposals, evaluations, resources = row["ProposalResponses"], row["EvaluationResponses"], row["Resources"]
        expected = 0.08 + len(proposals) * 0.25 + len(evaluations)
        counters = row["Counters"]
        valid = (row["Status"] == "completed" and row["Error"] is None and len(proposals) == 24 and 1 <= len(evaluations) <= 32
                 and counters["Proposals"] == 32 and counters["EvaluationAttempts"] == len(evaluations)
                 and counters["CompletedEvaluations"] == len(evaluations) and row["ObservedGenerations"] == list(range(1, 25))
                 and finite(row["BestQuality"]) and 0 < row["BestQuality"] <= 1 and finite(row["ElapsedSeconds"]) and row["ElapsedSeconds"] > 0
                 and finite(row["ProcessCpuSeconds"]) and integer(row["ProcessAllocatedBytes"], 0, 2**63 - 1)
                 and resources["Spent"]["cost_units"] == expected <= 40 and resources["Reserved"]["cost_units"] == 0
                 and resources["Unknown"] == 0 and resources["Admitted"] == resources["Settled"] and resources["DroppedReceipts"] == 0
                 and resources["MaximumViolated"] is False
                 and row["PhysicalProposalCalls"] == (0 if replay else 24) and row["PhysicalEvaluationCalls"] == (0 if replay else len(evaluations))
                 and row["ReplayedProposalCalls"] == (24 if replay else 0) and row["ReplayedEvaluationCalls"] == (len(evaluations) if replay else 0))
        receipts = resources["Receipts"]
        valid &= (len(receipts) == resources["Settled"] and len({r["OperationId"] for r in receipts}) == len(receipts)
                  and abs(sum(r["Charged"]["Amounts"]["cost_units"] for r in receipts) - expected) < 1e-9)
        if row["Pipeline"] is not None:
            pipeline = row["Pipeline"]
            valid &= (pipeline["RunId"] == row["RunId"] and pipeline["FirstEvaluationId"] == 0 and pipeline["AbortedWaves"] == 0
                      and pipeline["WaveSize"] == 8 and pipeline["ProposalQueueCapacity"] == pipeline["EvaluationQueueCapacity"] == 2
                      and pipeline["ProposalQueuePeak"] <= 2 and pipeline["EvaluationQueuePeak"] <= 2
                      and pipeline["ProposalRunningPeak"] <= pipeline["ProposalWorkers"] and pipeline["EvaluationRunningPeak"] <= 4
                      and pipeline["IsScheduleComplete"] is True and pipeline["DroppedScheduleRecords"] == 0 and len(pipeline["Schedule"]) == 64)
        return bool(valid)
    except (KeyError, TypeError, ValueError):
        return False


def analyze(campaign, source):
    require(re.fullmatch(r"[0-9a-f]{40}", source) is not None, "Pin a complete source revision.")
    require(campaign.get("Protocol") == "authored-proposal-pipeline-v1", "Unsupported pipeline protocol.")
    require(campaign.get("AssemblyVersion", "").endswith("+" + source), "Assembly does not match the source pin.")
    require(campaign.get("CoreAssemblyVersion", "").endswith("+" + source), "Core assembly does not match the source pin.")
    require(re.fullmatch(r"[0-9a-f]{64}", campaign.get("AssemblySha256", "")) is not None, "Missing assembly hash.")
    require(re.fullmatch(r"[0-9a-f]{64}", campaign.get("CoreAssemblySha256", "")) is not None, "Missing core assembly hash.")
    seeds, repeats = campaign.get("Seeds"), campaign.get("TimingRepetitions")
    require(integer(seeds, 1, 16) and integer(repeats, 1, 5), "Invalid seed/repetition bounds.")
    require((campaign.get("ProposalCap"), campaign.get("EvaluationAttemptCap"), campaign.get("CostCap")) == (32, 32, 40), "Budget protocol differs.")
    rows = campaign.get("Runs")
    require(isinstance(rows, list) and len(rows) == 24 * seeds * repeats, "Missing or extra scheduled runs.")
    scheduled = {(task, profile, method, seed, repeat) for task in TASKS for profile in PROFILES for method in METHODS
                 for seed in range(seeds) for repeat in range(repeats)}
    indexed, valid = {}, {}
    for row in rows:
        key = tuple(row[field] for field in ("Task", "Profile", "Method", "Seed", "TimingRepetition"))
        require(key in scheduled and key not in indexed, "Unexpected or duplicate run identity.")
        indexed[key] = row
        live, replay = row["Live"], row["Replay"]
        valid[key] = (row["Status"] == "completed" and row["ReplayMatches"] is True and execution_valid(live) and execution_valid(replay, True)
                      and live["StateHash"] == replay["StateHash"] and live["LogicalEvidenceSha256"] == replay["LogicalEvidenceSha256"]
                      and all(live[field] == replay[field] for field in ("ProposalResponses", "EvaluationResponses", "ObservedGenerations"))
                      and sorted(live["Resources"]["Receipts"], key=lambda r: r["OperationId"]) == sorted(replay["Resources"]["Receipts"], key=lambda r: r["OperationId"])
                      and (live["Pipeline"] is None or live["Pipeline"]["Schedule"] == replay["Pipeline"]["Schedule"]))
        for execution in (live, replay):
            pipeline = execution.get("Pipeline")
            expected_workers = 4 if key[2] == "PipelineConcurrent" else 1
            valid[key] &= ((pipeline is not None and pipeline["ProposalWorkers"] == expected_workers and pipeline["EvaluationWorkers"] == 4)
                           if key[2].startswith("Pipeline") else pipeline is None)
    for task in TASKS:
        for profile in PROFILES:
            for seed in range(seeds):
                for repeat in range(repeats):
                    group = [indexed[task, profile, method, seed, repeat]["Live"] for method in METHODS]
                    require(len({r["InitialPopulationHash"] for r in group}) == 1, "Matched cohorts differ.")
    warmups = campaign.get("WarmupRuns")
    require(isinstance(warmups, list) and len(warmups) == 4, "Missing warmup evidence.")
    warmups_valid = sum(execution_valid(row) for row in warmups)
    summaries, comparisons = [], []
    for profile in PROFILES:
        for method in METHODS:
            keys = [key for key in indexed if key[1:3] == (profile, method)]
            good = [indexed[key]["Live"] for key in keys if valid[key]]
            durations = sorted(row["ElapsedSeconds"] for row in good)
            summaries.append(dict(Profile=profile, Method=method, Scheduled=len(keys), Valid=len(good),
                                  MeanFailureInclusiveQuality=statistics.fmean(indexed[k]["Live"]["BestQuality"] if valid[k] else 0 for k in keys),
                                  MedianCompletedElapsedSeconds=statistics.median(durations) if good else None,
                                  P95CompletedElapsedSeconds=percentile(durations, 0.95) if good else None,
                                  MeanCompletedCost=statistics.fmean(row["Resources"]["Spent"]["cost_units"] for row in good) if good else None,
                                  MedianCompletedAllocatedBytes=statistics.median(row["ProcessAllocatedBytes"] for row in good) if good else None,
                                  MedianCompletedProcessCpuSeconds=statistics.median(row["ProcessCpuSeconds"] for row in good) if good else None))
        for comparator in METHODS[:-1]:
            quality, speed, costs = [], [], []
            for seed in range(seeds):
                paired_quality, paired_speed, paired_cost = [], [], []
                for task in TASKS:
                    for repeat in range(repeats):
                        a, b = (task, profile, "PipelineConcurrent", seed, repeat), (task, profile, comparator, seed, repeat)
                        left, right = indexed[a]["Live"], indexed[b]["Live"]
                        paired_quality.append((left["BestQuality"] if valid[a] else 0) - (right["BestQuality"] if valid[b] else 0))
                        paired_cost.append(left["Resources"]["Spent"]["cost_units"] - right["Resources"]["Spent"]["cost_units"])
                        if valid[a] and valid[b]:
                            paired_speed.append(math.log(right["ElapsedSeconds"] / left["ElapsedSeconds"]))
                quality.append(statistics.fmean(paired_quality)); costs.append(statistics.fmean(paired_cost))
                if len(paired_speed) == len(TASKS) * repeats:
                    speed.append(statistics.fmean(paired_speed))
            speed_complete = len(speed) == seeds and warmups_valid == 4
            comparisons.append(dict(Profile=profile, Primary="PipelineConcurrent", Comparator=comparator, SeedClusters=seeds,
                                    MeanPairedQualityDifference=statistics.fmean(quality), QualityInterval=interval([quality], 10000, 20, 0.05 / 18, False),
                                    MeanPairedCostDifference=statistics.fmean(costs),
                                    GeometricElapsedSpeedRatio=math.exp(statistics.fmean(speed)) if speed_complete else None,
                                    SpeedRatioInterval=[math.exp(v) for v in interval([speed], 10000, 20, 0.05 / 18, False)] if speed_complete else None,
                                    WorstPairedSeedQualityDifference=min(quality), TimingEligible=speed_complete))
    return dict(Protocol=campaign["Protocol"], SourceRevision=source, AssemblySha256=campaign["AssemblySha256"],
                CoreAssemblySha256=campaign["CoreAssemblySha256"], Scheduled=len(rows),
                Valid=sum(valid.values()), WarmupsValid=warmups_valid,
                BootstrapSamples=10000, BootstrapSeed=20, NominalFamilyConfidence=0.95, AdjustedEndpoints=18,
                SeedClusters=seeds, TimingRepetitionsPerSeed=repeats, ConfirmatoryEligible=False,
                Interpretation="Two fixed authored tasks, artificial delays and declared work tariffs on a shared host. "
                "Pair methods first; average tasks/timing repeats within each seed, then resample seed clusters. "
                "Failed executions score zero quality and suppress the affected timing comparison. "
                "Completed-only latency tails are descriptive, not independent replications. "
                "Offline response replay executes no physical backend work; repeated timings are not additional search seeds. "
                "Small-seed bootstrap intervals cannot establish representative or competitor superiority.",
                PhysicalProposalCalls=sum(row["Live"]["PhysicalProposalCalls"] for row in rows),
                PhysicalEvaluationCalls=sum(row["Live"]["PhysicalEvaluationCalls"] for row in rows),
                FailedRuns=[list(key) for key in indexed if not valid[key]], Summaries=summaries, Comparisons=comparisons)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path)
    parser.add_argument("--source")
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--verify-evidence", type=Path)
    args = parser.parse_args()
    if args.verify_evidence is not None:
        require(args.input is None and args.source is None and args.output_dir is None, "Verification is read-only; do not combine output arguments.")
        report = verify_evidence(args.verify_evidence)
        print(f"Verified pipeline hash chain and recomputed {report['Scheduled']} scheduled runs.")
        return
    require(args.input is not None and args.source is not None and args.output_dir is not None, "Supply input, source and a new output directory.")
    campaign, raw_hash = load_json(args.input, 256 * 1024 * 1024)
    report = analyze(campaign, args.source)
    report["InputSha256"] = raw_hash
    compressed = gzip.compress(args.input.read_bytes(), mtime=0)
    report["CompressedSha256"] = hashlib.sha256(compressed).hexdigest()
    args.output_dir.mkdir(parents=False, exist_ok=False)
    (args.output_dir / "raw.json.gz").write_bytes(compressed)
    (args.output_dir / "analysis.json").write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(f"Pipeline analysis: {report['Valid']}/{report['Scheduled']} valid; {report['SeedClusters']} seed clusters; confirmatory eligible: false.")


def verify_evidence(directory):
    report, _ = load_json(directory / "analysis.json", 1024 * 1024)
    compressed_path = directory / "raw.json.gz"
    require(compressed_path.stat().st_size <= 32 * 1024 * 1024, "Compressed evidence exceeds its bound.")
    require(hashlib.sha256(compressed_path.read_bytes()).hexdigest() == report["CompressedSha256"], "Compressed evidence hash mismatch.")
    with gzip.open(compressed_path, "rb") as stream:
        raw = stream.read(256 * 1024 * 1024 + 1)
    require(len(raw) <= 256 * 1024 * 1024, "Expanded evidence exceeds its bound.")
    require(hashlib.sha256(raw).hexdigest() == report["InputSha256"], "Raw evidence hash mismatch.")
    recomputed = analyze(json.loads(raw), report["SourceRevision"])
    require(equivalent(recomputed, {k: v for k, v in report.items() if k not in ("InputSha256", "CompressedSha256")}), "Analysis differs from retained raw evidence.")
    return report


def equivalent(left, right):
    """Allow only libm roundoff across Python platforms, not changed counts, identities or evidence."""
    if type(left) is not type(right):
        return False
    if isinstance(left, dict):
        return left.keys() == right.keys() and all(equivalent(left[key], right[key]) for key in left)
    if isinstance(left, list):
        return len(left) == len(right) and all(equivalent(a, b) for a, b in zip(left, right))
    if isinstance(left, float):
        return math.isclose(left, right, rel_tol=1e-12, abs_tol=1e-15)
    return left == right


if __name__ == "__main__":
    main()
