"""US-08: fixed-budget adaptation versus the best development static operator."""
import argparse
import itertools
import math
from pathlib import Path
import statistics
import subprocess

from ablation import digest, load, write_new
from portfolio_evidence import verify_row

METHODS = ["mutation", "crossover", "restart", "refinement", "adaptive-parent", "adaptive-archive", "uniform-portfolio"]
TASKS = {"development": dict(numeric="portfolio-v2-shifted-rippled-quadratic", program="portfolio-v2-symbolic-shifted-square-sine", kernel="portfolio-v2-weighted-matrix-product"),
         "confirmation": dict(numeric="portfolio-v2-shifted-coupled-absolute", program="portfolio-v2-symbolic-shifted-absolute-cosine", kernel="portfolio-v2-weighted-l1-matrix")}


def analysis_hash():
    return digest(Path(__file__)) + ":" + digest(Path(__file__).with_name("portfolio_evidence.py")) + ":" + digest(Path(__file__).with_name("ablation.py"))


def register(binary, path, count=32, budget=64, confirmation_count=512):
    if type(count) is not int or type(confirmation_count) is not int or type(budget) is not int or not 2 <= count <= 100 or not 2 <= confirmation_count <= 512 or not 24 <= budget <= 256:
        raise ValueError("Unsupported fixed study size")
    binary = Path(binary).resolve()
    write_new(path, dict(Protocol="portfolio-study-v2", Binary=str(binary), BenchmarkHash=digest(binary), AnalysisHash=analysis_hash(),
                        CoreHash=digest(binary.parent / "AiDotNet.Evolution.dll"), Count=count, Budget=budget,
                        Alpha=0.05, MinimumGain=0.02, Methods=METHODS, ConfirmationCount=confirmation_count,
                        DevelopmentSeeds=list(range(48001, 48001 + count)), ConfirmationSeeds=list(range(940001, 940001 + confirmation_count))))


def validate(plan):
    n = plan["Count"]
    c = plan["ConfirmationCount"]
    if (plan["Protocol"] != "portfolio-study-v2" or type(n) is not int or type(c) is not int or type(plan["Budget"]) is not int or not 2 <= n <= 100 or not 2 <= c <= 512 or not 24 <= plan["Budget"] <= 256 or
            plan["AnalysisHash"] != analysis_hash() or
            plan["Alpha"] != 0.05 or plan["MinimumGain"] != 0.02 or plan["Methods"] != METHODS or
            plan["DevelopmentSeeds"] != list(range(48001, 48001 + n)) or plan["ConfirmationSeeds"] != list(range(940001, 940001 + c))):
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
        verify_row(row, plan["Budget"])
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
        if row["Method"].startswith("adaptive-") or row["Method"] == "uniform-portfolio":
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
    if load(root / "development.claim.json") != dict(PlanHash=digest(path), BestStatic=None, DevelopmentHash=None):
        raise ValueError("Development used another plan")
    if claim != dict(PlanHash=digest(path), BestStatic=selected, DevelopmentHash=digest(root / "development.json")):
        raise ValueError("Static comparator changed after selection")
    decisions = []
    for family, adaptive, control in itertools.product(TASKS["development"], METHODS[4:6], ("best-static", "uniform-portfolio")):
        pairs = {(r["Method"], r["Seed"]): r["Quality"] for r in confirmation if r["Family"] == family}
        comparator = selected[family] if control == "best-static" else control
        gains = [pairs[adaptive, seed] - pairs[comparator, seed] for seed in plan["ConfirmationSeeds"]]
        lower = max(-1, statistics.mean(gains) - math.sqrt(2 * math.log(12 / plan["Alpha"]) / len(gains)))
        decisions.append(dict(Family=family, Adaptive=adaptive, Comparator=comparator, Control=control, MeanGain=statistics.mean(gains),
                              LowerGain=lower, PromotionEligible=lower > plan["MinimumGain"]))
    summaries = []
    for partition, rows in (("development", development), ("confirmation", confirmation)):
        for family, method in itertools.product(TASKS[partition], METHODS):
            group = [r for r in rows if r["Family"] == family and r["Method"] == method]
            summaries.append(dict(Partition=partition, Family=family, Method=method, Quality=statistics.mean(r["Quality"] for r in group),
                                  Cost=statistics.mean(r["Resources"]["Spent"]["cost_units"] for r in group),
                                  Diversity=statistics.mean(r["Diversity"] for r in group),
                                  CpuSeconds=statistics.mean(r["CpuSeconds"] for r in group),
                                  WallSeconds=statistics.mean(r["Seconds"] for r in group),
                                  ProposalCalls=statistics.mean(r["ProposalCalls"] for r in group),
                                  ObjectiveCalls=statistics.mean(r["ObjectiveCalls"] for r in group),
                                  Failures=sum(r["Status"] != "completed" for r in group)))
    before_after = []
    for family, adaptive, control in itertools.product(TASKS["development"], METHODS[4:6], ("best-static", "uniform-portfolio")):
        comparator = selected[family] if control == "best-static" else control
        before = next(r for r in summaries if r["Partition"] == "confirmation" and r["Family"] == family and r["Method"] == comparator)
        after = next(r for r in summaries if r["Partition"] == "confirmation" and r["Family"] == family and r["Method"] == adaptive)
        before_after.append(dict(Family=family, Adaptive=adaptive, Control=control, Comparator=comparator,
            Metrics={key: dict(Before=before[key], After=after[key], Delta=after[key]-before[key])
                     for key in ("Quality", "Diversity", "Cost", "CpuSeconds", "WallSeconds", "ProposalCalls", "ObjectiveCalls", "Failures")}))
    promotions = [dict(Family=family, Adaptive=adaptive,
        Eligible=all(d["PromotionEligible"] for d in decisions if d["Family"] == family and d["Adaptive"] == adaptive)
                 and not any(r["Failures"] for r in summaries if r["Family"] == family))
        for family, adaptive in itertools.product(TASKS["development"], METHODS[4:6])]
    write_new(output, dict(PlanHash=digest(path), Summary=summaries, Decisions=decisions, Promotions=promotions, BeforeAfter=before_after,
                          Scope="Local numeric, expression and CPU kernels; service-operation budgets, not equal CPU time or API money. No model-quality or production speedup claim. Optional portfolio remains opt-in."))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    p = commands.add_parser("register"); p.add_argument("binary"); p.add_argument("path")
    p.add_argument("--count", type=int, default=32); p.add_argument("--budget", type=int, default=64)
    p.add_argument("--confirmation-count", type=int, default=512)
    p = commands.add_parser("execute"); p.add_argument("path"); p.add_argument("directory"); p.add_argument("partition", choices=list(TASKS))
    p = commands.add_parser("report"); p.add_argument("path"); p.add_argument("directory"); p.add_argument("output")
    arguments = vars(parser.parse_args()); command = arguments.pop("command")
    {"register": register, "execute": execute, "report": report}[command](**arguments)
