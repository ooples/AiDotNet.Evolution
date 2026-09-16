"""Compare two complete controlled campaigns; descriptive ratios, not statistical speedup claims."""
import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path
import statistics


def require(value, message):
    if not value:
        raise ValueError(message)


def compare(before, after):
    for report in (before, after):
        require(report["status"] == "passed" and not report["smoke"], "A full passing campaign is required.")
        require(report["workingTreeStatus"] == "clean", "Dirty source provenance.")
    require(before["cases"] == after["cases"] and before["repetitions"] == after["repetitions"], "Unpaired campaign plans.")
    require(before["affinityHex"] == after["affinityHex"] and before["pinnedTopology"] == after["pinnedTopology"], "Changed CPU controls.")
    environments = [attempt["measurement"]["environment"] for report in (before, after) for attempt in report["attempts"] if attempt["status"] == "passed"]
    require(environments and all(value == environments[0] for value in environments), "Changed runtime/host controls.")
    rows = []
    for case in before["cases"]:
        groups = []
        for report in (before, after):
            attempts = [attempt for attempt in report["attempts"] if attempt["caseId"] == case["id"] and attempt["status"] == "passed"]
            require(sorted(attempt["repetition"] for attempt in attempts) == list(range(report["repetitions"])), "Missing or duplicate paired repetitions.")
            measurements = [attempt["measurement"] for attempt in attempts]
            for measurement in measurements:
                require(measurement["case"] == case and measurement["operations"] > 0 and measurement["iterations"] > 0, "Incorrect measured case/work.")
                for field in ("elapsedMilliseconds", "operationsPerSecond", "managedAllocatedBytes"):
                    require(math.isfinite(measurement[field]) and measurement[field] >= 0, "Invalid measurement.")
                require(measurement["elapsedMilliseconds"] >= 250 and math.isclose(measurement["operationsPerSecond"], measurement["operations"] * 1000 / measurement["elapsedMilliseconds"], rel_tol=1e-6), "Invalid throughput.")
            groups.append(measurements)
        if case["kind"] in ("engine", "checkpoint-restore"):
            require(len({row["stateHash"] for group in groups for row in group}) == 1 and groups[0][0]["stateHash"], "Search state changed across revisions.")
        if case["kind"] == "engine":
            require(len({row["bestQuality"] for group in groups for row in group}) == 1, "Search quality changed across revisions.")
        metrics = []
        for group in groups:
            metrics.append(dict(medianMillisecondsPerOperation=statistics.median(row["elapsedMilliseconds"] / row["operations"] for row in group),
                                medianAllocatedBytesPerOperation=statistics.median(row["managedAllocatedBytes"] / row["operations"] for row in group),
                                medianOperationsPerSecond=statistics.median(row["operationsPerSecond"] for row in group),
                                maximumLifetimePeakBytes=max(row["processLifetimePeakWorkingSetBytes"] for row in group)))
        left, right = metrics
        rows.append(dict(caseId=case["id"], before=left, after=right,
                         elapsedReductionPercent=100 * (1 - right["medianMillisecondsPerOperation"] / left["medianMillisecondsPerOperation"]),
                         allocationReductionPercent=100 * (1 - right["medianAllocatedBytesPerOperation"] / left["medianAllocatedBytesPerOperation"])))
    return dict(beforeRevision=before["sourceRevision"], afterRevision=after["sourceRevision"], rows=rows,
                interpretation="Sequential before/after campaigns on one host, three fresh-process repetitions per case. Descriptive medians only; not randomized cross-revision timing, statistical superiority, optimizer-quality improvement or competitor evidence. All regressions retained.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    reports, hashes = [], []
    for path in (args.before, args.after):
        content = path.read_bytes()
        if path.suffix == ".gz":
            content = gzip.decompress(content)
        hashes.append(hashlib.sha256(content).hexdigest())
        reports.append(json.loads(content))
    result = compare(*reports)
    result.update(beforeRawSha256=hashes[0], afterRawSha256=hashes[1])
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)
        stream.write("\n")


if __name__ == "__main__":
    main()
