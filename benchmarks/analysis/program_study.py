"""One-use, fixed-budget program estimation study; never a powered release claim."""
import argparse
import hashlib
import html
import json
import math
from pathlib import Path
import random
import statistics
import subprocess
import sys

from analyze import finite, names, require
from design import digest, write_new
from program_report import TRACKS, read, report as pilot_report

EXTERNAL = Path(__file__).resolve().parents[1] / "external"
sys.path.insert(0, str(EXTERNAL))
from program_tasks import TASKS, initial_program
from run_program_pilot import run as run_pilot
from run_program_audit import run as run_audit

COMPARISONS = [("controlled", "openevolve"), ("controlled", "one-shot"),
               ("controlled", "single-parent"), ("native-bounded", "openevolve")]
SCALES = {"base64_encoding": 0.30, "sha256_hashing": 0.22, "count_connected_components": 0.35}


def hashes(paths):
    return {str(Path(p).resolve()): hashlib.sha256(Path(p).read_bytes()).hexdigest() for p in paths}


def verify_artifacts(plan):
    require(hashes(plan["artifacts"]) == plan["artifacts"], "Registered implementation changed; no dispatch permitted.")


def validate_registration(plan):
    require(plan.get("schema") == "evolution-program-estimation-registration-v1", "Invalid registration schema.")
    require(names(list(plan["tasks"]), 3) and set(plan["tasks"]) == set(plan["scales_seconds"]) and
            all(finite(v) and v > 0 for v in plan["scales_seconds"].values()), "Invalid fixed task scales.")
    schedule = plan["schedule"]
    require([b["phase"] for b in schedule] == ["calibration", "calibration", "confirmation", "confirmation"], "Invalid fixed phase schedule.")
    seeds = [b["seed"] for b in schedule]
    instance_seeds = [b[key] for b in schedule for key in ("search_instance_seed", "diagnostic_instance_seed", "audit_instance_seed")]
    require(len(set(seeds)) == 4 and len(set(instance_seeds)) == 12 and
            all(type(s) is int and 0 <= s < 2**32 for s in seeds + instance_seeds), "Repeated or invalid search/instance seeds.")
    require(all(b["id"] == f"{b['phase']}-{b['seed']}" and len(b["tasks"]) == len(plan["tasks"]) and
                set(b["tasks"]) == set(plan["tasks"]) for b in schedule), "Changed scheduled task panel or block identity.")
    require(plan["confirmation_search_runs_per_task_method"] == 2 and
            plan["model_calls_maximum"] == (len(schedule) * len(plan["tasks"]) * 11 if plan["codex"] else 0) and
            plan["model_calls_maximum"] <= 132, "Changed fixed model-call/run budget.")
    require(type(plan["timing_samples"]) is int and 1 <= plan["timing_samples"] <= 9 and
            finite(plan["alpha"]) and 0 < plan["alpha"] <= 0.2 and finite(plan["planning_power"]) and
            0.5 < plan["planning_power"] < 1 and finite(plan["planning_utility_effect"]) and
            0 < plan["planning_utility_effect"] <= 1, "Invalid registered statistical configuration.")


def prepare(root, *, image, upstream, openevolve, dll, codex=None):
    root = Path(root).resolve()
    require(set(TASKS) == set(SCALES), "The registered three-task panel changed; revalidate its budgets.")
    sources = {task: initial_program(upstream, task)[1] for task in TASKS}
    files = [*EXTERNAL.glob("*.py"), EXTERNAL / "sandbox" / "worker.py", EXTERNAL / "sandbox" / "Dockerfile",
             *Path(__file__).parent.glob("*.py"), *Path(dll).resolve().parent.glob("*.dll")]
    if codex:
        files.append(Path(codex).resolve(strict=True))
    schedule = []
    for phase, seeds in (("calibration", [101, 103]), ("confirmation", [1009, 1013])):
        for seed in seeds:
            tasks = list(TASKS)
            random.Random(seed).shuffle(tasks)
            schedule.append(dict(id=f"{phase}-{seed}", phase=phase, seed=seed, tasks=tasks,
                                 search_instance_seed=seed * 10 + 1, diagnostic_instance_seed=seed * 10 + 2,
                                 audit_instance_seed=seed * 10 + 3))
    plan = dict(schema="evolution-program-estimation-registration-v1", purpose="fixed-budget-estimation",
                model="gpt-6-astra", transport="subscription" if codex else "scripted-fixture", image=image, upstream=str(Path(upstream).resolve()),
                openevolve=str(Path(openevolve).resolve()), dll=str(Path(dll).resolve(strict=True)),
                codex=str(Path(codex).resolve(strict=True)) if codex else None,
                source_revision=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=EXTERNAL, text=True).strip(),
                artifacts=hashes(files), tasks=sources, scales_seconds=SCALES, schedule=schedule,
                model_calls_maximum=132 if codex else 0, model_calls_per_block_maximum=33,
                iterations=2, timing_samples=3, alpha=0.05, planning_power=0.8, planning_utility_effect=0.05,
                confirmation_search_runs_per_task_method=2, tuning_trials=0,
                endpoint="utility=1/(1+fresh-process-batch-seconds/fixed-task-scale)",
                target_utility=0.5, task_weighting="equal fixed tasks; no unseen-family inference",
                budget_policy="Two confirmation runs fixed before calibration; never truncate a powered recommendation or extend until a win.",
                confirmation_scope="fresh search runs and instances on fixed development families, NOT held-out families",
                confidence="Simultaneous two-sided taskwise Hoeffding bounds on paired [-1,1] utility differences; fixed-task averages, conditional on independent stationary runs within tasks.",
                dependence_limit="Requested provider snapshot unreported; shared host/provider drift can violate independence/stationarity; no certified release inference.",
                claim="none")
    validate_registration(plan)
    root.mkdir(parents=True, exist_ok=False)
    write_new(root / "registration.json", plan)
    return digest(plan)


def trajectory(raw, scale):
    counters = raw.get("independent_counters")
    if (not isinstance(counters, dict) or any(not isinstance(counters.get(k), dict) or
            set(counters[k]) != {"model", "evaluate"} or any(type(v) is not int or v < 0 for v in counters[k].values())
            for k in ("attempted", "unknown")) or not finite(counters.get("evaluation_seconds")) or
            type(counters.get("model_tokens")) is not int or counters["model_tokens"] < 0):
        return dict(complete=False, reason="missing-independent-counters", counters=None, points=None)
    receipts = raw.get("receipts", [])
    complete = (len(receipts) == sum(counters["attempted"].values()) and
                [r.get("sequence") for r in receipts] == list(range(1, len(receipts) + 1)) and
                all(sum(r.get("operation") == operation for r in receipts) == count for operation, count in counters["attempted"].items()))
    if not complete:
        return dict(complete=False, reason="dropped-or-reordered-records", counters=counters, points=None)
    best, points, tokens, work, started = None, [], 0, 0.0, -1.0
    for receipt in receipts:
        current_tokens, current_work = receipt.get("model_tokens_after"), receipt.get("evaluation_seconds_after")
        begin, end = receipt.get("started_elapsed_seconds"), receipt.get("finished_elapsed_seconds")
        if (type(current_tokens) is not int or current_tokens < tokens or not finite(current_work) or current_work < work or
                not finite(begin) or not finite(end) or begin < started or end < begin):
            return dict(complete=False, reason="invalid-independent-progress", counters=counters, points=None)
        tokens, work, started = current_tokens, current_work, end
        result = receipt.get("result")
        if receipt["operation"] == "evaluate" and receipt["status"] == "completed" and isinstance(result, dict) and result.get("status") == "valid":
            quality = result.get("quality")
            if not finite(quality) or quality <= 0:
                return dict(complete=False, reason="invalid-search-quality", counters=counters, points=None)
            utility = 1 / (1 + 1 / quality / scale)
            best = utility if best is None else max(best, utility)
        points.append(dict(sequence=receipt["sequence"], operation=receipt["operation"], status=receipt["status"],
                           model_tokens=tokens, evaluator_seconds=work, elapsed_seconds=end, best_search_utility=best))
    if tokens != counters["model_tokens"] or not math.isclose(work, counters["evaluation_seconds"], rel_tol=1e-12):
        return dict(complete=False, reason="counter-mismatch", counters=counters, points=None)
    unknown = any(counters["unknown"].values())
    hits = [p for p in points if p["best_search_utility"] is not None and p["best_search_utility"] >= 0.5]
    target = dict(threshold=0.5, status="unknown" if unknown else "hit" if hits else "not-hit",
                  tokens=hits[0]["model_tokens"] if hits and not unknown else None,
                  evaluator_seconds=hits[0]["evaluator_seconds"] if hits and not unknown else None,
                  observed_tokens=tokens, censoring="last observed work, not unspent allowance; informative early stops possible")
    return dict(complete=True, reason=None, counters=counters, points=points,
                search_only=True, unknown_work=unknown, cost_to_target=target)


def resource_summary(root, plan):
    summary = dict(model_attempts=0, known_model_tokens=0, unknown_model_attempts=0,
                   container_attempts=0, unknown_container_attempts=0, known_supervisor_seconds=0.0,
                   peak_memory_bytes=None, memory_limit_is_not_measured_peak=True)
    for block in plan["schedule"]:
        directory = Path(root) / block["id"]
        for child in sorted((directory / "model").glob("*")):
            if not child.is_dir():
                continue
            summary["model_attempts"] += 1
            receipt = read(child / "receipt.json") if (child / "receipt.json").exists() else {}
            usage = receipt.get("usage", {})
            if receipt.get("unknown_usage") is False and all(type(usage.get(k)) is int and usage[k] >= 0 for k in ("input_tokens", "output_tokens")):
                summary["known_model_tokens"] += usage["input_tokens"] + usage["output_tokens"]
            else:
                summary["unknown_model_attempts"] += 1
        for sandbox in (directory / "sandbox", Path(root) / (block["id"] + "-audit") / "sandbox"):
            for child in sorted(sandbox.glob("evolution-*")):
                if not child.is_dir():
                    continue
                summary["container_attempts"] += 1
                receipt = read(child / "receipt.json") if (child / "receipt.json").exists() else {}
                if receipt.get("unknown_work") is not False:
                    summary["unknown_container_attempts"] += 1
                if finite(receipt.get("supervisor_seconds")):
                    summary["known_supervisor_seconds"] += receipt["supervisor_seconds"]
    return summary


def render(result):
    lines = ["# Fixed-budget program study", "", f"Status: {result['status']}. Claim: none. Superiority not established.", "",
             "Confirmation uses fresh search runs/instances on fixed development task families, not unseen families.",
             "Intervals are conditional on independent stationary runs within each task; provider/host drift remains a limitation.", ""]
    for name in ("calibration", "confirmation"):
        phase = result[name]
        lines += ["## " + name.title(), "", f"Scheduled rows: {phase['scheduled_rows']}; failed/missing/fallback: {phase['failed_missing_or_fallback']}.", "",
                  "| Mode | Comparator | Paired utility effect | Simultaneous interval | Win / tie / loss |", "| --- | --- | ---: | --- | --- |"]
        for comparison in phase["comparisons"]:
            value, interval = comparison["paired_effect"], comparison["simultaneous_interval"]
            lines.append(f"| {comparison['mode']} | {comparison['comparator']} | " +
                         ("unknown" if value is None else f"{value:.6f}") + " | " +
                         ("unknown" if interval is None else f"[{interval[0]:.6f}, {interval[1]:.6f}]") +
                         f" | {comparison['win']} / {comparison['tie']} / {comparison['loss']} |")
        lines += ["", "| Task | Seed | Mode / method | Status | Utility | Actual tokens |", "| --- | ---: | --- | --- | ---: | ---: |"]
        for row in phase["rows"]:
            utility = "unknown" if row["utility"] is None else f"{row['utility']:.6f}"
            lines.append(f"| {row['task']} | {row['search_seed']} | {row['mode']} / {row['method']} | {row['status']} | {utility} | {row['model_tokens']} |")
        lines.append("")
    lines += ["## Accounting and limits", "", "```json", json.dumps(result["resources"], indent=2), "```", "",
              "A two-run confirmation panel is budget-limited estimation, not a truncated powered design. No run is added in response to favorable or unfavorable results.",
              "Fresh-process batch utility includes startup, imports and serialization. Cost and runtime advantages are not interchangeable.",
              "Search curves below are not confirmation evidence; unavailable or incomplete curves are not reconstructed."]
    text = "\n".join(lines) + "\n"
    figures = []
    for block in result["blocks"]:
        for progress in block["progress"]:
            curve = progress["trajectory"]
            label = f"{block['id']} / {progress['task']} / {progress['mode']} / {progress['method']}"
            if not curve["complete"] or curve.get("unknown_work"):
                figures.append("<p>Unavailable trajectory: " + html.escape(label) + "</p>")
                continue
            points = [p for p in curve["points"] if p["best_search_utility"] is not None]
            segments = []
            for index, point in enumerate(points):
                x, y = 20 + 300 * point["model_tokens"] / 100000, 120 - 100 * point["best_search_utility"]
                segments.append((f"M{x:.3f},{y:.3f}" if index == 0 else f"H{x:.3f} V{y:.3f}"))
            figures.append("<figure><figcaption>" + html.escape(label) + "</figcaption><svg viewBox='0 0 340 145' width='340' role='img'>" +
                           "<title>Best measured search utility versus cumulative reported model tokens; 0 to 100000 tokens, utility 0 to 1</title>" +
                           "<path d='M20,20 V120 H320' fill='none' stroke='gray'/><path d='" + " ".join(segments) +
                           "' fill='none' stroke='blue'/><text x='20' y='140' font-size='10'>0 to 100000 reported tokens</text></svg></figure>")
    return text, "<!doctype html><meta charset='utf-8'><title>Program study</title><pre>" + html.escape(text) + "</pre>" + "".join(figures)


def summarize(plan, blocks, phase):
    scheduled = [b for b in plan["schedule"] if b["phase"] == phase]
    require(len(scheduled) >= 2, "Paired search variance needs at least two scheduled independent runs.")
    indexed = {b["id"]: b for b in blocks}
    require(len(indexed) == len(blocks), "Duplicate study block.")
    rows = []
    for block in scheduled:
        observed = {(r["task"], r["mode"], r["method"]): r for r in indexed.get(block["id"], {}).get("rows", [])}
        require(len(observed) == len(indexed.get(block["id"], {}).get("rows", [])), "Duplicate study run.")
        for task in plan["tasks"]:
            for mode, method in TRACKS:
                row = observed.get((task, mode, method), dict(task=task, mode=mode, method=method, status="missing",
                    deployed_seconds=None, model_tokens=None, search_evaluator_seconds=None))
                seconds = row.get("deployed_seconds")
                require(seconds is None or finite(seconds) and seconds > 0, "Invalid deployed runtime.")
                rows.append({**row, "block": block["id"], "search_seed": block["seed"],
                             "utility": None if seconds is None else 1 / (1 + seconds / plan["scales_seconds"][task])})
    pairs, comparisons = [], []
    lookup = {(r["task"], r["mode"], r["method"], r["search_seed"]): r for r in rows}
    n, task_count, comparison_count = len(scheduled), len(plan["tasks"]), len(COMPARISONS)
    half_width = math.sqrt(2 * math.log(2 * task_count * comparison_count / plan["alpha"]) / n)
    for mode, comparator in COMPARISONS:
        groups = []
        for task in plan["tasks"]:
            differences = []
            for block in scheduled:
                a, b = lookup[task, mode, "aidotnet", block["seed"]], lookup[task, mode, comparator, block["seed"]]
                difference = None if a["utility"] is None or b["utility"] is None else a["utility"] - b["utility"]
                pairs.append(dict(task=task, mode=mode, comparator=comparator, seed=block["seed"], difference=difference,
                                  failed_or_fallback=a["status"] != "validated" or b["status"] != "validated"))
                differences.append(difference)
            known = all(d is not None for d in differences)
            groups.append(dict(task=task, differences=differences, mean=statistics.fmean(differences) if known else None,
                               variance=statistics.variance(differences) if known else None))
        known = all(g["mean"] is not None for g in groups)
        effect = statistics.fmean(g["mean"] for g in groups) if known else None
        comparisons.append(dict(mode=mode, comparator=comparator, tasks=groups, paired_effect=effect,
                                simultaneous_interval=[max(-1, effect - half_width), min(1, effect + half_width)] if known else None,
                                interval_status="conditional-fixed-task-bound" if known else "incomplete-panel",
                                win=sum(p["difference"] > 0 for p in pairs if p["mode"] == mode and p["comparator"] == comparator and p["difference"] is not None),
                                tie=sum(p["difference"] == 0 for p in pairs if p["mode"] == mode and p["comparator"] == comparator and p["difference"] is not None),
                                loss=sum(p["difference"] < 0 for p in pairs if p["mode"] == mode and p["comparator"] == comparator and p["difference"] is not None)))
    return dict(phase=phase, scheduled_rows=len(rows), rows=rows, pairs=pairs, comparisons=comparisons,
                independent_search_runs_per_task_method=n, timing_repeats_are_not_runs=True,
                failed_missing_or_fallback=sum(r["status"] != "validated" for r in rows),
                known_model_tokens=sum(r.get("model_tokens") or 0 for r in rows),
                unknown_cost_rows=sum(r.get("model_tokens") is None or r.get("search_evaluator_seconds") is None for r in rows),
                claim="none")


def sample_advice(plan, calibration):
    variances = [t["variance"] for c in calibration["comparisons"] for t in c["tasks"]]
    alpha, power, effect = plan["alpha"], plan["planning_power"], plan["planning_utility_effect"]
    t, c = len(plan["tasks"]), len(COMPARISONS)
    required = math.ceil(2 * (math.sqrt(math.log(t * c / alpha)) + math.sqrt(math.log(t / (1 - power))))**2 / effect**2)
    normal = statistics.NormalDist()
    z = normal.inv_cdf(1 - alpha / c) + normal.inv_cdf(power)
    advisory = max(2, math.ceil(z * z * max(variances) / effect**2)) if all(v is not None for v in variances) else None
    return dict(schema="program-sample-advice-v1", paired_variances=variances, advisory_normal_runs=advisory,
                conservative_powered_runs=required, fixed_confirmation_runs=plan["confirmation_search_runs_per_task_method"],
                powered_design_feasible=required <= plan["confirmation_search_runs_per_task_method"],
                decision="execute pre-registered budget-limited estimation, NOT truncated powered confirmation",
                warning="Two pilot runs give an unstable variance estimate; zero observed variance is not zero population variance.")


def execute(root, expected_hash):
    root = Path(root).resolve()
    plan = read(root / "registration.json")
    validate_registration(plan)
    require(plan.get("schema") == "evolution-program-estimation-registration-v1" and digest(plan) == expected_hash, "Registration hash mismatch.")
    verify_artifacts(plan)
    write_new(root / "consumed.json", dict(registration_sha256=expected_hash))
    blocks = []
    failure = None
    for block in plan["schedule"]:
        try:
            verify_artifacts(plan)
            if block["phase"] == "confirmation" and not (root / "confirmation-registration.json").exists():
                calibration = summarize(plan, blocks, "calibration")
                write_new(root / "calibration.json", calibration)
                advice = sample_advice(plan, calibration)
                write_new(root / "sample-advice.json", advice)
                write_new(root / "confirmation-registration.json", dict(parent_sha256=expected_hash, advice_sha256=digest(advice),
                           blocks=[b for b in plan["schedule"] if b["phase"] == "confirmation"], claim="estimation-only"))
            output = root / block["id"]
            result = run_pilot(output, plan["image"], plan["upstream"], plan["openevolve"], plan["dll"],
                               codex=plan["codex"], model=plan["model"], tasks=block["tasks"], samples=plan["timing_samples"],
                               seed=block["seed"], search_instance_seed=block["search_instance_seed"], diagnostic_instance_seed=block["diagnostic_instance_seed"])
            audit_path = root / (block["id"] + "-audit")
            run_audit(output, audit_path, plan["image"], seed=block["audit_instance_seed"])
            analyzed = pilot_report(output, audit_path)
            progress = []
            for task in block["tasks"]:
                raw = read(output / task / "comparison.json")
                for run in raw["runs"]:
                    progress.append(dict(task=task, mode=run["mode"], method=run["method"],
                                         trajectory=trajectory(run, plan["scales_seconds"][task])))
            blocks.append(dict(id=block["id"], phase=block["phase"], seed=block["seed"], rows=analyzed["rows"],
                               progress=progress, model_calls=result["model_calls"], evaluator_attempts=result["evaluator_attempts"]))
            write_new(root / (block["id"] + "-analysis.json"), blocks[-1])
            require(not result.get("not_run") and result["unknown_evaluator_attempts"] == 0 and
                    all(not any(p["trajectory"]["counters"]["unknown"].values()) for p in progress), "Unknown work stops subsequent blocks.")
            require(sum(b["model_calls"] for b in blocks) <= plan["model_calls_maximum"], "Study model-call cap exceeded.")
            print(json.dumps(dict(block=block["id"], status=result["status"], model_calls=result["model_calls"])), flush=True)
        except Exception as error:
            failure = dict(block=block["id"], error=type(error).__name__ + ": " + str(error)[:400])
            break
    result = dict(schema="evolution-program-study-v1", registration_sha256=expected_hash,
                  status="completed" if failure is None else "failed", failure=failure, blocks=blocks,
                  calibration=summarize(plan, blocks, "calibration"), confirmation=summarize(plan, blocks, "confirmation"),
                  resources=resource_summary(root, plan), claim="none", superiority_established=False)
    if result["resources"]["model_attempts"] > plan["model_calls_maximum"]:
        result.update(status="failed", failure={"error": "Independent model-call cap exceeded"})
    write_new(root / "study.json", result)
    markdown, webpage = render(result)
    (root / "study.md").write_text(markdown, encoding="utf-8")
    (root / "study.html").write_text(webpage, encoding="utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare_parser = commands.add_parser("prepare")
    for name in ("root", "image", "upstream", "openevolve", "dll"):
        prepare_parser.add_argument("--" + name, required=True)
    prepare_parser.add_argument("--codex")
    execute_parser = commands.add_parser("execute")
    execute_parser.add_argument("--root", required=True)
    execute_parser.add_argument("--registration-sha256", required=True)
    args = vars(parser.parse_args())
    command = args.pop("command")
    if command == "prepare":
        print(prepare(**args))
    else:
        result = execute(args["root"], args["registration_sha256"])
        print(json.dumps(dict(status=result["status"], claim=result["claim"])))
        raise SystemExit(0 if result["status"] == "completed" else 1)
