"""Read-only US-02 program-pilot reporting. Never converts timings into search seeds."""
import argparse
import hashlib
import html
import json
import math
from pathlib import Path
import statistics

from analyze import finite, names, require
from design import digest, write_new

TRACKS = [("controlled", method) for method in ("aidotnet", "openevolve", "one-shot", "single-parent")]
TRACKS += [("native-bounded", method) for method in ("aidotnet", "openevolve")]


def read(path):
    path = Path(path)
    require(path.stat().st_size <= 32 * 1024**2, "Program report exceeds 32 MiB.")
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "Duplicate JSON field.")
            result[key] = value
        return result
    return json.loads(path.read_bytes(), object_pairs_hook=unique,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError("Nonfinite JSON.")))


def indexed(rows, key):
    result = {}
    for row in rows:
        identity = key(row)
        require(identity not in result, "Duplicate scheduled observation.")
        result[identity] = row
    return result


def duration(receipt):
    if receipt.get("status") != "valid" or receipt.get("unknown_work") is not False:
        return None
    samples = receipt.get("samples")
    require(isinstance(samples, list) and samples and all(finite(x) and x > 0 for x in samples), "Invalid timing samples.")
    value = statistics.median(samples)
    require(finite(receipt.get("duration_seconds")) and
            math.isclose(value, receipt["duration_seconds"], rel_tol=1e-12), "Median does not match timing samples.")
    return value


def report(pilot, audit):
    pilot, audit = Path(pilot), Path(audit)
    plan, campaign = read(pilot / "plan.json"), read(pilot / "report.json")
    audit_plan, audited = read(audit / "plan.json"), read(audit / "report.json")
    require(plan.get("schema") == "evolution-program-pilot-v1" and plan.get("claim") == "none", "Unsupported program plan.")
    require(campaign.get("status") in ("completed", "failed") and campaign.get("claim") == "none", "Require a stopped development pilot.")
    require(campaign.get("plan_sha256") == digest(plan), "Pilot plan binding changed.")
    require(audit_plan.get("schema") == "evolution-post-selection-audit-v1" and
            audited.get("status") in ("passed", "failed") and audited.get("plan_sha256") == digest(audit_plan), "Invalid audit binding/state.")
    require(audit_plan.get("pilot_report_sha256") == hashlib.sha256((pilot / "report.json").read_bytes()).hexdigest(), "Audit belongs to another pilot.")
    tasks = campaign.get("planned_tasks")
    require(names(tasks, 256) and set(tasks) == set(plan["tasks"]), "Invalid planned task panel.")
    require(len(plan.get("search_seeds", [])) == 1 and type(plan["search_seeds"][0]) is int, "This pilot schema represents exactly one search seed.")
    observed = indexed(campaign["tasks"], lambda row: row["task"])
    require(set(observed).issubset(tasks), "Unplanned task.")
    audit_rows = indexed(audited["runs"], lambda row: (row["task"], row["mode"], row["method"]))
    require(set(audit_rows).issubset({(task, *track) for task in tasks for track in TRACKS}), "Unplanned audit observation.")
    rows = []
    for task in tasks:
        pairs = indexed(observed.get(task, {}).get("pairs", []), lambda row: (row["mode"], row["method"]))
        require(set(pairs).issubset(TRACKS), "Unplanned comparison track.")
        for mode, method in TRACKS:
            pair = pairs.get((mode, method))
            row = dict(task=task, mode=mode, method=method, search_seed=plan["search_seeds"][0],
                       status="missing", original_seconds=None, deployed_seconds=None, speedup=None,
                       model_tokens=None, search_evaluator_seconds=None, timing_samples=0)
            if pair is not None:
                original, selected = pair["original"], pair["selected"]
                require(original.get("phase") == selected.get("phase") == "confirmation" and
                        original.get("input_sha256") == selected.get("input_sha256") and
                        original.get("oracle_sha256") == selected.get("oracle_sha256"), "Diagnostic inputs/oracles/phases differ.")
                require(original["candidate_hash"] == plan["tasks"][task]["source_sha256"], "Original source identity changed.")
                before, after = duration(original), duration(selected)
                check = audit_rows.get((task, mode, method))
                if check is not None:
                    require(check["selected_hash"] == selected["candidate_hash"] and type(check["valid"]) is bool, "Audited selection identity/verdict changed.")
                tokens, work = pair.get("model_tokens"), pair.get("search_evaluation_seconds")
                require(tokens is None or type(tokens) is int and tokens >= 0, "Invalid actual token cost.")
                require(work is None or finite(work), "Invalid evaluator work.")
                fallback = (pair.get("fallback") is True or pair["search_status"] != "completed" or
                            selected["candidate_hash"] == original["candidate_hash"] or after is None or
                            check is not None and not check["valid"])
                status = "unknown" if before is None or check is None else "fallback" if fallback else "validated"
                deployed = None if status == "unknown" else before if fallback else after
                row.update(status=status, original_seconds=before, deployed_seconds=deployed,
                           speedup=before / deployed if deployed is not None else None,
                           model_tokens=tokens, search_evaluator_seconds=work,
                           timing_samples=len(original.get("samples", [])) + len(selected.get("samples", [])))
            rows.append(row)
    by_track = {(r["task"], r["mode"], r["method"]): r for r in rows}
    comparisons = []
    for mode, comparator in [("controlled", "openevolve"), ("controlled", "one-shot"),
                             ("controlled", "single-parent"), ("native-bounded", "openevolve")]:
        paired = []
        for task in tasks:
            primary, control = by_track[task, mode, "aidotnet"], by_track[task, mode, comparator]
            a, b = primary["deployed_seconds"], control["deployed_seconds"]
            paired.append(dict(task=task, log_runtime_ratio=math.log(b / a) if a and b else None,
                               fallback_in_pair=primary["status"] == "fallback" or control["status"] == "fallback"))
        complete = all(p["log_runtime_ratio"] is not None for p in paired)
        comparisons.append(dict(mode=mode, comparator=comparator, task_pairs=paired,
                                descriptive_geometric_runtime_ratio=math.exp(statistics.fmean(p["log_runtime_ratio"] for p in paired)) if complete else None,
                                confidence_interval=None, paired_search_variance=None,
                                inference_status="insufficient-independent-search-runs" if complete else "incomplete-paired-panel"))
    return dict(schema="evolution-program-analysis-v1", claim="none", purpose="retrospective-development",
                pilot_report_sha256=audit_plan["pilot_report_sha256"], audit_report_sha256=hashlib.sha256((audit / "report.json").read_bytes()).hexdigest(),
                independent_search_runs_per_task_method=1, rows=rows, comparisons=comparisons,
                known_model_tokens=sum(r["model_tokens"] or 0 for r in rows), unknown_token_rows=sum(r["model_tokens"] is None for r in rows),
                known_search_evaluator_seconds=sum(r["search_evaluator_seconds"] or 0 for r in rows),
                unknown_evaluator_rows=sum(r["search_evaluator_seconds"] is None for r in rows),
                sample_size_plan=None, superiority_established=False,
                progress_curves=None, progress_curve_status="not-reconstructed-from-summary-only-input",
                limitations=["Fresh-process batch latency includes startup/imports/serialization, not kernel latency.",
                             "Timing repeats and different tasks cannot replace independent paired search runs.",
                             "Ratios compare selected/deployed runtimes directly, not ratios with different noisy baseline denominators.",
                             "Known cost subtotals are not complete totals when any cost is unknown; equal caps do not imply equal cost.",
                             "No numeric-protocol conversion, final registration, power guarantee or exact provider snapshot is invented.",
                             "Hash consistency is not authentication; source evidence and externally retained hashes require trusted custody."])


def markdown(value):
    lines = ["# Real program pilot analysis", "", "Claim: none. One independent search run per task/method; confidence intervals and sample-size planning withheld.", "",
             "| Task | Mode / method | Status | Before ms | Deployed ms | Tokens |", "| --- | --- | --- | ---: | ---: | ---: |"]
    for row in value["rows"]:
        fields = [row["task"], row["mode"] + " / " + row["method"], row["status"]]
        fields += ["unknown" if row[key] is None else f"{row[key] * 1000:.3f}" for key in ("original_seconds", "deployed_seconds")]
        fields.append(str(row["model_tokens"]) if row["model_tokens"] is not None else "unknown")
        lines.append("| " + " | ".join(fields) + " |")
    lines += ["", "## Descriptive comparisons, not demonstrated superiority", "",
              "Ratio is comparator runtime / Evolution runtime, geometrically averaged with equal task weights. Above 1 favors Evolution; actual costs differ.", "",
              "| Mode | Comparator | Runtime ratio | Inference |", "| --- | --- | ---: | --- |"]
    for comparison in value["comparisons"]:
        ratio = comparison["descriptive_geometric_runtime_ratio"]
        lines.append("| " + " | ".join([comparison["mode"], comparison["comparator"],
                     "unknown" if ratio is None else f"{ratio:.6f}", comparison["inference_status"]]) + " |")
    lines += ["", f"Known model-token subtotal: {value['known_model_tokens']}; unknown-cost rows: {value['unknown_token_rows']}.",
              f"Known search-evaluator seconds: {value['known_search_evaluator_seconds']:.3f}; unknown-work rows: {value['unknown_evaluator_rows']}.",
              "Progress curves are unavailable from these summary inputs; no missing trajectory is reconstructed."]
    return "\n".join(lines + ["", *["- " + text for text in value["limitations"]], ""])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("pilot", "audit", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    result = report(args.pilot, args.audit)
    args.output.mkdir(parents=True, exist_ok=False)
    write_new(args.output / "report.json", result)
    text = markdown(result)
    (args.output / "report.md").write_text(text, encoding="utf-8")
    (args.output / "report.html").write_text("<!doctype html><meta charset='utf-8'><title>Program pilot analysis</title><pre>" +
                                           html.escape(text) + "</pre>", encoding="utf-8")
    print(json.dumps({"rows": len(result["rows"]), "claim": result["claim"], "inference": "withheld"}))
