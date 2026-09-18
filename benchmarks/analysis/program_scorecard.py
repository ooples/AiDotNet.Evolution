"""Read-only descriptive scorecard for a stopped, registered program study.

Adds observed-domain AUC and explicit unavailable metrics without changing the
registered endpoint, sample size, confidence procedure or original evidence.
"""
import argparse
import hashlib
import html
import json
from pathlib import Path

from analyze import require
from design import digest, write_new
from program_report import indexed, read, report
from program_study import render as study_render, sample_advice, summarize, trajectory, validate_registration

AXES = ("model_tokens", "evaluation_attempts", "evaluator_seconds", "elapsed_seconds")


def progress_metrics(curve):
    if not curve.get("complete") or curve.get("unknown_work"):
        return dict(status="unavailable", reason=curve.get("reason") or "unknown-work",
                    points=None, auc=None, target=None)
    points, evaluations = [], 0
    for point in curve["points"]:
        evaluations += point["operation"] == "evaluate"
        points.append({**point, "evaluation_attempts": evaluations})
    known = [p for p in points if p["best_search_utility"] is not None]
    auc = {}
    for axis in AXES:
        if not known:
            auc[axis] = None
            continue
        start, end = known[0][axis], known[-1][axis]
        area = sum((b[axis] - a[axis]) * a["best_search_utility"] for a, b in zip(known, known[1:]))
        auc[axis] = dict(start=start, end=end, area=area,
                         mean_observed_utility=area / (end - start) if end > start else None)
    hit = next((p for p in known if p["best_search_utility"] >= 0.5), None)
    return dict(status="complete", reason=None, points=points, auc=auc,
                target=dict(threshold=0.5, status="hit" if hit else "not-hit",
                            at={axis: hit[axis] for axis in AXES} if hit else None,
                            observed_end={axis: points[-1][axis] for axis in AXES} if points else None),
                interpretation="Search-only left-step AUC over the observed valid domain; no imputation before first valid observation or after stopping. Unequal/censored domains are not fixed-budget efficacy comparisons.")


def build(root):
    root = Path(root)
    study, plan = read(root / "study.json"), read(root / "registration.json")
    validate_registration(plan)
    require(study.get("schema") == "evolution-program-study-v1" and
            study.get("status") in ("completed", "failed") and study.get("claim") == "none" and
            study.get("superiority_established") is False and study["registration_sha256"] == digest(plan),
            "Require a stopped registered study with no superiority claim.")
    blocks = indexed(study["blocks"], lambda b: b["id"])
    require(set(blocks).issubset(b["id"] for b in plan["schedule"]), "Unplanned study block.")
    for phase in ("calibration", "confirmation"):
        require(study[phase] == summarize(plan, study["blocks"], phase), "Registered phase summary changed.")
    rows, unchanged = [], True
    for block in plan["schedule"]:
        original = blocks.get(block["id"])
        evidence, checks, raw_runs = {}, {}, {}
        if original is not None:
            require(read(root / (block["id"] + "-analysis.json")) == original, "Changed frozen block evidence.")
            pilot, audit = root / block["id"], root / (block["id"] + "-audit")
            analyzed = report(pilot, audit)
            require(all(r["search_seed"] == block["seed"] for r in analyzed["rows"]), "Pilot search seed differs from registered block.")
            evidence = indexed(analyzed["rows"], lambda r: (r["task"], r["mode"], r["method"]))
            checks = indexed(read(audit / "report.json")["runs"], lambda r: (r["task"], r["mode"], r["method"]))
            for task in block["tasks"]:
                for run in read(pilot / task / "comparison.json")["runs"]:
                    key = (task, run["mode"], run["method"])
                    require(key not in raw_runs, "Duplicate raw run.")
                    raw_runs[key] = run
            for old in original["rows"]:
                current = evidence[old["task"], old["mode"], old["method"]]
                for key in ("status", "original_seconds", "deployed_seconds", "speedup", "model_tokens", "search_evaluator_seconds"):
                    unchanged = unchanged and current[key] == old[key]
        phase = study[block["phase"]]
        for scheduled in (r for r in phase["rows"] if r["block"] == block["id"]):
            key = (scheduled["task"], scheduled["mode"], scheduled["method"])
            row = evidence.get(key, scheduled)
            raw = raw_runs.get(key, {})
            curve = trajectory(raw, plan["scales_seconds"][scheduled["task"]])
            counters = curve.get("counters")
            audit = checks.get(key)
            receipts = raw.get("receipts", [])
            evaluated = [r.get("result", {}) for r in receipts if r.get("operation") == "evaluate"]
            hashes = [r["candidate_hash"] for r in evaluated if isinstance(r, dict) and "candidate_hash" in r]
            rows.append(dict(block=block["id"], phase=block["phase"], search_seed=block["seed"], **dict(zip(("task", "mode", "method"), key)),
                             status=row["status"], original_seconds=row.get("original_seconds"), deployed_seconds=row.get("deployed_seconds"),
                             speedup=row.get("speedup"), audited_correct=audit["valid"] if audit else None,
                             independent_counters=counters, progress=progress_metrics(curve),
                             known_duplicate_evaluations=len(hashes) - len(set(hashes)) if curve["complete"] else None,
                             nonvalid_evaluation_receipts=sum(not isinstance(r, dict) or r.get("status") != "valid" for r in evaluated) if curve["complete"] else None))
    require(len(rows) == sum(study[p]["scheduled_rows"] for p in ("calibration", "confirmation")), "Scheduled scorecard grid changed.")
    summary = []
    for phase in ("calibration", "confirmation"):
        group = [r for r in rows if r["phase"] == phase]
        summary.append(dict(phase=phase, scheduled=len(group), audit_pass=sum(r["audited_correct"] is True for r in group),
                            audit_fail=sum(r["audited_correct"] is False for r in group), audit_unknown=sum(r["audited_correct"] is None for r in group),
                            search_target_hits=sum((r["progress"].get("target") or {}).get("status") == "hit" for r in group),
                            search_target_unknown=sum(r["progress"]["status"] != "complete" for r in group),
                            incomplete_or_unknown_trajectories=sum(r["progress"]["status"] != "complete" for r in group)))
    return dict(schema="evolution-program-scorecard-v1", claim="none", reporting_scope="post-run descriptive expansion; original registered estimates unchanged",
                original_study_sha256=hashlib.sha256((root / "study.json").read_bytes()).hexdigest(),
                registration_sha256=study["registration_sha256"], frozen_source_revision=plan["source_revision"],
                original_row_costs_and_outcomes_unchanged=unchanged, rows=rows, summary=summary, resources=study["resources"],
                frozen_sample_advice=read(root / "sample-advice.json") if (root / "sample-advice.json").exists() else None,
                prospective_sample_advice=sample_advice(plan, study["calibration"]),
                unavailable=dict(peak_memory_bytes="not measured; sandbox limit is not peak use",
                                 kernel_and_startup_separate_seconds="only complete fresh-process batch latency was measured",
                                 cpu_gpu_seconds="supervisor wall work is not CPU/GPU time",
                                 money="subscription entitlement used; no API-key calls; subscription allocation cost unmeasured",
                                 archive_coverage_and_qd="no shared fixed reporting grid for these program controllers",
                                 cache_hits_and_retries="not independently instrumented across all controllers",
                                 numerical_error="exact byte/graph contracts, not a floating-point error endpoint",
                                 replay="artifacts retained; live provider responses and hardware timing are not deterministic"))


def render(value, study):
    original, _ = study_render(study)
    lines = ["Frozen registered report follows; independently reconciled costs appear in the expanded scorecard below.",
             f"Original row costs and outcomes unchanged: {value.get('original_row_costs_and_outcomes_unchanged', 'not evaluated')}. If false, treat frozen cost summaries as historical, not current complete totals.", "",
             original, "## Expanded descriptive scorecard", "",
             "Added after the registered run; no endpoint, CI, seed, method or sample-size changes.",
             "AUC uses only the observed valid domain. It is not a fixed-budget ranking; no gain or missing work is imputed.", "",
             "| Phase | Scheduled | Audit pass / fail / unknown | Search target hits / scheduled | Unknown targets/curves |",
             "| --- | ---: | --- | --- | ---: |"]
    for row in value["summary"]:
        lines.append(f"| {row['phase']} | {row['scheduled']} | {row['audit_pass']} / {row['audit_fail']} / {row['audit_unknown']} | {row['search_target_hits']} / {row['scheduled']} | {row['search_target_unknown']} |")
    if value.get("prospective_sample_advice"):
        old, current = value.get("frozen_sample_advice"), value["prospective_sample_advice"]
        lines += ["", "### Prospective power advice, not a change to this experiment", "",
                  f"Frozen advice: {old['conservative_powered_runs'] if old else 'unavailable'} runs. Current sufficient bound: {current['conservative_powered_runs']} runs per task/method. Fixed confirmation count: {current['fixed_confirmation_runs']}.",
                  "The current adviser matches the displayed two-sided interval. The original advice and precommitted run count remain in the archive; no additional samples or retrospective release claim follow from this correction."]
    lines += ["", "### Unmeasured or inapplicable scorecard fields", ""]
    lines += [f"- {key}: {reason}." for key, reason in value["unavailable"].items()]
    lines += ["", "### Deployed runtime and independent cost counters", "",
              "Before/deployed are fresh-process batch milliseconds, not isolated kernel timings. Counters retain known subtotals; unknown attempts remain explicit.", "",
              "| Block / task / mode / method | Before ms | Deployed ms | Speedup | Model calls / tokens | Evaluations / work s | Unknown model / evaluation attempts |",
              "| --- | ---: | ---: | ---: | --- | --- | --- |"]
    for row in value["rows"]:
        label = " / ".join(row[k] for k in ("block", "task", "mode", "method"))
        before, after, ratio = ["unknown" if row.get(k) is None else f"{row[k] * factor:.6f}"
                                for k, factor in (("original_seconds", 1000), ("deployed_seconds", 1000), ("speedup", 1))]
        counters = row.get("independent_counters")
        model = f"{counters['attempted']['model']} / {counters['model_tokens']}" if counters else "unknown"
        work = f"{counters['attempted']['evaluate']} / {counters['evaluation_seconds']:.6f}" if counters else "unknown"
        unknown = f"{counters['unknown']['model']} / {counters['unknown']['evaluate']}" if counters else "unknown"
        lines.append(f"| {label} | {before} | {after} | {ratio} | {model} | {work} | {unknown} |")
    lines += ["", "### Per-run observed-domain progress", "",
              "Four axes: model tokens, evaluation attempts, evaluator work seconds, broker elapsed seconds. Full domains, AUCs and target costs are in JSON; all four curves appear in HTML.", "",
              "| Block / task / mode / method | Trace | Target | Mean observed utility by four axes |",
              "| --- | --- | --- | --- |"]
    figures = []
    for row in value["rows"]:
        label = " / ".join(row[k] for k in ("block", "task", "mode", "method"))
        progress = row["progress"]
        target = (progress.get("target") or {}).get("status", "unknown")
        means = ["unknown" if not progress.get("auc") or progress["auc"][axis] is None or progress["auc"][axis]["mean_observed_utility"] is None
                 else f"{progress['auc'][axis]['mean_observed_utility']:.6f}" for axis in AXES]
        lines.append(f"| {label} | {progress['status']} | {target} | {' / '.join(means)} |")
        if progress["status"] != "complete":
            continue
        points = [p for p in progress["points"] if p["best_search_utility"] is not None]
        for axis in AXES:
            domain = progress["auc"][axis]
            if domain is None or domain["end"] <= domain["start"]:
                continue
            segments = []
            for index, point in enumerate(points):
                x = 20 + 300 * (point[axis] - domain["start"]) / (domain["end"] - domain["start"])
                y = 120 - 100 * point["best_search_utility"]
                segments.append(f"M{x:.3f},{y:.3f}" if index == 0 else f"H{x:.3f} V{y:.3f}")
            caption = f"{label}: {axis}, observed {domain['start']:.3f} to {domain['end']:.3f}; utility 0 to 1"
            figures.append("<figure><figcaption>" + html.escape(caption) + "</figcaption><svg viewBox='0 0 340 145' width='340' role='img'><title>" +
                           html.escape(caption) + "</title><path d='M20,20 V120 H320' fill='none' stroke='gray'/><path d='" +
                           " ".join(segments) + "' fill='none' stroke='blue'/></svg></figure>")
    markdown = "\n".join(lines) + "\n"
    return markdown, "<!doctype html><meta charset='utf-8'><title>Program study scorecard</title><pre>" + html.escape(markdown) + "</pre>" + "".join(figures)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    value = build(args.root)
    args.output.mkdir(parents=True, exist_ok=False)
    write_new(args.output / "scorecard.json", value)
    markdown, webpage = render(value, read(args.root / "study.json"))
    (args.output / "scorecard.md").write_text(markdown, encoding="utf-8")
    (args.output / "scorecard.html").write_text(webpage, encoding="utf-8")
    print(json.dumps(dict(rows=len(value["rows"]), original_costs_and_outcomes_unchanged=value["original_row_costs_and_outcomes_unchanged"])))
