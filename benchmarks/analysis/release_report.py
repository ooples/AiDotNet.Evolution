"""V1-43 (#123): the published evidence report, and the lint that refuses an unsupported claim.

The story asks for a report that names benchmark, tasks, models, budget and hardware, publishes the
full bundle, and keeps losses and inconclusive results visible - and its metric is
*claims without linked evidence: 0*, measured by a report lint. The lint is the part that makes the
rest true rather than well-intentioned, so it is the centre of this module:

Every claim carries the **relative path of the file it came from**, and `lint` refuses the report
unless each of those files exists in the bundle and each claim carries a reading. A claim with no
evidence, or evidence that is not in the bundle, is a failure of the report rather than a footnote
in it. `unsupported` is the number the metrics row wants; it is 0 or `lint` raises.

`build` takes a campaign bundle (the pilot's today, the gate campaign's when #122 runs) and derives
everything from its committed files; it invents nothing and copies no number by hand.
"""
import json
import sys
from pathlib import Path

SCHEMA = "evolution-release-report-v1"
REQUIRED = ("registration.json", "report.json", "rows.json", "power-analysis.json", "summary.json")


def read(bundle, name):
    return json.loads((Path(bundle) / name).read_text(encoding="utf-8"))


def claim(text, reading, source, *, supported=True):
    """One statement, the number behind it, and the file that number came from."""
    return {"Claim": text, "Reading": reading, "Evidence": source, "Supported": supported}


def build(bundle):
    """Derives the report from the bundle's own files. Every claim names the file it came from."""
    bundle = Path(bundle)
    missing = [name for name in REQUIRED if not (bundle / name).is_file()]
    if missing:
        raise ValueError("The bundle is missing " + ", ".join(sorted(missing)))
    registration, report = read(bundle, "registration.json"), read(bundle, "report.json")
    power, summary, rows = read(bundle, "power-analysis.json"), read(bundle, "summary.json"), read(bundle, "rows.json")
    spec = registration["Spec"]
    budget = spec["Budget"]

    claims = [
        claim("The campaign is pre-registered and its registration is committed.",
              "RegistrationSha256 " + registration["RegistrationSha256"][:12], "registration.json"),
        claim("Every arm ran on the same declared per-cell budget.",
              "%d model calls, %d tokens, %d wall seconds per cell"
              % (budget["ModelCalls"], budget["ModelTokens"], budget["WallSeconds"]), "registration.json"),
        claim("The scored partition was sealed before the run.",
              "TestPartitionHashes " + json.dumps({k: v[:12] for k, v in spec["TestPartitionHashes"].items()}),
              "registration.json"),
        claim("The report regenerates from the raw rows.",
              "RawSha256 " + report["RawSha256"][:12] + " over %d rows" % len(rows), "report.json"),
        claim("The campaign's own spend is accounted per cell.",
              "%d model calls, %d cells, statuses %s"
              % (summary["model_calls"], summary["cells"], json.dumps(summary["statuses"], sort_keys=True)),
              "summary.json"),
        claim("The sizing the next campaign needs comes from this one's measured variance.",
              "required N = %s" % power.get("RequiredN", power.get("Unachievable")), "power-analysis.json"),
    ]
    if (bundle / "ranked-losses.json").is_file():
        losses = read(bundle, "ranked-losses.json")
        for endpoint in losses["Endpoints"]:
            claims.append(claim(
                "Cells lost to %s on %s are published, worst margin first." % (endpoint["Config"], endpoint["Family"]),
                "%d of %d comparable cells lost, worst margin %s"
                % (endpoint["LostCells"], endpoint["ComparablePairs"],
                   "none" if endpoint["WorstMargin"] is None else "%+.4f" % endpoint["WorstMargin"]),
                "ranked-losses.json"))

    endpoints = []
    for endpoint in report["Endpoints"]:
        # Superiority, non-superiority and inconclusiveness are all reported in the same shape, so a
        # reader cannot tell from the format alone which way an endpoint went.
        endpoints.append({
            "Family": endpoint["Family"], "Config": endpoint["Config"], "Metric": endpoint["Metric"],
            "Direction": endpoint["Direction"], "N": endpoint["N"],
            "MeanDifference": endpoint["MeanDifference"], "Interval95": endpoint["Interval95"],
            "OneSidedP": endpoint["OneSidedP"], "HolmAdjustedP": endpoint["HolmAdjustedP"],
            "Superior": endpoint["Superior"],
            "Verdict": "superior" if endpoint["Superior"] else
                       ("inconclusive" if endpoint["Interval95"][0] <= 0 <= endpoint["Interval95"][1] else "not superior"),
            "Evidence": "report.json",
        })
    return {
        "Schema": SCHEMA,
        "Campaign": spec["Campaign"],
        "Benchmark": sorted({row["Family"] for row in rows}),
        "Tasks": sorted(spec["TestPartitionHashes"]),
        "Models": list(spec["Models"]),
        "Arms": sorted({row["Arm"] for row in rows}),
        "Budget": budget,
        "Hardware": summary.get("hardware", "not recorded by this bundle"),
        "Bundle": sorted(path.name for path in bundle.iterdir() if path.is_file()),
        "Endpoints": endpoints,
        "Claims": claims,
        "Unsupported": 0,
    }


def lint(report, bundle):
    """The story's metric: claims without linked evidence, which must be zero.

    Four ways a report fails here, each of which has a plausible way of happening by accident: a
    claim with no evidence path, a path that is not in the bundle it claims to come from, a claim
    with no reading behind it, and an endpoint whose verdict disagrees with its own interval. The
    last one matters most - a report that silently promoted an inconclusive endpoint to a win would
    pass every other check in this file.
    """
    bundle = Path(bundle)
    unsupported = []
    for item in report["Claims"] + report["Endpoints"]:
        label = item.get("Claim") or "%s/%s" % (item.get("Family"), item.get("Config"))
        source = item.get("Evidence")
        if not source:
            unsupported.append(label + ": no evidence named")
        elif not (bundle / source).is_file():
            unsupported.append(label + ": evidence " + str(source) + " is not in the bundle")
        elif "Reading" in item and not str(item["Reading"]).strip():
            unsupported.append(label + ": no reading behind the claim")
    for endpoint in report["Endpoints"]:
        lower, upper = endpoint["Interval95"]
        straddles = lower <= 0 <= upper
        if endpoint["Superior"] and straddles:
            unsupported.append("%s/%s: reported superior on an interval that includes no difference"
                               % (endpoint["Family"], endpoint["Config"]))
        if endpoint["Verdict"] == "inconclusive" and not straddles:
            unsupported.append("%s/%s: called inconclusive on an interval that excludes zero"
                               % (endpoint["Family"], endpoint["Config"]))
    if report["Unsupported"] != len(unsupported):
        unsupported.append("the report records %d unsupported claims and the lint found %d"
                           % (report["Unsupported"], len(unsupported)))
    if unsupported:
        raise ValueError("Claims without linked evidence: " + "; ".join(unsupported))
    return 0


def lines(report):
    out = ["campaign %s on %s" % (report["Campaign"], ", ".join(report["Benchmark"])),
           "models %s, arms %s" % (", ".join(report["Models"]), ", ".join(report["Arms"])),
           "budget %d calls / %d tokens / %d s per cell; hardware: %s"
           % (report["Budget"]["ModelCalls"], report["Budget"]["ModelTokens"],
              report["Budget"]["WallSeconds"], report["Hardware"]),
           "bundle: " + ", ".join(report["Bundle"])]
    for endpoint in report["Endpoints"]:
        out.append("%-22s %-12s %-13s mean %+.4f CI95 [%+.4f, %+.4f] p=%.4g holm=%.4g"
                   % (endpoint["Family"], endpoint["Config"], endpoint["Verdict"], endpoint["MeanDifference"],
                      endpoint["Interval95"][0], endpoint["Interval95"][1], endpoint["OneSidedP"],
                      endpoint["HolmAdjustedP"]))
    for item in report["Claims"]:
        out.append("  %-72s %s [%s]" % (item["Claim"], item["Reading"], item["Evidence"]))
    out.append("claims without linked evidence: %d" % report["Unsupported"])
    return out


def render(report):
    return json.dumps(report, indent=1) + "\n"


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: release_report.py <bundle-directory>")
    report = build(sys.argv[1])
    lint(report, sys.argv[1])
    print("\n".join(lines(report)))
    return report


if __name__ == "__main__":
    main()
