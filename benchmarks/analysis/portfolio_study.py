"""US-08: fixed-budget adaptation versus the best development static operator."""
import argparse
import itertools
import math
from pathlib import Path
import statistics
import subprocess

from ablation import digest, load, write_new

METHODS = ["mutation", "crossover", "restart", "refinement", "adaptive-parent", "adaptive-archive"]
TASKS = {"development": dict(numeric="rosenbrock-chain", program="symbolic-quartic-cosine", kernel="blocked-gram-product"),
         "confirmation": dict(numeric="maximum-coupled-square", program="symbolic-frequency-absolute", kernel="blocked-l1-distance")}


def register(binary, path, count=32, budget=128):
    if not 2 <= count <= 100 or not 24 <= budget <= 256:
        raise ValueError("Unsupported fixed study size")
    binary = Path(binary).resolve()
    write_new(path, dict(Protocol="portfolio-study-v1", Binary=str(binary), BenchmarkHash=digest(binary),
                        CoreHash=digest(binary.parent / "AiDotNet.Evolution.dll"), Count=count, Budget=budget,
                        Alpha=0.05, MinimumGain=0.02, Methods=METHODS,
                        DevelopmentSeeds=list(range(28001, 28001 + count)), ConfirmationSeeds=list(range(820001, 820001 + count))))


def validate(plan):
    n = plan["Count"]
    if (plan["Protocol"] != "portfolio-study-v1" or not 2 <= n <= 100 or not 24 <= plan["Budget"] <= 256 or
            plan["Alpha"] != 0.05 or plan["MinimumGain"] != 0.02 or plan["Methods"] != METHODS or
            plan["DevelopmentSeeds"] != list(range(28001, 28001 + n)) or plan["ConfirmationSeeds"] != list(range(820001, 820001 + n))):
        raise ValueError("Changed study criteria")


def verify(plan, directory, partition):
    validate(plan)
    root = Path(directory)
    result = load(root / (partition + ".json"))
    expected = dict(Partition=partition, Budget=plan["Budget"], Seeds=plan[partition.title() + "Seeds"])
    if (result["Protocol"] != plan["Protocol"] or result["Request"] != expected or
            result["RequestHash"] != digest(root / (partition + ".request.json")) or
            result["BenchmarkHash"] != plan["BenchmarkHash"] or result["CoreHash"] != plan["CoreHash"]):
        raise ValueError("Study artifact mismatch")
    keys = [(r["Family"], r["Method"], r["Seed"]) for r in result["Rows"]]
    if len(keys) != len(set(keys)) or set(keys) != set(itertools.product(TASKS[partition], METHODS, expected["Seeds"])):
        raise ValueError("Missing, duplicated or extra rows")
    for family, seed in itertools.product(TASKS[partition], expected["Seeds"]):
        if len({r["InitialHash"] for r in result["Rows"] if r["Family"] == family and r["Seed"] == seed}) != 1:
            raise ValueError("Unpaired initial populations")
    for row in result["Rows"]:
        resources = row["Resources"]
        if (row["Task"] != TASKS[partition][row["Family"]] or row["Status"] not in ("completed", "failed") or
                not math.isfinite(row["Quality"]) or not 0 <= row["Quality"] <= 1):
            raise ValueError("Invalid task, status or endpoint")
        if row["Status"] == "failed":
            if row["Quality"] != 0:
                raise ValueError("Failed run received credit")
            continue
        if (resources["Spent"]["cost_units"] != row["ProposalCalls"] + row["ObjectiveCalls"] or
                resources["Spent"]["cost_units"] > plan["Budget"] or resources["Unknown"] or resources["MaximumViolated"] or
                any(resources["Reserved"].values()) or len(row["Observations"]) != row["ObjectiveCalls"]):
            raise ValueError("Missing refinement work or invalid total costs")
        credits = row["Credits"]
        if len({c["Generation"] for c in credits}) != len(credits):
            raise ValueError("Duplicate outcome notification")
        if row["Method"].startswith("adaptive-"):
            if len(credits) < row["ProposalCalls"] or sum(c["ProposalCost"]["Charged"]["Amounts"]["cost_units"] for c in credits) != sum(
                    r["Charged"]["Amounts"]["cost_units"] for r in resources["Receipts"] if r["Stage"] == "Proposal"):
                raise ValueError("Missing typed cost attribution")
            for credit in credits:
                if (not math.isfinite(credit["Reward"]) or not 0 <= credit["Reward"] <= 1 or
                        (credit["Status"] != "Completed" and credit["Reward"] != 0)):
                    raise ValueError("Invalid reward")
    return result["Rows"]


def best_static(rows):
    return {family: max(METHODS[:4], key=lambda method: statistics.mean(r["Quality"] for r in rows if r["Family"] == family and r["Method"] == method))
            for family in TASKS["development"]}


def execute(path, directory, partition):
    plan = load(path); validate(plan)
    root = Path(directory); root.mkdir(parents=True, exist_ok=True)
    binary = Path(plan["Binary"])
    if digest(binary) != plan["BenchmarkHash"] or digest(binary.parent / "AiDotNet.Evolution.dll") != plan["CoreHash"]:
        raise ValueError("Measured binaries changed")
    selected = None
    if partition == "confirmation":
        if load(root / "development.claim.json")["PlanHash"] != digest(path):
            raise ValueError("Development used another plan")
        selected = best_static(verify(plan, root, "development"))
    claim = dict(PlanHash=digest(path), BestStatic=selected,
                 DevelopmentHash=digest(root / "development.json") if selected else None)
    write_new(str(Path(path).resolve()) + "." + partition + ".used", dict(Directory=str(root.resolve()), **claim))
    write_new(root / (partition + ".claim.json"), claim)
    request = root / (partition + ".request.json")
    write_new(request, dict(Partition=partition, Budget=plan["Budget"], Seeds=plan[partition.title() + "Seeds"]))
    with (root / (partition + ".log")).open("x", encoding="utf-8") as log:
        run = subprocess.run(["dotnet", str(binary), "--portfolio-study", str(request), str(root / (partition + ".json"))],
                             stdout=log, stderr=subprocess.STDOUT, check=False)
    verify(plan, root, partition)
    if run.returncode:
        raise RuntimeError("Study contains failed runs; retain and investigate")


def report(path, directory, output):
    plan = load(path); root = Path(directory)
    development = verify(plan, root, "development")
    confirmation = verify(plan, root, "confirmation")
    selected = best_static(development)
    claim = load(root / "confirmation.claim.json")
    if claim != dict(PlanHash=digest(path), BestStatic=selected, DevelopmentHash=digest(root / "development.json")):
        raise ValueError("Static comparator changed after selection")
    decisions = []
    for family, adaptive in itertools.product(TASKS["development"], METHODS[4:]):
        pairs = {(r["Method"], r["Seed"]): r["Quality"] for r in confirmation if r["Family"] == family}
        gains = [pairs[adaptive, seed] - pairs[selected[family], seed] for seed in plan["ConfirmationSeeds"]]
        lower = max(-1, statistics.mean(gains) - math.sqrt(2 * math.log(6 / plan["Alpha"]) / len(gains)))
        decisions.append(dict(Family=family, Adaptive=adaptive, BestStatic=selected[family], MeanGain=statistics.mean(gains),
                              LowerGain=lower, PromotionEligible=lower > plan["MinimumGain"]))
    summaries = []
    for partition, rows in (("development", development), ("confirmation", confirmation)):
        for family, method in itertools.product(TASKS[partition], METHODS):
            group = [r for r in rows if r["Family"] == family and r["Method"] == method]
            summaries.append(dict(Partition=partition, Family=family, Method=method, Quality=statistics.mean(r["Quality"] for r in group),
                                  Cost=statistics.mean(r["Resources"]["Spent"]["cost_units"] for r in group),
                                  Failures=sum(r["Status"] != "completed" for r in group)))
    write_new(output, dict(PlanHash=digest(path), Summary=summaries, Decisions=decisions,
                          Scope="Local numeric, expression and CPU kernels; service-operation budgets, not equal CPU time or API money. No model-quality or production speedup claim. Optional portfolio remains opt-in."))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("register"); p.add_argument("binary"); p.add_argument("path")
    p.add_argument("--count", type=int, default=32); p.add_argument("--budget", type=int, default=128)
    p = commands.add_parser("execute"); p.add_argument("path"); p.add_argument("directory"); p.add_argument("partition", choices=list(TASKS))
    p = commands.add_parser("report"); p.add_argument("path"); p.add_argument("directory"); p.add_argument("output")
    arguments = vars(parser.parse_args()); command = arguments.pop("command")
    {"register": register, "execute": execute, "report": report}[command](**arguments)
