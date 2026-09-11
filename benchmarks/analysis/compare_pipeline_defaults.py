"""Descriptive before/after default-mode profiles; sequential source groups are not a causal timing experiment."""
import argparse
import json
from pathlib import Path
import re
import statistics

from analyze import load_json, require


def compare(before, after, before_source, after_source):
    for report, source in ((before, before_source), (after, after_source)):
        require(re.fullmatch(r"[0-9a-f]{40}", source) is not None and report["sourceRevision"] == source, "Source pin mismatch.")
        require(report["protocol"] == "engine-profile-v1" and report["status"] == "passed" and report["smoke"] is False,
                "A full successful profile is required; do not omit failed attempts.")
        require(report["repetitions"] == 3 and len(report["cases"]) == 44 and len(report["attempts"]) == 132, "Profile plan is incomplete.")
        require(len({case["id"] for case in report["cases"]}) == 44, "Repeated case identity.")
        expected = {(case["id"], repeat) for case in report["cases"] for repeat in range(3)}
        actual = {(attempt["caseId"], attempt["repetition"]) for attempt in report["attempts"]}
        require(actual == expected and all(attempt["status"] == "passed" and attempt["measurement"] is not None for attempt in report["attempts"]),
                "Failed, missing or substituted profile attempt.")
    require(before["cases"] == after["cases"] and before["affinityHex"] == after["affinityHex"], "Profile factors or affinity differ.")
    rows = []
    for case in before["cases"]:
        left = sorted((a for a in before["attempts"] if a["caseId"] == case["id"]), key=lambda a: a["repetition"])
        right = sorted((a for a in after["attempts"] if a["caseId"] == case["id"]), key=lambda a: a["repetition"])
        for a, b in zip(left, right):
            ma, mb = a["measurement"], b["measurement"]
            require(ma["case"] == mb["case"] == case and ma["environment"] == mb["environment"], "Measured case or runtime controls differ.")
            require(all(ma[field] == mb[field] for field in ("operations", "evaluationCalls", "occupiedCells", "stateHash", "bestQuality")),
                    "Default-mode logical work or quality changed across source revisions.")
        median = lambda group, field: statistics.median(a["measurement"][field] for a in group)
        old_time, new_time = median(left, "elapsedMilliseconds"), median(right, "elapsedMilliseconds")
        old_alloc, new_alloc = median(left, "managedAllocatedBytes"), median(right, "managedAllocatedBytes")
        rows.append(dict(CaseId=case["id"], Kind=case["kind"], Repetitions=3, StateQualityAndWorkIdentical=True,
                         BeforeMedianMilliseconds=old_time, AfterMedianMilliseconds=new_time,
                         DescriptiveElapsedRatioAfterOverBefore=new_time / old_time,
                         BeforeMedianAllocatedBytes=old_alloc, AfterMedianAllocatedBytes=new_alloc,
                         AllocationDifferenceBytes=new_alloc - old_alloc, AllocationRatioAfterOverBefore=new_alloc / old_alloc if old_alloc else None))
    return dict(Protocol="pipeline-default-regression-v1", BeforeSource=before_source, AfterSource=after_source,
                AttemptsPerSource=132, AllStateQualityAndWorkIdentical=True, Comparisons=rows, ConfirmatoryEligible=False,
                Interpretation="Same predeclared 44 cases, three fresh-process repetitions, exact CPU-group/affinity/runtime controls. "
                "Source groups ran sequentially on a shared host, not randomized interleaved builds. "
                "Elapsed ratios are descriptive and cannot establish a causal speedup/regression. "
                "All failures must be retained; a failed or omitted attempt prevents this comparison. "
                "Allocation deltas describe whole measured operations, including task/runtime/observer/checkpoint costs, not only proposal wrappers.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("before", "after", "output"):
        parser.add_argument("--" + name, required=True, type=Path)
    parser.add_argument("--before-source", required=True)
    parser.add_argument("--after-source", required=True)
    args = parser.parse_args()
    before, before_hash = load_json(args.before, 64 * 1024 * 1024)
    after, after_hash = load_json(args.after, 64 * 1024 * 1024)
    report = compare(before, after, args.before_source, args.after_source)
    report.update(BeforeReportSha256=before_hash, AfterReportSha256=after_hash)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(report, output, indent=2, allow_nan=False); output.write("\n")
    print("Compared 132 before + 132 after attempts: default state, quality and logical work are identical.")


if __name__ == "__main__":
    main()
