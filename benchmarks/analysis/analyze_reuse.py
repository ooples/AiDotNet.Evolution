"""Audit raw noisy reuse and paired prior information; stdlib only, no evaluator/model calls."""
import argparse
import base64
from datetime import datetime
import hashlib
import json
import math
from pathlib import Path
import statistics
from analyze import interval, load_json, require


def text_json(text):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate embedded JSON field.")
            result[key] = value
        return result
    def invalid(value):
        raise ValueError("Nonfinite embedded JSON: " + value)
    require(len(text.encode("utf-8")) <= 8 * 1024 * 1024, "Embedded JSON exceeds bounds.")
    return json.loads(text, object_pairs_hook=pairs, parse_constant=invalid)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def validate_plan(plan):
    require(plan["Protocol"] == "noisy-reuse-authored-development-v3" and plan["SchemaVersion"] == 1, "Unknown reuse protocol.")
    require(plan["SeedCount"] in (2, 32) and plan["Tasks"] == ["Quadratic", "Rippled"] and
            plan["Phases"] == ["cold", "warm", "force-fresh", "expired"] and plan["Methods"] == ["AlwaysFresh", "ExistingSamples"] and
            plan["Executions"] == ["primary", "replay"], "Changed campaign contexts.")
    require((plan["AggregateEvaluationCap"], plan["SamplesPerAggregate"], plan["PhysicalObservationCap"], plan["ProposalCallCap"],
             plan["PriorAggregates"], plan["ImportedSeeds"]) == (64, 5, 320, 256, 16, 8), "Changed campaign budgets.")
    require((plan["MaximumAgeMinutes"], plan["WarmAgeMinutes"], plan["ExpiredAgeMinutes"]) == (60, 1, 121), "Changed freshness schedule.")
    require(plan["PhaseSeedOffsets"] == {"prior": 100000, "cold": 200000, "warm": 300000, "force-fresh": 400000, "expired": 500000}, "Changed fresh/replay streams.")
    require(plan["BootstrapSamples"] == 10000 and plan["BootstrapSeed"] == 20260911, "Changed primary analysis.")
    require((plan["ConfirmationAggregates"], plan["ConfirmationSamples"], plan["ConfirmationSeedOffset"]) == (5, 25, 1000000), "Changed independent confirmation budget/stream.")
    if plan["SourceRevision"] == "working-tree-smoke":
        require(plan["SeedCount"] == 2, "Primary requires a source pin.")
    else:
        require(len(plan["SourceRevision"]) == 40 and plan["CoreInformationalVersion"].endswith("+" + plan["SourceRevision"]), "Core/source mismatch.")


def raw_observation(group, expected_digest, trace, seen_samples, seen_artifacts):
    require(len(expected_digest) == 64 and all(c in "0123456789abcdef" for c in expected_digest), "Unsafe raw evidence identity.")
    raw, actual_digest = load_json(group / "raw" / (expected_digest + ".json"), 1024 * 1024)
    require(actual_digest == expected_digest, "Raw evidence checksum mismatch.")
    seen_artifacts.add((group, expected_digest))
    origin, stored = text_json(trace["Origin"]), text_json(raw["Origin"])
    kind = origin["Kind"]
    require(kind in (0, 1) and stored["Kind"] == 0, "Invalid fresh/reused provenance.")
    expected = dict(stored, Kind=kind)
    require(origin == expected and raw["ScopeKey"] == origin["ScopeKey"], "Reuse changed original provenance or uncertainty.")
    require(raw["Genome"] == trace["Genome"], "Raw evidence belongs to a different candidate.")
    values = raw["Values"]
    require(len(values) == len(origin["SampleIds"]) == 5 and len(set(origin["SampleIds"])) == 5, "Sample count or identity mismatch.")
    require(all(type(value) in (int, float) and math.isfinite(value) for value in values), "Nonfinite raw observation.")
    mean = statistics.mean(values)
    error = math.sqrt(sum((value - mean) ** 2 for value in values) / 20)
    for left, right in ((mean, raw["Mean"]), (mean, trace["Quality"]), (error, raw["StandardError"]), (error, origin["StandardError"])):
        require(math.isclose(left, right, rel_tol=1e-12, abs_tol=1e-12), "Raw mean/standard error does not reproduce the reused summary.")
    require(origin["OriginalCostUnits"] == 5 and origin["StatisticsVersion"] == "arithmetic-mean-sample-se-v1" and
            origin["LowerConfidenceBound"] is None and origin["UpperConfidenceBound"] is None and origin["ConfidenceLevel"] is None,
            "Unsupported acquisition cost or uncertainty policy.")
    for sample_id in origin["SampleIds"]:
        require(sample_id not in seen_samples or seen_samples[sample_id] == expected_digest, "One sample identity was reassigned to different raw observations.")
        seen_samples[sample_id] = expected_digest
    return origin, raw


def validate_run(group, row, plan, prior_files, repertoire_digest, imported, seen_samples, seen_artifacts):
    phase, method = row["Phase"], row["Method"]
    budget = 16 if phase == "prior" else 64
    require(row["Valid"] is True and row["FreshAggregates"] * 5 == row["CurrentPhysicalObservations"] <= budget * 5, "Lost or over-budget physical observations.")
    ledger = row["Resources"]
    require(ledger["Spent"].get("cost_units", 0) == row["CurrentPhysicalObservations"] and ledger["Unknown"] == 0 and
            not ledger["MaximumViolated"] and all(value == 0 for value in ledger["Reserved"].values()), "Unsettled or inconsistent ledger.")
    require(ledger["Admitted"] == ledger["Settled"] == len(ledger["Receipts"]) and ledger["DroppedReceipts"] == 0, "Receipt audit is incomplete.")
    charges = {}
    operation_ids = set()
    for receipt in ledger["Receipts"]:
        require(receipt["OperationId"] not in operation_ids and receipt["Outcome"] == 0 and not receipt["ExceededMaximum"], "Invalid/duplicate resource receipt.")
        operation_ids.add(receipt["OperationId"])
        for key, value in receipt["Charged"]["Amounts"].items():
            charges[key] = charges.get(key, 0) + value
    require(all(charges.get(key, 0) == value for key, value in ledger["Spent"].items()), "Receipt charges differ from totals.")
    completed = [entry for entry in row["Trace"] if entry["Status"] == 0]
    require(row["BestObservedQuality"] == max(entry["Quality"] for entry in completed) and
            any(entry["Genome"] == row["BestGenome"] and entry["Quality"] == row["BestObservedQuality"] for entry in completed), "Reported winner differs from measured trace.")
    require(len(completed) == budget and row["Counters"]["EvaluationAttempts"] == budget and len(row["Decisions"]) == budget, "Missing logical evaluations.")
    require(len(row["Trace"]) == row["Counters"]["Proposals"] <= (16 if phase == "prior" else 256), "Proposal trace or cap mismatch.")
    require([entry["Genome"] for entry in row["Trace"][:len(row["InitialGenomes"])]] == row["InitialGenomes"], "Initial candidates differ from declared prior information.")
    require(ledger["Spent"].get("proposal_calls", 0) == len(row["Trace"]) - len(row["InitialGenomes"]) and
            ledger["Spent"].get("validation_calls", 0) == budget, "Proposal/validation work mismatch.")
    require(sum(entry["CostUnits"] for entry in row["Trace"]) == row["CurrentPhysicalObservations"], "Trace cost omits work.")
    require(all(entry["Origin"] is None and entry["CostUnits"] == 0 for entry in row["Trace"] if entry["Status"] != 0), "Unexpected failed measurement.")
    decisions = {item["EvaluationId"]: item for item in row["Decisions"]}
    require(len(decisions) == budget, "Duplicate reuse decision.")
    hits, fresh = 0, 0
    for entry in completed:
        decision = decisions[entry["EvaluationId"]]
        require(decision["Origin"] == entry["Origin"], "Decision/trace provenance differs.")
        origin, raw = raw_observation(group, decision["EvidenceSha256"], entry, seen_samples, seen_artifacts)
        require(origin["ScopeKey"] == row["ScopeKey"], "Candidate applicability changed.")
        if origin["Kind"] == 1:
            hits += 1
            require(decision["Decision"] == 0 and entry["CostUnits"] == 0 and method == "ExistingSamples" and phase in ("prior", "cold", "warm", "expired"), "Reuse hidden as fresh work.")
            observed = datetime.fromisoformat(origin["ObservedAt"])
            now = datetime.fromisoformat(plan["Clock"]).timestamp() + (121 if phase == "expired" else 0 if phase == "prior" else 1) * 60
            require(0 <= now - observed.timestamp() <= 3600, "Expired/future observation was reused.")
            require(origin["SourceRunId"].startswith(row["RunId"].rsplit("/", 2)[0] + "/") or phase == "prior", "Reuse crossed a task/seed/execution boundary.")
        else:
            fresh += 1
            require(entry["CostUnits"] == 5 and origin["SourceRunId"] == row["RunId"] and origin["SourceEvaluationId"] == str(entry["EvaluationId"]), "Fresh sample attribution mismatch.")
            require(raw["RootSeed"] == row["Seed"] + plan["PhaseSeedOffsets"][phase], "Fresh measurement reused another phase's random stream.")
            expected_time = datetime.fromisoformat(plan["Clock"]).timestamp() + (121 if phase == "expired" else 0 if phase == "prior" else 1) * 60
            require(datetime.fromisoformat(origin["ObservedAt"]).timestamp() == expected_time and
                    origin["SampleIds"] == [row["RunId"] + f"/{entry['EvaluationId']}/{index}" for index in range(5)], "Fresh observation clock or sample identity differs from acquisition.")
    require((hits, fresh, row["OriginalSampleReferences"], row["RawReads"], row["RawWrites"]) ==
            (row["CacheHits"], row["FreshAggregates"], hits * 5, hits, fresh), "Raw acquisition/reuse counts differ.")
    store_calls = 0 if method == "AlwaysFresh" else fresh + (0 if phase == "force-fresh" else budget)
    require(ledger["Spent"].get("cache_store_invocations", 0) == store_calls, "Persistent lookup/publication work omitted.")
    if phase in ("prior", "cold"):
        require(row["PriorCostAttributed"] == 0 and row["CopiedPriorRecords"] == 0 and row["PriorCacheFiles"] == {} and row["RepertoireSha256"] is None, "Cold/prior hidden knowledge.")
    else:
        require(row["PriorCostAttributed"] == 80 and row["CopiedPriorRecords"] == 16 and row["PriorCacheFiles"] == prior_files and
                row["RepertoireSha256"] == repertoire_digest and row["InitialGenomes"] == imported, "Unequal or unaccounted prior information.")
    if method == "AlwaysFresh" or phase == "force-fresh":
        require(hits == 0, "Fresh measurement was bypassed.")
    confirmation_mean = None
    if phase == "prior":
        require(row["Confirmation"] is None, "Hidden prior confirmation work.")
    else:
        confirmation = row["Confirmation"]
        receipt = confirmation["Resources"]
        require(confirmation["PhysicalObservations"] == receipt["Spent"]["cost_units"] == 25 and receipt["Unknown"] == 0 and
                all(value == 0 for value in receipt["Reserved"].values()) and receipt["Admitted"] == receipt["Settled"] == 5 and
                len(receipt["Receipts"]) == 5 and all(item["Charged"]["Amounts"]["cost_units"] == 5 and item["Outcome"] == 0 for item in receipt["Receipts"]), "Confirmation work was omitted or unsettled.")
        require(len(confirmation["Rows"]) == 5, "Incomplete confirmation samples.")
        values = []
        for index, entry in enumerate(confirmation["Rows"]):
            origin, raw = raw_observation(group, entry["EvidenceSha256"], entry, seen_samples, seen_artifacts)
            require(entry["EvaluationId"] == index and entry["Genome"] == row["BestGenome"] and entry["Status"] == 0 and entry["CostUnits"] == 5 and
                    origin["Kind"] == 0 and origin["SourceRunId"] == row["RunId"] + "/confirmation" and origin["SourceEvaluationId"] == str(index), "Confirmation reused search evidence or another winner.")
            require(origin["SampleIds"] == [row["RunId"] + f"/confirmation/{index}/{sample}" for sample in range(5)] and
                    origin["ScopeKey"] == row["ScopeKey"] and raw["RootSeed"] == row["Seed"] + plan["PhaseSeedOffsets"][phase] + 1000000 and
                    raw["SeedStream"] == 990001 + index, "Confirmation was not separately acquired on its declared stream.")
            expected_time = datetime.fromisoformat(plan["Clock"]).timestamp() + (121 if phase == "expired" else 1) * 60 + 1
            require(datetime.fromisoformat(origin["ObservedAt"]).timestamp() == expected_time, "Confirmation clock mismatch.")
            values.extend(raw["Values"])
        confirmation_mean = statistics.mean(values)
        confirmation_error = math.sqrt(sum((value - confirmation_mean) ** 2 for value in values) / 600)
        require(math.isclose(confirmation["Mean"], confirmation_mean, rel_tol=1e-12, abs_tol=1e-12) and
                math.isclose(confirmation["StandardError"], confirmation_error, rel_tol=1e-12, abs_tol=1e-12), "Confirmation aggregate does not reproduce from raw samples.")
    # Keep a compact replay comparison; physical sample IDs/state hashes are deliberately distinct.
    return {key: row[key] for key in ("CurrentPhysicalObservations", "FreshAggregates", "CacheHits", "BestGenome", "BestObservedQuality", "InitialGenomes", "Counters")} | {
        "ConfirmationMean": confirmation_mean,
        "Trace": [(item["EvaluationId"], item["Genome"], item["Status"], item["Quality"], item["CostUnits"],
                   text_json(item["Origin"])["StandardError"] if item["Origin"] else None) for item in row["Trace"]]}


def analyze(root):
    plan, plan_digest = load_json(root / "plan.json", 128 * 1024)
    validate_plan(plan)
    seen_samples, seen_artifacts, cases, totals, prior_totals = {}, set(), {}, {}, {}
    for execution in plan["Executions"]:
        totals[execution] = prior_totals[execution] = 0
        for task in plan["Tasks"]:
            for seed in range(plan["SeedCount"]):
                group = root / execution / task / str(seed)
                prior, prior_digest = load_json(group / "prior.json", 4 * 1024 * 1024)
                prior_files = {path.name: digest(path.read_bytes()) for path in sorted((group / "prior-cache").glob("*.json"))}
                require(len(prior_files) == 16, "Prior cache is incomplete.")
                repertoire, repertoire_digest = load_json(group / "repertoire.json", 8 * 1024 * 1024)
                require(repertoire["SchemaVersion"] == 1 and digest(repertoire["Payload"].encode()) == repertoire["Checksum"], "Repertoire integrity mismatch.")
                content = text_json(repertoire["Payload"])
                provenance = content["Provenance"]
                require(provenance["EvidenceSha256"] == prior_digest and provenance["SourceStateHash"] == prior["StateHash"] and provenance["PriorCostUnits"] == 80, "Prior cost or source evidence missing.")
                imported = []
                for entry in content["Entries"]:
                    payload = base64.b64decode(entry["PayloadBase64"], validate=True)
                    require(digest(payload) == entry["PayloadSha256"], "Seed payload changed.")
                    imported.append(int(payload))
                require(len(imported) == len(set(imported)) == 8 and all(0 <= value <= 65535 for value in imported), "Invalid imported seeds.")
                cases[(execution, task, seed, "prior", "ExistingSamples")] = validate_run(group, prior, plan, {}, None, [], seen_samples, seen_artifacts)
                prior_totals[execution] += prior["CurrentPhysicalObservations"]
                for phase in plan["Phases"]:
                    pair = []
                    for method in plan["Methods"]:
                        row, _ = load_json(group / f"{phase}-{method}.json", 4 * 1024 * 1024)
                        require((row["RunId"], row["Objective"], row["Seed"], row["Phase"], row["Method"]) ==
                                (f"{execution}/{task}/{seed}/{phase}/{method}", task, seed, phase, method), "Case identity mismatch.")
                        cases[(execution, task, seed, phase, method)] = validate_run(group, row, plan, prior_files, repertoire_digest, imported, seen_samples, seen_artifacts)
                        totals[execution] += row["CurrentPhysicalObservations"]
                        pair.append(row)
                    require(pair[0]["InitialGenomes"] == pair[1]["InitialGenomes"] and pair[0]["PriorCacheFiles"] == pair[1]["PriorCacheFiles"], "Unpaired prior/initial information.")
                require({path.stem for path in (group / "raw").glob("*.json")} == {value for location, value in seen_artifacts if location == group}, "Unreported or missing raw acquisitions.")
    replay_equal = all(value == cases[("replay", *key[1:])] for key, value in cases.items() if key[0] == "primary")
    seed_effects = [statistics.mean(cases[("primary", task, seed, "warm", "AlwaysFresh")]["CurrentPhysicalObservations"] -
                                   cases[("primary", task, seed, "warm", "ExistingSamples")]["CurrentPhysicalObservations"] for task in plan["Tasks"])
                   for seed in range(plan["SeedCount"])]
    confirmation_totals = {execution: plan["SeedCount"] * 2 * 4 * 2 * 25 for execution in plan["Executions"]}
    return {"PlanSha256": plan_digest, "SourceRevision": plan["SourceRevision"], "SeedCount": plan["SeedCount"], "RetainedRunsIncludingPrior": len(cases),
            "SearchPhysicalObservationsByExecution": totals, "PriorPhysicalObservationsByExecution": prior_totals,
            "ConfirmationPhysicalObservationsByExecution": confirmation_totals,
            "TotalPhysicalObservations": sum(totals.values()) + sum(prior_totals.values()) + sum(confirmation_totals.values()), "DistinctPhysicalSampleIds": len(seen_samples),
            "ReplayEqualExcludingAcquisitionIdentity": replay_equal, "PrimarySavedObservations": statistics.mean(seed_effects),
            "PrimaryPairedSeedBootstrap95": interval([seed_effects], plan["BootstrapSamples"], plan["BootstrapSeed"], 0.05, False),
            "SeedEffects": seed_effects, "Inference": "Saved observation work in a fixed authored paired no-reuse ablation. No independent-sample multiplication, quality superiority, representative task-population claim or physical timing-noise claim. Smoke intervals are not inferential."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    summary = analyze(args.directory)
    require(summary["TotalPhysicalObservations"] == summary["DistinctPhysicalSampleIds"], "Physical samples duplicated or missing.")
    require(summary["ReplayEqualExcludingAcquisitionIdentity"], "Quality/accounting replay mismatch.")
    if args.output:
        with args.output.open("x", encoding="utf-8") as output:
            json.dump(summary, output, indent=2, allow_nan=False)
    print(json.dumps({key: value for key, value in summary.items() if key != "SeedEffects"}, indent=2))
