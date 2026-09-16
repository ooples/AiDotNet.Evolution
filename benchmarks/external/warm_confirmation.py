"""Fixed, interleaved final timing confirmation; never feeds the search controller.

The paired-log Student interval assumes independent, approximately normal log
ratios. It is not a distribution-free guarantee. Correctness remains a separate gate.
"""
import math
import random
import statistics

from program_controls import candidate_hash
from warm_study_design import digest

SCHEMA = "paired-log-confirmation-v1"


def policy(tracks, pairs=16):
    if type(tracks) is not int or not 1 <= tracks <= 1_000_000 or type(pairs) is not int or not 3 <= pairs <= 64:
        raise ValueError("Declare bounded challenge count and fixed paired sample count")
    return dict(schema=SCHEMA, pairs=pairs, alpha=.05 / (3 * tracks), minimum_relative_gain=0.0,
                family="three registered partitions; all planned tracks per partition")


def validate_policy(value):
    if (value.get("schema") != SCHEMA or type(value.get("pairs")) is not int or not 3 <= value["pairs"] <= 64
            or type(value.get("alpha")) not in (int, float) or not math.isfinite(value["alpha"]) or not 0 < value["alpha"] <= .05
            or type(value.get("minimum_relative_gain")) not in (int, float)
            or not math.isfinite(value["minimum_relative_gain"]) or not 0 <= value["minimum_relative_gain"] < 1):
        raise ValueError("Invalid frozen timing confirmation policy")


def assess(values, frozen):
    validate_policy(frozen)
    original, selected = values["original"], values["selected"]
    if any(row["status"] != "valid" for row in (original, selected)):
        return dict(confirmed=False, reason="invalid-confirmation", upper_log_ratio=None)
    samples = [original["samples"], selected["samples"]]
    ids = original["sample_ids"] + selected["sample_ids"]
    if (any(len(s) != frozen["pairs"] for s in samples) or len(ids) != 2 * frozen["pairs"]
            or len(ids) != len(set(ids)) or any(not isinstance(i, str) or not i for i in ids)
            or any(type(v) not in (int, float) or not math.isfinite(v) or v <= 0 for s in samples for v in s)):
        raise ValueError("Require distinct, finite, positive fresh timing pairs")
    logs = [math.log(b) - math.log(a) for a, b in zip(*samples)]
    deviation = statistics.stdev(logs)
    if deviation == 0:
        # Identical rounded observations cannot establish a precision floor.
        return dict(confirmed=False, reason="unresolved-timing-resolution", upper_log_ratio=None)
    from scipy.stats import t
    upper = statistics.mean(logs) + float(t.ppf(1-frozen["alpha"], len(logs)-1)) * deviation / math.sqrt(len(logs))
    confirmed = math.isfinite(upper) and upper < math.log1p(-frozen["minimum_relative_gain"])
    return dict(confirmed=confirmed, reason="confirmed-improvement" if confirmed else "not-confirmed",
                upper_log_ratio=upper if math.isfinite(upper) else None)


def manifest(evaluator, frozen):
    return dict(single_sample_evaluator=evaluator, noise_policy=frozen)


def confirm(initial, selected, evaluator, frozen, *, seed, owner):
    validate_policy(frozen)
    if evaluator.samples != 1 or evaluator.phase != "confirmation":
        raise ValueError("Interleaving requires a single-sample confirmation evaluator")
    randomizer = random.Random(seed)
    receipts = {"original": [], "selected": []}
    orders = []
    for _ in range(frozen["pairs"]):
        order = ["original", "selected"]
        randomizer.shuffle(order)
        orders.append(order)
        for role in order:
            code = initial if role == "original" else selected
            receipt = evaluator(code, owner=owner)
            if (receipt["candidate_hash"] != candidate_hash(code) or receipt["unknown_work"] is not False
                    or receipt["phase"] != "confirmation" or len(receipt["sample_ids"]) != 1
                    or receipt["input_sha256"] != evaluator.manifest["input_sha256"]
                    or receipt["evaluator_sha256"] != digest(evaluator.manifest)):
                raise ValueError("Unbound single-sample confirmation")
            receipts[role].append(receipt)
    identity = digest(manifest(evaluator.manifest, frozen))
    values = {}
    for role, rows in receipts.items():
        times = [row["duration_seconds"] for row in rows]
        valid = all(row["status"] == "valid" for row in rows)
        if valid and any(type(v) not in (int,float) or not math.isfinite(v) or v <= 0 for v in times):
            raise ValueError("Invalid positive timing measurement")
        duration = statistics.median(times) if valid else None
        values[role] = dict(rows[0], status="valid" if valid else "invalid", duration_seconds=duration,
                            quality=1/duration if valid else -1e300, samples=times,
                            work_units=sum(row["work_units"] for row in rows), evaluator_sha256=identity,
                            throughput_cases_per_second=len(evaluator.cases)/duration if valid else None)
        for key in ("sample_ids", "budget_operation_ids", "resources", "startup_samples"):
            values[role][key] = [item for row in rows for item in row[key]]
    return values, dict(policy=frozen, orders=orders, evaluator_manifest=manifest(evaluator.manifest,frozen),
                        evaluator_sha256=identity, **assess(values, frozen))


def validate_report(report):
    plan = report["plan"]
    frozen = plan["noise_policy"]
    if frozen != policy(sum(len(row["tracks"]) for row in plan["grid"]), frozen["pairs"]):
        raise ValueError("Changed registered confirmation family")
    all_ids = []
    for cell in report["rows"]:
        for track in cell["search_runs"]:
            for receipt in track["receipts"]:
                if receipt["operation"] == "evaluate" and receipt["status"] == "completed":
                    all_ids.extend(receipt["result"]["sample_ids"])
        for index, pair in enumerate(cell["pairs"]):
            evidence = pair["noise_confirmation"]
            values = {role:pair[role] for role in ("original", "selected")}
            actual = assess(values, frozen)
            if (type(evidence["confirmed"]) is not bool or type(pair["fallback"]) is not bool
                    or evidence["policy"] != frozen or any(evidence[k] != v for k,v in actual.items())):
                raise ValueError("Unreproducible timing confirmation")
            binding = evidence["evaluator_manifest"]
            if (binding["noise_policy"] != frozen or digest(binding) != evidence["evaluator_sha256"]
                    or any(v["input_sha256"] != binding["single_sample_evaluator"]["input_sha256"] for v in values.values())):
                raise ValueError("Changed confirmation evaluator identity")
            randomizer = random.Random(cell["seed"]*31+index)
            orders = []
            for _ in range(frozen["pairs"]):
                order = ["original", "selected"]
                randomizer.shuffle(order)
                orders.append(order)
            if evidence["orders"] != orders or pair["order"] != orders:
                raise ValueError("Timing order differs from registered randomization")
            for receipt in values.values():
                if (receipt["evaluator_sha256"] != evidence["evaluator_sha256"]
                        or len(receipt["samples"]) != frozen["pairs"]
                        or len(receipt["budget_operation_ids"]) != frozen["pairs"]):
                    raise ValueError("Incomplete or unbound timing batch")
                if receipt["status"] == "valid" and receipt["duration_seconds"] != statistics.median(receipt["samples"]):
                    raise ValueError("Reported latency differs from fresh observations")
            for sample, order in enumerate(orders):
                if values[order[0]]["budget_operation_ids"][sample] >= values[order[1]]["budget_operation_ids"][sample]:
                    raise ValueError("Journal dispatch order differs from timing pairs")
            if not actual["confirmed"] and not pair["fallback"]:
                raise ValueError("Unconfirmed candidate cannot be deployed")
            if (values["original"]["status"] != "valid" or any(r["status"] != "valid" for r in pair["original_audits"])
                    or (not pair["fallback"] and any(r["status"] != "valid" for r in pair["audits"]))):
                raise ValueError("Deployment has invalid correctness evidence")
            deployed = values["original" if pair["fallback"] else "selected"]
            expected_speedup = 1.0 if pair["fallback"] else values["original"]["duration_seconds"]/values["selected"]["duration_seconds"]
            if (pair["speedup"] != expected_speedup or pair["deployed_seconds"] != deployed["duration_seconds"]
                    or pair["deployed_hash"] != deployed["candidate_hash"] or pair["deployed"] != deployed):
                raise ValueError("Deployment differs from confirmed/fallback evidence")
            for receipt in [*values.values(), *pair["audits"], *pair["original_audits"]]:
                all_ids.extend(receipt["sample_ids"])
    if len(all_ids) != len(set(all_ids)):
        raise ValueError("Search, confirmation or audit samples were reused")
