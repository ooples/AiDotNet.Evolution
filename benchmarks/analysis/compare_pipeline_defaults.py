"""Descriptive before/after default-mode profiles; sequential source groups are not a causal timing experiment."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import statistics
import zipfile

from analyze import load_json, require
from analyze_pipeline import equivalent


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
        parser.add_argument("--" + name, type=Path)
    parser.add_argument("--before-source")
    parser.add_argument("--after-source")
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--verify-comparison", type=Path)
    args = parser.parse_args()
    if args.verify_comparison is not None:
        require(args.archive is not None and args.before is None and args.after is None and args.output is None, "Verification requires only an archive and comparison file.")
        verify_archive(args.archive, args.verify_comparison)
        print("Verified default-profile archive hash chain and recomputed 132 paired attempts.")
        return
    require(all(value is not None for value in (args.before, args.after, args.output, args.before_source, args.after_source)), "Supply both reports/source pins and a new output file.")
    before, before_hash = load_json(args.before, 64 * 1024 * 1024)
    after, after_hash = load_json(args.after, 64 * 1024 * 1024)
    report = compare(before, after, args.before_source, args.after_source)
    report.update(BeforeReportSha256=before_hash, AfterReportSha256=after_hash)
    if args.archive is not None:
        require(args.archive.stat().st_size <= 64 * 1024 * 1024, "Profile archive exceeds its bound.")
        report.update(ProfilesZipSha256=hashlib.sha256(args.archive.read_bytes()).hexdigest(),
                      BeforeReportMember=args.before.parent.name + "/" + args.before.name,
                      AfterReportMember=args.after.parent.name + "/" + args.after.name)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(report, output, indent=2, allow_nan=False); output.write("\n")
    print("Compared 132 before + 132 after attempts: default state, quality and logical work are identical.")


def verify_archive(archive_path, comparison_path):
    report, _ = load_json(comparison_path, 1024 * 1024)
    require(archive_path.stat().st_size <= 64 * 1024 * 1024, "Profile archive exceeds its bound.")
    require(hashlib.sha256(archive_path.read_bytes()).hexdigest() == report["ProfilesZipSha256"], "Profile archive hash mismatch.")
    profiles = []
    with zipfile.ZipFile(archive_path) as archive:
        for prefix in ("Before", "After"):
            name = report[prefix + "ReportMember"]
            require(re.fullmatch(r"[A-Za-z0-9_-]+/report.json", name) is not None, "Invalid report member identity.")
            require(sum(member.filename == name for member in archive.infolist()) == 1, "Missing/duplicate report member.")
            require(archive.getinfo(name).file_size <= 64 * 1024 * 1024, "Expanded profile exceeds its bound.")
            raw = archive.read(name)
            require(hashlib.sha256(raw).hexdigest() == report[prefix + "ReportSha256"], "Retained profile hash mismatch.")
            profiles.append(json.loads(raw))
    recomputed = compare(*profiles, report["BeforeSource"], report["AfterSource"])
    metadata = {"BeforeReportSha256", "AfterReportSha256", "ProfilesZipSha256", "BeforeReportMember", "AfterReportMember"}
    require(equivalent(recomputed, {k: v for k, v in report.items() if k not in metadata}), "Default comparison differs from retained profiles.")
    return report


if __name__ == "__main__":
    main()
