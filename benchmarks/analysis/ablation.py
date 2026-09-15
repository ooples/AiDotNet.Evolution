"""Frozen, one-use ablation campaigns; Python standard library only."""
import argparse
import hashlib
import itertools
import json
import math
from pathlib import Path
import statistics
import subprocess
import sys

PROTOCOL = "feature-ablation-v1"
FAMILIES = ("numeric", "program", "kernel")
FLAGS = ("Islands", "Migration", "Novelty", "Calibration", "Growth", "Continuous")


def matrix():
    def config(name, selection="Uniform", **flags):
        return dict(Name=name, Selection=selection, **{f: flags.get(f, False) for f in FLAGS})
    rows = [config("baseline"), config("ratio", "Ratio"), config("curiosity", "Curiosity"), config("double", "Double")]
    for name, flags in (("islands", ("Islands",)), ("islands-migration", ("Islands", "Migration")),
                        ("novelty", ("Novelty",)), ("calibration", ("Calibration",)), ("growth", ("Growth",)),
                        ("calibration-growth", ("Calibration", "Growth")), ("continuous", ("Continuous",)),
                        ("islands-continuous", ("Islands", "Continuous"))):
        rows.append(config(name, **dict.fromkeys(flags, True)))
    rows.append(config("all", **dict.fromkeys(FLAGS, True)))
    for flag in FLAGS:
        enabled = dict.fromkeys(FLAGS, True)
        enabled[flag] = False
        if flag == "Islands":
            enabled["Migration"] = False
        rows.append(config("all-no-" + flag.lower(), **enabled))
    return rows


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def load(path):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON key")
            result[key] = value
        return result
    return json.loads(Path(path).read_text(encoding="utf-8"), object_pairs_hook=pairs,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))


def write_new(path, value):
    with Path(path).open("x", encoding="utf-8", newline="\n") as output:
        json.dump(value, output, indent=2, allow_nan=False)


def validate_plan(plan):
    count = len(plan["DevelopmentSeeds"])
    if (plan["Protocol"] != PROTOCOL or plan["Alpha"] != 0.05 or plan["MinimumGain"] != 0.02 or
            plan["QualityWeight"] != 0.75 or plan["Configurations"] != matrix() or not 2 <= count <= 100 or
            plan["DevelopmentSeeds"] != list(range(17001, 17001 + count)) or
            plan["ConfirmationSeeds"] != list(range(910001, 910001 + count)) or
            not 16 <= plan["Budget"] <= 1024 or count * plan["Budget"] * 19 * 3 > 2_000_000):
        raise ValueError("Changed predeclared analysis criteria")


def register(binary, output, seeds=32, budget=64):
    if not 2 <= seeds <= 100 or not 16 <= budget <= 1024 or seeds * budget * 19 * 3 > 2_000_000:
        raise ValueError("Registration exceeds fixed bounds")
    binary = Path(binary).resolve()
    plan = dict(Protocol=PROTOCOL, Binary=str(binary), BenchmarkHash=digest(binary),
                CoreHash=digest(binary.parent / "AiDotNet.Evolution.dll"),
                Alpha=0.05, MinimumGain=0.02, QualityWeight=0.75, Budget=budget,
                DevelopmentSeeds=list(range(17001, 17001 + seeds)),
                ConfirmationSeeds=list(range(910001, 910001 + seeds)),
                Configurations=matrix(), Endpoint="0.75*quality+0.25*fixed-reference-diversity; failures=0",
                ConfirmationRule="One development nominee per family; simultaneous one-sided Hoeffding lower gain > 0.02; no retuning")
    write_new(output, plan)
    return plan


def verify(plan, result, partition, configs, request_hash=None):
    validate_plan(plan)
    request = dict(Protocol=PROTOCOL, Partition=partition, Budget=plan["Budget"],
                   Seeds=plan[partition.title() + "Seeds"], Configurations=configs)
    if result["Request"] != request or result["BenchmarkHash"] != plan["BenchmarkHash"] or result["CoreHash"] != plan["CoreHash"]:
        raise ValueError("Request or executable mismatch")
    expected_hash = request_hash or hashlib.sha256(json.dumps(request, indent=2, allow_nan=False).encode()).hexdigest()
    if result["RequestHash"] != expected_hash:
        raise ValueError("Request hash mismatch")
    expected = set(itertools.product(FAMILIES, request["Seeds"], [c["Name"] for c in configs]))
    actual = [(r["Family"], r["Seed"], r["Configuration"]) for r in result["Rows"]]
    if len(actual) != len(set(actual)) or set(actual) != expected:
        raise ValueError("Missing, duplicate or extra experiment rows")
    for family, seed in itertools.product(FAMILIES, request["Seeds"]):
        if len({r["InitialHash"] for r in result["Rows"] if r["Family"] == family and r["Seed"] == seed}) != 1:
            raise ValueError("Unpaired initial population")
    for row in result["Rows"]:
        tasks = {"development": {"numeric": "rippled-quadratic", "program": "symbolic-quadratic-cosine", "kernel": "blocked-matrix-product"},
                 "confirmation": {"numeric": "coupled-absolute", "program": "symbolic-absolute-sine", "kernel": "blocked-distance-matrix"}}
        if row["Task"] != tasks[partition][row["Family"]] or type(row["Success"]) is not bool:
            raise ValueError("Cross-partition task or invalid success")
        if row["Status"] not in ("completed", "failed") or not 0 <= row["Calls"] <= plan["Budget"] or not 0 <= row["Proposals"] <= plan["Budget"] * 8:
            raise ValueError("Invalid status or resource cap")
        if not all(math.isfinite(row[k]) and 0 <= row[k] <= 1 for k in ("Quality", "Diversity")):
            raise ValueError("Invalid bounded endpoint")
        if not math.isfinite(row["Seconds"]) or row["Seconds"] < 0:
            raise ValueError("Invalid elapsed cost")
        if row["Status"] == "failed" and (row["Quality"] or row["Diversity"] or row["Success"]):
            raise ValueError("Failure received fitness credit")
        if row["Status"] == "completed":
            resources = row["Resources"]
            if resources["Spent"]["cost_units"] != row["Calls"] or resources["Unknown"] or resources["MaximumViolated"] or any(resources["Reserved"].values()):
                raise ValueError("Unreconciled resource ledger")
            if resources["Spent"]["proposal_calls"] != row["Proposals"] - 8 or row["Success"] != (row["Quality"] >= 0.8):
                raise ValueError("Proposal or success mismatch")
            if len(row["Observations"]) != row["Calls"]:
                raise ValueError("Missing raw observations")
            for observation in row["Observations"]:
                if not math.isfinite(observation["Quality"]) or not 0 <= observation["Quality"] <= 1:
                    raise ValueError("Invalid raw quality")
                times = observation["TimingsMilliseconds"]
                if row["Family"] == "kernel" and (len(times) != 4 or any(not math.isfinite(t) or t < 0 for t in times) or
                        not math.isclose(observation["Quality"], 1 / (1 + statistics.median(times[1:])), rel_tol=1e-12)):
                    raise ValueError("Kernel timing receipt mismatch")
                expected_work = {"numeric": 4, "program": 2048, "kernel": 5 * 24 ** 3}[row["Family"]]
                if observation["PrimitiveCases"] != expected_work:
                    raise ValueError("Primitive work mismatch")
    return result["Rows"]


def endpoint(row):
    return 0 if row["Status"] != "completed" else 0.75 * row["Quality"] + 0.25 * row["Diversity"]


def nominees(rows):
    result = {}
    for family in FAMILIES:
        names = sorted({r["Configuration"] for r in rows if r["Configuration"] != "baseline"})
        result[family] = max(names, key=lambda name: statistics.mean(endpoint(r) for r in rows if r["Family"] == family and r["Configuration"] == name))
    return result


def execute(plan_path, directory, partition):
    plan = load(plan_path)
    validate_plan(plan)
    root = Path(directory)
    root.mkdir(parents=True, exist_ok=True)
    binary = Path(plan["Binary"])
    if digest(binary) != plan["BenchmarkHash"] or digest(binary.parent / "AiDotNet.Evolution.dll") != plan["CoreHash"]:
        raise ValueError("Registered binary changed")
    configs = plan["Configurations"]
    selection = None
    if partition == "confirmation":
        if load(root / "development.claim.json")["PlanHash"] != digest(plan_path):
            raise ValueError("Development used a different plan")
        development = load(root / "development.json")
        selection = nominees(verify(plan, development, "development", configs, digest(root / "development.request.json")))
        configs = [c for c in configs if c["Name"] in {"baseline", *selection.values()}]
    # Durable claim precedes any evaluator invocation. Interrupted campaigns are retained, never rerun in place.
    claim = dict(PlanHash=digest(plan_path), Nominees=selection,
                 DevelopmentHash=digest(root / "development.json") if selection else None)
    write_new(str(Path(plan_path).resolve()) + "." + partition + ".used", dict(Directory=str(root.resolve()), **claim))
    write_new(root / (partition + ".claim.json"), claim)
    request = dict(Protocol=PROTOCOL, Partition=partition, Budget=plan["Budget"],
                   Seeds=plan[partition.title() + "Seeds"], Configurations=configs)
    request_path = root / (partition + ".request.json")
    write_new(request_path, request)
    with (root / (partition + ".log")).open("x", encoding="utf-8") as log:
        completed = subprocess.run(["dotnet", str(binary), "--ablation", str(request_path), str(root / (partition + ".json"))],
                                   stdout=log, stderr=subprocess.STDOUT, check=False)
    verify(plan, load(root / (partition + ".json")), partition, configs, digest(request_path))
    return completed.returncode


def report(plan_path, directory, output):
    plan = load(plan_path)
    root = Path(directory)
    dev = verify(plan, load(root / "development.json"), "development", plan["Configurations"], digest(root / "development.request.json"))
    selected = nominees(dev)
    configs = [c for c in plan["Configurations"] if c["Name"] in {"baseline", *selected.values()}]
    confirmation = verify(plan, load(root / "confirmation.json"), "confirmation", configs, digest(root / "confirmation.request.json"))
    for partition in ("development", "confirmation"):
        claim = load(root / (partition + ".claim.json"))
        if claim["PlanHash"] != digest(plan_path) or (partition == "confirmation" and
                (claim["Nominees"] != selected or claim["DevelopmentHash"] != digest(root / "development.json"))):
            raise ValueError("Frozen selection was altered")
    summaries = []
    for family, name in itertools.product(FAMILIES, [c["Name"] for c in plan["Configurations"]]):
        rows = [r for r in dev if r["Family"] == family and r["Configuration"] == name]
        summaries.append(dict(Family=family, Configuration=name, Quality=statistics.mean(r["Quality"] for r in rows),
                              Diversity=statistics.mean(r["Diversity"] for r in rows), Success=sum(r["Success"] for r in rows)/len(rows),
                              Failures=sum(r["Status"] != "completed" for r in rows),
                              Calls=statistics.mean(r["Calls"] for r in rows), Seconds=statistics.mean(r["Seconds"] for r in rows)))
    decisions = []
    for family in FAMILIES:
        by_key = {(r["Configuration"], r["Seed"]): r for r in confirmation if r["Family"] == family}
        gains = [endpoint(by_key[selected[family], seed]) - endpoint(by_key["baseline", seed]) for seed in plan["ConfirmationSeeds"]]
        # Paired bounded differences in [-1,1]; three preselected family hypotheses.
        radius = math.sqrt(2 * math.log(3 / plan["Alpha"]) / len(gains))
        lower = max(-1, statistics.mean(gains) - radius)
        decisions.append(dict(Family=family, Nominee=selected[family], MeanGain=statistics.mean(gains), LowerGain=lower,
                              Decision="confirmed" if lower > plan["MinimumGain"] else "inconclusive-retain-baseline"))
    interactions = []
    for family in FAMILIES:
        means = {name: statistics.mean(endpoint(r) for r in dev if r["Family"] == family and r["Configuration"] == name)
                 for name in ("baseline", "islands", "islands-migration", "calibration", "growth", "calibration-growth", "continuous", "islands-continuous")}
        interactions.append(dict(Family=family,
            MigrationGivenIslands=means["islands-migration"] - means["islands"],
            CalibrationGrowth=means["calibration-growth"] - means["calibration"] - means["growth"] + means["baseline"],
            IslandsContinuous=means["islands-continuous"] - means["islands"] - means["continuous"] + means["baseline"]))
    presets = [dict(Family=d["Family"], Evidence=d["Decision"], Configuration=next(c for c in configs if c["Name"] ==
                   (d["Nominee"] if d["Decision"] == "confirmed" else "baseline"))) for d in decisions]
    result = dict(PlanHash=digest(plan_path), Development=summaries, Interactions=interactions, Confirmation=decisions, Presets=presets,
                  Scope="Local fixed workloads; bounded expression grammar, not LLM-generated code. Kernel search utility is noisy, not a verified speedup. Descriptive development effects are not significance tests. Library defaults unchanged.")
    write_new(output, result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("register")
    p.add_argument("binary"); p.add_argument("output"); p.add_argument("--seeds", type=int, default=32); p.add_argument("--budget", type=int, default=64)
    p = commands.add_parser("execute")
    p.add_argument("plan"); p.add_argument("directory"); p.add_argument("partition", choices=["development", "confirmation"])
    p = commands.add_parser("report")
    p.add_argument("plan"); p.add_argument("directory"); p.add_argument("output")
    args = parser.parse_args()
    if args.command == "register":
        register(args.binary, args.output, args.seeds, args.budget)
    elif args.command == "execute":
        return execute(args.plan, args.directory, args.partition)
    else:
        report(args.plan, args.directory, args.output)
    return 0


if __name__ == "__main__":
    sys.exit(main())
