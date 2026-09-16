"""Conservative fixed-sample release claims. Integrity checks are not evidence authenticity."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import statistics
import subprocess


def require(condition, message):
    if not condition:
        raise ValueError(message)


def number(value, positive=False):
    require(type(value) in (int, float) and math.isfinite(value), "Finite numeric measurement required")
    require(value > 0 if positive else value >= 0, "Invalid measurement sign")
    return value


def text(value):
    require(isinstance(value, str) and 0 < len(value.strip()) <= 4096, "Named evidence field required")
    return value


def digest(data):
    return hashlib.sha256(data).hexdigest()


def load(path):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate JSON key")
            result[key] = value
        return result
    require(path.stat().st_size <= 32 * 1024 * 1024, "Evidence file exceeds bound")
    raw = path.read_bytes()
    return json.loads(raw, object_pairs_hook=pairs, parse_constant=lambda _: (_ for _ in ()).throw(ValueError("Nonfinite JSON")))


def confined(root, relative):
    text(relative)
    require(not Path(relative).is_absolute() and "\\" not in relative and ":" not in relative, "Relative portable evidence path required")
    target = (root / relative).resolve(strict=True)
    require(target.is_relative_to(root.resolve()) and target.is_file(), "Evidence escapes bundle")
    return target


def validate_protocol(p):
    require(p["schema"] == "evolution-release-protocol-v1", "Unknown protocol")
    require(p["endpoint"] in ("runtime", "target-cost"), "One primary endpoint must be frozen")
    for name in ("benchmark", "model", "model_snapshot", "hardware", "cost_metric", "correctness_oracle", "registration_review", "pilot_sha256"):
        text(p[name])
    for name in ("candidate_revision", "competitor_revision"):
        require(isinstance(p[name], str) and len(p[name]) == 40 and all(c in "0123456789abcdef" for c in p[name]), "Exact implementation revision required")
    require(len(p["pilot_sha256"]) == 64 and all(c in "0123456789abcdef" for c in p["pilot_sha256"]), "Pilot digest required")
    require(p["competitor"] == "OpenEvolve", "This gate is specifically the named OpenEvolve comparison")
    require(p["calibrated_on_pilot"] is True, "Provisional thresholds must be calibrated before freezing")
    require(type(p["seeds"]) is list and 2 <= len(p["seeds"]) <= 10000 and
            all(type(s) is int and 0 <= s < 2**32 for s in p["seeds"]) and len(set(p["seeds"])) == len(p["seeds"]), "Invalid fixed seed set")
    require(type(p["tasks"]) is list and 1 <= len(p["tasks"]) <= 100, "Task set required")
    ids = [text(t["id"]) for t in p["tasks"]]
    require(len(ids) == len(set(ids)), "Duplicate tasks")
    for task in p["tasks"]:
        text(task["family"])
        require(isinstance(task["definition_sha256"], str) and len(task["definition_sha256"]) == 64
                and all(c in "0123456789abcdef" for c in task["definition_sha256"]), "Task definition digest required")
        require(0 <= number(task["maximum_regression"]) <= .1, "Regression allowance must be predeclared and no more than 10%")
    require(p["alpha"] == .05, "95% family-wise intervals required")
    require(p["minimum_ratio"] == (1.10 if p["endpoint"] == "runtime" else 1.25), "Primary practical effect changed")
    require(p["failure_rule"] == "retain-fallback-infrastructure-blocks", "Unknown failure scoring rule")
    lower, upper = p["log_ratio_bounds"]
    require(type(lower) in (int, float) and type(upper) in (int, float) and math.isfinite(lower) and math.isfinite(upper)
            and -20 <= lower < 0 < upper <= 20, "Predeclared finite log-ratio support required")
    number(p["search_cost_cap"], True)
    if p["endpoint"] == "runtime":
        require(p["runtime_statistic"] == "median" and type(p["runtime_samples"]) is int and 3 <= p["runtime_samples"] <= 1000,
                "Frozen runtime measurement protocol required")
    if p["endpoint"] == "target-cost":
        text(p["target_definition"])


def interval(values, p, alpha):
    lower, upper = p["log_ratio_bounds"]
    require(all(lower <= x <= upper for x in values), "Observed ratio outside frozen support; do not clip or refit")
    mean = statistics.mean(values)
    radius = (upper-lower) * math.sqrt(math.log(2/alpha)/(2*len(values)))
    return {"ratio": math.exp(mean), "lower": math.exp(mean-radius), "upper": math.exp(mean+radius), "pairs": len(values)}


def assess(protocol, observations):
    validate_protocol(protocol)
    expected = {(t["id"], seed) for t in protocol["tasks"] for seed in protocol["seeds"]}
    require(type(observations) is list and len(observations) == len(expected), "Incomplete fixed campaign")
    keys = [(row["task"], row["seed"]) for row in observations]
    require(len(set(keys)) == len(keys) and set(keys) == expected, "Missing, extra or duplicate pairs")
    logs, blocked = {}, []
    for row in observations:
        task, seed = row["task"], row["seed"]
        for name in ("candidate", "competitor"):
            side = row[name]
            require(side["status"] in ("winner", "fallback", "infrastructure-failure", "target-not-reached"), "Unknown outcome")
            number(side["search_cost"])
            require(side["search_cost"] <= protocol["search_cost_cap"], "Search exceeded declared total cost")
            for field in ("program_sha256", "receipt", "correctness_receipt"):
                text(side[field])
            require(len(side["program_sha256"]) == 64 and all(c in "0123456789abcdef" for c in side["program_sha256"]), "Program hash invalid")
            require(type(side["correct"]) is bool, "Explicit correctness verdict required")
            if side["status"] in ("winner", "fallback"):
                require(side["correct"], "Promoted/fallback program lacks independent correctness")
                if side["status"] == "fallback":
                    require(side["program_sha256"] == row["initial_program_sha256"], "Failed search changed fallback")
            else:
                blocked.append({"task": task, "seed": seed, "side": name, "status": side["status"]})
        if row["candidate"]["status"] not in ("winner", "fallback") or row["competitor"]["status"] not in ("winner", "fallback"):
            continue
        c, b = row["candidate"], row["competitor"]
        if protocol["endpoint"] == "runtime":
            require(c["search_cost"] == b["search_cost"], "Runtime claims require equal reconciled total search cost")
            ratio = number(b["runtime_ms"], True) / number(c["runtime_ms"], True)
        else:
            require(c["target_reached"] is True and b["target_reached"] is True, "Cost endpoint requires the same frozen target")
            ratio = number(b["search_cost"], True) / number(c["search_cost"], True)
        logs[task, seed] = math.log(ratio)
    if blocked:
        return {"eligible": False, "status": "incomplete-evidence", "blocked": blocked,
                "observations_retained": len(observations), "families": [], "claim": "none"}
    # One aggregate observation per seed preserves arbitrary within-seed task correlation.
    # Fixed task set, equally weighted tasks; uncertainty is across independent paired seeds.
    families = sorted({t["family"] for t in protocol["tasks"]})
    alpha = protocol["alpha"] / (1 + len(protocol["tasks"]) + len(families))
    task_results = {t["id"]: interval([logs[t["id"], s] for s in protocol["seeds"]], protocol, alpha) for t in protocol["tasks"]}
    overall = interval([statistics.mean(logs[t["id"], s] for t in protocol["tasks"]) for s in protocol["seeds"]], protocol, alpha)

    def passes(summary, tasks):
        return (summary["ratio"] >= protocol["minimum_ratio"] and summary["lower"] > 1 and
                all(task_results[t["id"]]["lower"] >= 1-t["maximum_regression"] for t in tasks))
    family_results = []
    for family in families:
        tasks = [t for t in protocol["tasks"] if t["family"] == family]
        summary = interval([statistics.mean(logs[t["id"], s] for t in tasks) for s in protocol["seeds"]], protocol, alpha)
        family_results.append({"family": family, **summary, "eligible": passes(summary, tasks)})
    eligible = passes(overall, protocol["tasks"])
    return {"eligible": eligible, "status": "supported" if eligible else "inconclusive-or-regressing",
            "overall": overall, "tasks": task_results, "families": family_results, "alpha_per_interval": alpha,
            "observations_retained": len(observations), "claim": "declared-task-set" if eligible else "none"}


def verify_bundle(root, expected_protocol_hash):
    manifest = load(root / "manifest.json")
    require(manifest["schema"] == "evolution-release-bundle-v1", "Unknown evidence bundle")
    artifacts = manifest["artifacts"]
    require(type(artifacts) is dict and 3 <= len(artifacts) <= 10000, "Complete artifact inventory required")
    total_bytes = 0
    for name, expected in artifacts.items():
        artifact = confined(root, name)
        total_bytes += artifact.stat().st_size
        require(artifact.stat().st_size <= 32 * 1024 * 1024 and total_bytes <= 256 * 1024 * 1024, "Bundle exceeds inspection bounds")
        require(digest(artifact.read_bytes()) == expected, "Artifact digest mismatch: " + name)
    require("protocol.json" in artifacts and "observations.json" in artifacts, "Protocol and raw paired observations required")
    require(artifacts["protocol.json"] == expected_protocol_hash, "Frozen registration hash mismatch")
    protocol = load(root / "protocol.json")
    require("pilot.json" in artifacts and artifacts["pilot.json"] == protocol["pilot_sha256"], "Calibrating pilot missing")
    rows = load(root / "observations.json")
    receipts_used = set()
    for row in rows:
        require(row["initial_program"] in artifacts and artifacts[row["initial_program"]] == row["initial_program_sha256"], "Original fallback source missing")
        for system in ("candidate", "competitor"):
            side = row[system]
            require(side["program"] in artifacts and artifacts[side["program"]] == side["program_sha256"], "Resulting program source missing")
            require(side["receipt"] in artifacts and side["correctness_receipt"] in artifacts, "Raw work/correctness receipts missing")
            require(side["receipt"] not in receipts_used, "Work receipt reused across independent runs")
            receipts_used.add(side["receipt"])
            receipt = load(confined(root, side["receipt"]))
            oracle = load(confined(root, side["correctness_receipt"]))
            require((receipt["task"], receipt["seed"], receipt["system"]) == (row["task"], row["seed"], system), "Receipt run identity mismatch")
            components = receipt["cost_components"]
            require(type(components) is dict and components and sum(number(v) for v in components.values()) == side["search_cost"], "Total search cost is not itemized/reconciled")
            require(receipt["program_sha256"] == side["program_sha256"] and receipt["search_cost"] == side["search_cost"]
                    and receipt["cost_metric"] == protocol["cost_metric"], "Work receipt does not reconcile")
            require(oracle["program_sha256"] == side["program_sha256"] and oracle["correct"] == side["correct"]
                    and oracle["oracle"] == protocol["correctness_oracle"] and oracle["phase"] == "confirmation", "Independent correctness receipt does not reconcile")
            if side["status"] in ("winner", "fallback"):
                if protocol["endpoint"] == "runtime":
                    samples = receipt["runtime_samples_ms"]
                    require(receipt["measurement_phase"] == "confirmation" and len(samples) == protocol["runtime_samples"]
                            and statistics.median([number(v, True) for v in samples]) == side["runtime_ms"], "Fresh runtime observations do not reconcile")
                else:
                    require(receipt["target_definition"] == protocol["target_definition"] and receipt["target_reached"] is True,
                            "Target attainment receipt missing")
    return assess(protocol, rows)


def release(root):
    root = root.resolve()
    claim = load(root / "claim.json")
    require(claim["schema"] == "evolution-release-claim-v1", "Unknown release claim")
    if claim["claim"] == "none":
        text(claim["reason"])
        require(set(claim) == {"schema", "claim", "reason"}, "No hidden evidence/claims on engineering-only releases")
        return {"eligible": True, "claim": "none", "reason": claim["reason"]}
    require(claim["claim"] == "declared-task-set", "Only a supported frozen task-set claim is allowed")
    revision = claim["registration_commit"]
    require(isinstance(revision, str) and len(revision) == 40 and all(c in "0123456789abcdef" for c in revision), "Exact prior registration commit required")
    registered_path = text(claim["registered_protocol_path"])
    require(registered_path.startswith("release/registrations/") and registered_path.endswith(".json")
            and ".." not in registered_path and "\\" not in registered_path and ":" not in registered_path, "Registration must be committed under release/registrations")
    repository = root.parent
    head = subprocess.check_output(["git", "-C", str(repository), "rev-parse", "HEAD"], text=True).strip()
    require(revision != head, "Protocol and result cannot first appear in the same commit")
    require(subprocess.run(["git", "-C", str(repository), "merge-base", "--is-ancestor", revision, head], capture_output=True).returncode == 0,
            "Registration must precede the release revision")
    registered = subprocess.check_output(["git", "-C", str(repository), "show", revision + ":" + registered_path])
    require(digest(registered) == claim["protocol_sha256"], "Claim does not match the prior committed protocol")
    # Bundle is a directory; resolving its manifest enforces containment, including symlinks.
    bundle = confined(root, text(claim["bundle"]) + "/manifest.json").parent
    return verify_bundle(bundle, text(claim["protocol_sha256"]))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    with args.output.open("x", encoding="utf-8", newline="\n") as output:
        try:
            report = release(args.release)
        except (ValueError, KeyError, TypeError, OSError, OverflowError, subprocess.CalledProcessError) as error:
            report = {"eligible": False, "claim": "none", "status": "invalid-evidence", "error": str(error)}
        json.dump(report, output, indent=2, allow_nan=False)
    raise SystemExit(0 if report["eligible"] else 1)
