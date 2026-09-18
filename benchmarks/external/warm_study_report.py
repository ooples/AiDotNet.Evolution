"""Search-level intervals and full descriptive metrics; never an all-metrics claim."""
import argparse
import json
import math
from pathlib import Path
import statistics

from docker_sandbox import encode
from warm_study_design import CONTRASTS, digest


def interval(logs, alpha):
    from scipy.stats import t
    if len(logs) < 2 or not all(math.isfinite(x) for x in logs):
        raise ValueError("Require finite independent search pairs")
    mean = statistics.mean(logs)
    radius = t.ppf(1-alpha/2, len(logs)-1)*statistics.stdev(logs)/math.sqrt(len(logs))
    return dict(ratio=math.exp(mean), lower=math.exp(mean-radius), upper=math.exp(mean+radius), n=len(logs))


def summarize(report):
    plan = report["plan"]
    accounting = None
    if plan.get("schema") in ("warm-head-to-head-v3", "warm-head-to-head-v4", "warm-head-to-head-v5"):
        from warm_budget import validate_accounting
        accounting = validate_accounting(report)
    if plan.get("schema") in ("warm-head-to-head-v4", "warm-head-to-head-v5"):
        from warm_confirmation import validate_report
        validate_report(report)
    if plan.get("schema") == "warm-head-to-head-v5":
        from warm_screening import validate_screening
        validate_screening(report)
    if report["plan_sha256"] != digest(plan) or report["status"] != "completed" or report["unknown_work"]:
        raise ValueError("Incomplete/unreconciled study cannot produce a comparative summary")
    if len(report["rows"]) != len(plan["grid"]):
        raise ValueError("Missing planned cells")
    for row, cell in zip(report["rows"], plan["grid"]):
        if row["status"] != "completed" or any(row.get(k) != v for k,v in cell.items()):
            raise ValueError("Planned grid was changed")
        keys = [(p["mode"],p["method"]) for p in row["pairs"]]
        if len(keys) != len(set(keys)) or set(keys) != {tuple(t) for t in cell["tracks"]}:
            raise ValueError("Missing/duplicated controller results")
    summaries, comparisons = [], []
    groups = {}
    for row in report["rows"]:
        for pair in row["pairs"]:
            profile = row["evolution_profile"] if pair["method"] == "aidotnet" else row["openevolve_profile"] if pair["method"] == "openevolve" else "fixed"
            groups.setdefault((row["task"],pair["mode"],pair["method"],profile), []).append(pair)
    for (task, mode, method, profile), pairs in groups.items():
        resources = [r for p in pairs for r in p["deployed"]["resources"]]
        summaries.append(dict(task=task, mode=mode, method=method, profile=profile, searches=len(pairs),
                              before_seconds=statistics.mean(p["original"]["duration_seconds"] for p in pairs),
                              after_seconds=statistics.mean(p["deployed_seconds"] for p in pairs),
                              geometric_speedup=math.exp(statistics.mean(math.log(p["speedup"]) for p in pairs)),
                              throughput_cases_per_second=statistics.mean(p["deployed"]["throughput_cases_per_second"] for p in pairs),
                              cpu_seconds_through_response=statistics.mean(r["cpu_seconds_through_response"] for r in resources),
                              warm_cpu_seconds=statistics.mean(r["warm_cpu_seconds"] for r in resources),
                              peak_bytes_through_response_max=max(r["peak_bytes_through_response"] for r in resources),
                              model_calls=sum(p["model_calls"] for p in pairs), model_tokens=sum(p["model_tokens"] for p in pairs),
                              search_wall_seconds=sum(p["search_wall_seconds"] for p in pairs),
                              search_evaluation_seconds=sum(p["search_evaluation_seconds"] for p in pairs),
                              fallbacks=sum(p["fallback"] for p in pairs),
                              invalid_selected=sum(p["selected"]["status"] != "valid" for p in pairs),
                              invalid_audits=sum(a["status"] != "valid" for p in pairs for a in p["audits"])))
    if plan["phase"] != "development":
        tasks = sorted({r["task"] for r in report["rows"]})
        for task in tasks:
            rows = [r for r in report["rows"] if r["task"] == task]
            if len({r["seed"] for r in rows}) != len(rows):
                raise ValueError("Repeated searches are not independent sample units")
            for mode, competitor in CONTRASTS:
                logs = []
                for row in rows:
                    pairs = {(p["mode"],p["method"]):p for p in row["pairs"]}
                    logs.append(math.log(pairs[(mode,competitor)]["speedup"] / pairs[(mode,"aidotnet")]["speedup"]))
                ci = interval(logs, .05/(len(tasks)*len(CONTRASTS)))
                comparisons.append(dict(task=task, mode=mode, competitor=competitor, **ci,
                                        latency_superiority=ci["upper"] < 1, practical_twenty_percent=ci["upper"] < .8))
    return dict(schema="warm-study-summary-v1", phase=plan["phase"], plan_sha256=report["plan_sha256"],
                metrics=summaries, comparisons=comparisons, claim="none", accounting=accounting,
                screening=[dict(cell=index,owner=owner,**audit["summary"])
                           for index,cell in enumerate(report["rows"]) for owner,audit in cell.get("screening",{}).items()],
                all_registered_latency_gates_pass=bool(comparisons) and plan["phase"] == "final" and all(c["latency_superiority"] for c in comparisons),
                limitations=["Intervals assume approximately normal independent search-level log ratios; only the registered tasks are covered",
                             "Latency significance is NOT superiority on tokens, CPU, memory, search cost or correctness",
                             "Kernel resources include observer/startup and stop at response probe, not full host/process lifetime",
                             "Trusted upstream validation is not an independent mathematical correctness proof",
                             "Task/host/model-version coverage and power must be reviewed before any competitive claim"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report")
    parser.add_argument("output")
    args = parser.parse_args()
    result = summarize(json.loads(Path(args.report).read_bytes()))
    with Path(args.output).open("xb") as stream:
        stream.write(encode(result))
