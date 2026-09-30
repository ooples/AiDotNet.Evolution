"""Checks a run-containers.sh output directory against V1-61's acceptance criteria and writes summary.json.

    python verify.py OUT

1. The multi-host run's final state hash equals the single-host run's, and one and four workers reach the same
   state in the throughput runs.
2. Every evaluation reached the engine once, and exactly one commit was accepted for each.
3. The killed worker's last lease was re-issued: another worker committed that evaluation under a new lease.
4. Four remote workers process a 1 s evaluator at least 3.5 times as fast as one.
Exits 1 when any check fails.
"""

from __future__ import annotations

import json
import pathlib
import sys

SCALING_TARGET = 3.5


def events(path: pathlib.Path) -> list[dict]:
    lines = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if line.startswith("{"):
            try:
                lines.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return lines


def main() -> int:
    out = pathlib.Path(sys.argv[1])
    single = json.loads((out / "single.json").read_text(encoding="utf-8"))
    multi = json.loads((out / "multi.json").read_text(encoding="utf-8"))
    tp1 = json.loads((out / "tp1.json").read_text(encoding="utf-8"))
    tp4 = json.loads((out / "tp4.json").read_text(encoding="utf-8"))
    checks: dict[str, dict] = {}

    checks["state_hash_matches_single_host"] = {
        "passed": multi["stateHash"] == single["stateHash"],
        "single": single["stateHash"], "multi": multi["stateHash"],
    }
    checks["state_hash_independent_of_worker_count"] = {
        "passed": tp1["stateHash"] == tp4["stateHash"],
        "one_worker": tp1["stateHash"], "four_workers": tp4["stateHash"],
    }

    worker_events = [e for name in ("w1", "w2", "w3") for e in events(out / f"multi-{name}.log")]
    accepted: dict[str, int] = {}
    for event in worker_events:
        if event.get("event") == "commit:accepted":
            key = event["identity"]["evaluationId"]
            accepted[key] = accepted.get(key, 0) + 1
    asked = multi["evaluationsAsked"]
    checks["exactly_once"] = {
        "passed": multi["resultsDelivered"] == asked and multi["resultsMissing"] == 0
        and len(accepted) == asked and all(count == 1 for count in accepted.values()),
        "asked": asked, "delivered": multi["resultsDelivered"], "missing": multi["resultsMissing"],
        "evaluations_with_an_accepted_commit": len(accepted),
        "evaluations_accepted_more_than_once": sorted(k for k, v in accepted.items() if v > 1),
    }

    killed = events(out / "multi-w1.log")
    claims = [e for e in killed if e.get("event") == "claim"]
    committed_by_killed = {e["identity"]["leaseId"] for e in killed if str(e.get("event", "")).startswith("commit")}
    reissue = {"passed": False, "reason": "the killed worker never claimed a lease"}
    if claims:
        last = claims[-1]["identity"]
        final = next((c for c in multi["committed"] if str(c["evaluationId"]) == last["evaluationId"]), None)
        reissue = {
            "passed": last["leaseId"] not in committed_by_killed and final is not None
            and final["leaseId"] != last["leaseId"] and final["provenance"] != "worker:w1",
            "killed_worker_last_claim": last,
            "final_commit": final,
        }
    checks["killed_worker_lease_reissued"] = reissue

    ratio = tp4["evaluationsPerSecond"] / tp1["evaluationsPerSecond"]
    checks["four_worker_scaling"] = {
        "passed": ratio >= SCALING_TARGET,
        "one_worker_per_second": tp1["evaluationsPerSecond"], "four_workers_per_second": tp4["evaluationsPerSecond"],
        "ratio": ratio, "target": SCALING_TARGET, "efficiency": ratio / 4,
    }

    summary = {"passed": all(c["passed"] for c in checks.values()), "checks": checks}
    (out / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    for name, check in checks.items():
        print(f"{'PASS' if check['passed'] else 'FAIL'} {name}")
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
