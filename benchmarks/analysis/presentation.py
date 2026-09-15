"""Dependency-free, escaped presentation; missing measurements never become zeros."""
import html
import math
import statistics


def scorecard(rows, plan):
    result = []
    for task in plan["Tasks"]:
        for method in plan["Methods"]:
            group = [r for r in rows if r["Task"] == task["Name"] and r["Method"] == method]
            valid = [r for r in group if r["Status"] == "completed"]
            areas = []
            for row in valid:
                if not row["TrajectoryComplete"] or not row["Curve"]:
                    continue
                # Left-continuous incumbent integral: no quality exists before
                # the first measurement; absent trajectories remain unknown.
                area, previous_cost, utility = 0.0, 0, 0.0
                for point in row["Curve"]:
                    area += (point["CostUnits"] - previous_cost) * utility
                    previous_cost = point["CostUnits"]
                    utility = task["Scale"] / (task["Scale"] + point["BestLoss"])
                if previous_cost == plan["Budget"] or row.get("TerminalCarryForward"):
                    area += (plan["Budget"] - previous_cost) * utility
                    areas.append(area / plan["Budget"])
            known_resources = [r["Resources"]["Spent"] for r in group if r["Resources"] is not None]
            resource_names = sorted({k for values in known_resources for k in values})
            target = task.get("TargetLoss")
            observations = []
            if target is not None:
                for row in group:
                    known = row["Status"] == "completed" and row["TrajectoryComplete"]
                    hits = [p for p in row["Curve"] if p["BestLoss"] <= target] if known else []
                    observations.append(dict(Seed=row["Seed"], Hit=bool(hits), Unknown=not known,
                                             CostUnits=hits[0]["CostUnits"] if hits else plan["Budget"],
                                             Censored=not bool(hits)))
            result.append(dict(Task=task["Name"], Method=method, Scheduled=len(group),
                               AccountedCompletionRate=len(valid) / len(group),
                               CorrectnessPassRate=None, WorstNumericalError=None, RuntimeSpeedup=None,
                               PeakMemoryBytes=None, TargetLoss=target,
                               TargetHitRate=sum(r["Hit"] for r in observations) / len(group) if observations else None,
                               UnknownTargetRuns=sum(r["Unknown"] for r in observations) if observations else None,
                               TimeToTarget=None, TargetCostObservations=observations,
                               ArchiveCoverage=None, NormalizedQDScore=None,
                               MeanUtilityAucKnownOnly=statistics.fmean(areas) if areas else None,
                               AucKnownRuns=len(areas), AucUnknownRuns=len(group) - len(areas),
                               KnownResourceTotals={k: math.fsum(v[k] for v in known_resources if k in v)
                                                    for k in resource_names},
                               ResourceUnknownRuns={k: sum(r["Resources"] is None or k not in r["Resources"]["Spent"]
                                                           or r["Resources"].get("Unknown", 0) > 0
                                                           or any(r["Resources"].get("Reserved", {}).values()) for r in group)
                                                    for k in resource_names},
                               UnavailableReason="No independent correctness/runtime/memory/common-grid measurements; targets require schema-v2 declaration. Completion is not correctness."))
    return result


def markdown_details(report):
    def number(value):
        return "unknown" if value is None else format(value, ".6g")
    lines = ["", "## Measurement scorecard", "",
             "Known work totals are lower bounds when work is unknown. Completion is not independent correctness.", "",
             "| Task | Method | Accounted completion | Known-only utility AUC | AUC known / scheduled |",
             "| --- | --- | ---: | ---: | ---: |"]
    for row in report["Scorecard"]:
        lines.append(f"| {row['Task']} | {row['Method']} | {number(row['AccountedCompletionRate'])} | {number(row['MeanUtilityAucKnownOnly'])} | {row['AucKnownRuns']} / {row['Scheduled']} |")
    lines += ["", "Correctness pass rate, numerical error, runtime speedup, memory, undeclared targets,",
              "common-grid coverage and normalized QD score are **unknown**, not zero or inferred from completion.",
              "Actual available resource totals and their unknown-run counts are retained in JSON.",
              "Declared target rates include all scheduled runs; non-hits are censored at the budget with unknown trajectories flagged separately.",
              "", "## Budget-indexed progress", "", "Known-only medians; missing curves are not interpolated.", "",
              "| Task | Method | Cost units | Known / scheduled | Median loss |", "| --- | --- | ---: | ---: | ---: |"]
    for row in report["Progress"]:
        lines.append(f"| {row['Task']} | {row['Method']} | {row['CostUnits']} | {row['KnownRuns']} / {row['KnownRuns'] + row['MissingRuns']} | {number(row['MedianLossKnownOnly'])} |")
    lines += ["", "## Paired win / tie / loss counts", "", "Exact utility differences; failures remain paired.", ""]
    for row in report["Comparisons"]:
        counts = row["WinTieLoss"]
        lines.append(f"- {row['Primary']} versus {row['Comparator']}: {counts['Wins']} / {counts['Ties']} / {counts['Losses']}.")
    if "FixedDesign" in report:
        lines += ["", "## Locked fixed-sample analysis", "", "Registration: `" + report["FixedDesign"]["RegistrationSha256"] + "`.",
                  "No sample extension is permitted. One-sided Hoeffding lower bounds below are separate from descriptive bootstrap intervals; no release promotion follows.", ""]
        for row in report["FixedDesign"]["Comparisons"]:
            lines.append(f"- {row['Comparator']}: lower bound {number(row['LowerBound'])}; above zero: {row['AboveZero']}.")
    return lines


def html_report(report):
    # Escaping the complete Markdown also escapes raw diagnostics. No CDN,
    # JavaScript, remote resources, or untrusted HTML execution is needed.
    from analyze import markdown
    curves = []
    for summary in report["Summaries"]:
        rows = [r for r in report["Progress"] if r["Task"] == summary["Task"] and r["Method"] == summary["Method"]]
        known = [r for r in rows if r["MedianLossKnownOnly"] is not None]
        label = html.escape(summary["Task"] + " / " + summary["Method"])
        if len(known) < 2:
            curves.append(f"<p>{label}: insufficient known points for a curve.</p>")
            continue
        xmax = max(r["CostUnits"] for r in rows)
        ymax = max(r["MedianLossKnownOnly"] for r in known) or 1
        segments, current = [], []
        for row in rows:
            if row["MedianLossKnownOnly"] is None:
                if current:
                    segments.append(current)
                    current = []
                continue
            current.append(f"{20 + 360 * row['CostUnits'] / xmax:.3f},{180 - 160 * (row['MedianLossKnownOnly'] / ymax):.3f}")
        if current:
            segments.append(current)
        geometry = "".join('<polyline fill="none" stroke="currentColor" points="' + " ".join(segment) + '"/>' for segment in segments)
        curves.append(f'<figure><figcaption>{label}: known-only median loss versus cost units</figcaption><svg role="img" aria-label="{label}" viewBox="0 0 400 200">{geometry}</svg></figure>')
    return ('<!doctype html><html lang="en"><meta charset="utf-8"><title>Experiment report</title>'
            '<meta http-equiv="Content-Security-Policy" content="default-src \'none\'; style-src \'unsafe-inline\'">'
            '<style>body{max-width:1000px;margin:2em auto;font-family:system-ui}pre{white-space:pre-wrap}svg{width:400px;max-width:100%}</style>'
            '<body><h1>Experiment report</h1>' + "".join(curves) + '<pre>' + html.escape(markdown(report)) + '</pre></body></html>')
