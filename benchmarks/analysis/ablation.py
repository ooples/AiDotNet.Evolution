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

PROTOCOL = "feature-ablation-v2"
FAMILIES = ("numeric", "program", "kernel")
FLAGS = ("Islands", "Migration", "Novelty", "Calibration", "Growth", "Continuous")
TASKS = {"development": dict(numeric="v2-shifted-rippled-quadratic", program="v2-symbolic-shifted-square-sine", kernel="v2-weighted-matrix-product"),
         "confirmation": dict(numeric="v2-shifted-coupled-absolute", program="v2-symbolic-shifted-absolute-cosine", kernel="v2-weighted-l1-matrix")}


def finite(value, lower=0, upper=math.inf):
    return type(value) in (int, float) and math.isfinite(value) and lower <= value <= upper


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
    confirmation_count = len(plan["ConfirmationSeeds"])
    if (plan["Protocol"] != PROTOCOL or plan["Alpha"] != 0.05 or plan["MinimumGain"] != 0.02 or
            plan["QualityWeight"] != 0.75 or json.dumps(plan["Configurations"],sort_keys=True) != json.dumps(matrix(),sort_keys=True) or not 2 <= count <= 100 or
            plan["DevelopmentSeeds"] != list(range(170001, 170001 + count)) or
            not 2 <= confirmation_count <= 1024 or plan["ConfirmationSeeds"] != list(range(1910001, 1910001 + confirmation_count)) or
            any(type(s) is not int for s in plan["DevelopmentSeeds"] + plan["ConfirmationSeeds"]) or
            type(plan["Budget"]) is not int or not 16 <= plan["Budget"] <= 1024 or
            plan["Budget"] * (count * 19 * 3 + confirmation_count * 4 * 3) > 2_000_000 or
            plan["AnalysisHash"] != digest(__file__)):
        raise ValueError("Changed predeclared analysis criteria")


def register(binary, output, seeds=32, budget=64, confirmation_seeds=None):
    confirmation_seeds = seeds if confirmation_seeds is None else confirmation_seeds
    if (type(seeds) is not int or type(budget) is not int or type(confirmation_seeds) is not int
            or not 2 <= seeds <= 100 or not 2 <= confirmation_seeds <= 1024 or not 16 <= budget <= 1024
            or budget * (seeds * 19 * 3 + confirmation_seeds * 4 * 3) > 2_000_000):
        raise ValueError("Registration exceeds fixed bounds")
    binary = Path(binary).resolve()
    plan = dict(Protocol=PROTOCOL, Binary=str(binary), BenchmarkHash=digest(binary), AnalysisHash=digest(__file__),
                CoreHash=digest(binary.parent / "AiDotNet.Evolution.dll"),
                Alpha=0.05, MinimumGain=0.02, QualityWeight=0.75, Budget=budget,
                DevelopmentSeeds=list(range(170001, 170001 + seeds)),
                ConfirmationSeeds=list(range(1910001, 1910001 + confirmation_seeds)),
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
        if row["Task"] != TASKS[partition][row["Family"]] or type(row["Success"]) is not bool:
            raise ValueError("Cross-partition task or invalid success")
        if (row["Status"] not in ("completed", "failed") or type(row["Calls"]) is not int or type(row["Proposals"]) is not int
                or not 0 <= row["Calls"] <= plan["Budget"] or not 0 <= row["Proposals"] <= plan["Budget"] * 8):
            raise ValueError("Invalid status or resource cap")
        if not all(finite(row[k], 0, 1) for k in ("Quality", "Diversity")):
            raise ValueError("Invalid bounded endpoint")
        if not finite(row["Seconds"]) or not finite(row["CpuSeconds"]):
            raise ValueError("Invalid elapsed cost")
        target = row["CallsToTarget"]
        if ((target is None) != (row["SecondsToTarget"] is None) or (target is not None and
                (type(target) is not int or not 1 <= target <= row["Calls"] or not finite(row["SecondsToTarget"], 0, row["Seconds"])))):
            raise ValueError("Invalid or censored target attainment")
        if row["Status"] == "failed" and (target is not None or row["FinalElites"]):
            raise ValueError("Failed run has successful evidence")
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
                if not finite(observation["Quality"], 0, 1):
                    raise ValueError("Invalid raw quality")
                if len(observation["Descriptors"]) != 2 or any(not finite(v,-1,1) for v in observation["Descriptors"]):
                    raise ValueError("Invalid reference-grid descriptors")
                times = observation["TimingsMilliseconds"]
                if row["Family"] != "kernel" and times:
                    raise ValueError("Unexpected timing observations")
                if row["Family"] == "kernel" and (len(times) != 4 or any(not finite(t) or t <= 0 for t in times) or
                        not math.isclose(observation["Quality"], 1 / (1 + statistics.median(times[1:])), rel_tol=1e-12)):
                    raise ValueError("Kernel timing receipt mismatch")
                expected_work = {"numeric": 4, "program": 2048, "kernel": 5 * 24 ** 3}[row["Family"]]
                if observation["PrimitiveCases"] != expected_work:
                    raise ValueError("Primitive work mismatch")
            elites = row["FinalElites"]
            cells = [tuple(e["Bins"]) for e in elites]
            if (len(cells) != len(set(cells)) or any(len(b) != 2 or any(type(i) is not int or not 0 <= i < 8 for i in b) for b in cells)
                    or row["Diversity"] != len(cells) / 64 or row["Quality"] != max((e["Quality"] for e in elites),default=0)):
                raise ValueError("Unreconstructable final archive metrics")
            raw = {(o["Genome"], o["Quality"], tuple(min(7,math.floor((v+1)*4)) for v in o["Descriptors"])) for o in row["Observations"]}
            if any((e["Genome"],e["Quality"],tuple(e["Bins"])) not in raw for e in elites):
                raise ValueError("Final elite has no matching raw evaluation")
            if (target is not None) != any(o["Quality"] >= .8 for o in row["Observations"]):
                raise ValueError("Target attainment differs from observed search")
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
    if partition not in TASKS:
        raise ValueError("Unknown registered partition")
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
                              Calls=statistics.mean(r["Calls"] for r in rows), Seconds=statistics.mean(r["Seconds"] for r in rows),
                              CpuSeconds=statistics.mean(r["CpuSeconds"] for r in rows),
                              TargetReachRate=sum(r["CallsToTarget"] is not None for r in rows)/len(rows),
                              PenalizedCallsToTarget=statistics.mean(r["CallsToTarget"] if r["CallsToTarget"] is not None else plan["Budget"]+1 for r in rows),
                              SecondsToTargetAmongReachers=statistics.mean(r["SecondsToTarget"] for r in rows if r["SecondsToTarget"] is not None)
                                  if any(r["SecondsToTarget"] is not None for r in rows) else None))
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
    effects = []
    for family in FAMILIES:
        before = next(r for r in summaries if r["Family"] == family and r["Configuration"] == "baseline")
        baseline = {r["Seed"]:r for r in dev if r["Family"] == family and r["Configuration"] == "baseline"}
        for after in (r for r in summaries if r["Family"] == family and r["Configuration"] != "baseline"):
            pairs = [endpoint(r)-endpoint(baseline[r["Seed"]]) for r in dev if r["Family"] == family and r["Configuration"] == after["Configuration"]]
            effects.append(dict(Family=family, Configuration=after["Configuration"], Before=before, After=after,
                Delta={k:after[k]-before[k] for k in ("Quality","Diversity","Success","Calls","Seconds","CpuSeconds","TargetReachRate","PenalizedCallsToTarget")},
                PairedEndpointGain=statistics.mean(pairs), PairedWins=sum(g>0 for g in pairs), PairedLosses=sum(g<0 for g in pairs),
                Interpretation="Paired descriptive development comparison; not a promotion test"))
    result = dict(PlanHash=digest(plan_path), Development=summaries, Effects=effects, Interactions=interactions, Confirmation=decisions, Presets=presets,
                  Scope="Local fixed workloads; bounded expression grammar, not LLM-generated code. Kernel search utility is noisy, not a verified speedup. Descriptive development effects are not significance tests. Library defaults unchanged.")
    write_new(output, result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("register")
    p.add_argument("binary"); p.add_argument("output"); p.add_argument("--seeds", type=int, default=32); p.add_argument("--budget", type=int, default=64)
    p.add_argument("--confirmation-seeds", type=int, help="Predeclare a larger independent confirmation sample; defaults to --seeds")
    p = commands.add_parser("execute")
    p.add_argument("plan"); p.add_argument("directory"); p.add_argument("partition", choices=["development", "confirmation"])
    p = commands.add_parser("report")
    p.add_argument("plan"); p.add_argument("directory"); p.add_argument("output")
    args = parser.parse_args()
    if args.command == "register":
        register(args.binary, args.output, args.seeds, args.budget, args.confirmation_seeds)
    elif args.command == "execute":
        return execute(args.plan, args.directory, args.partition)
    else:
        report(args.plan, args.directory, args.output)
    return 0


if __name__ == "__main__":
    sys.exit(main())
