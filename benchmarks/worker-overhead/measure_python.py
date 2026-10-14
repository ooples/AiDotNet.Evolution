"""Measures the Python reference worker's overhead per evaluation against the real durable host (V1-25, #119).

Overhead is everything ``serve`` spends on one lease except the evaluator itself: the claim round trip, unwrapping
the engine envelope, encoding the receipt and the commit round trip (which includes the host's durable journal
write). Each lease is served by one ``serve(..., max_leases=1)`` call, the unmodified code path a deployment runs,
and the evaluator's own time, measured inside it, is subtracted from that call's wall time.

usage: measure_python.py --host <executable-or-dotnet> [--host-arg <dll>] [--evaluations N] [--warmup W]
                         [--gate-p95-ms MS] [--output <new.json>]

The JSON report goes to stdout (and to --output, which must not exist). With --gate-p95-ms the exit code is 1 when
the measured p95 is not below the target; without it the report is evidence only.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import platform
import sys
import tempfile
import time

if os.environ.get("AIDOTNET_PYTHON_INSTALLED") != "1":
    sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "bindings" / "python"))
from aidotnet_evolution import DurableWorkClient, serve  # noqa: E402

SCHEMA = "aidotnet-worker-overhead-v1"
MAXIMUM_WORK_ITEMS = 1024  # EvolutionWorkCoordinatorOptions' default retained work-item limit


def percentile(sorted_values: list[float], fraction: float) -> float:
    """Nearest-rank percentile: the smallest value with at least ``fraction`` of the samples at or below it."""
    if not sorted_values:
        raise ValueError("no samples")
    rank = max(1, -(-len(sorted_values) * fraction // 1))  # ceil without float rounding surprises
    return sorted_values[int(rank) - 1]


def summarize(samples_ns: list[int]) -> dict[str, float]:
    ordered = sorted(value / 1e6 for value in samples_ns)
    return {"count": len(ordered), "min_ms": ordered[0], "p50_ms": percentile(ordered, 0.50),
            "p95_ms": percentile(ordered, 0.95), "p99_ms": percentile(ordered, 0.99), "max_ms": ordered[-1],
            "mean_ms": sum(ordered) / len(ordered)}


def envelope(evaluation_id: str, source: str) -> str:
    """The payload the engine bridge enqueues, so the default ``engine_program_source`` path is the one timed."""
    return json.dumps({"schema": 1, "genomePayload": source, "canonicalGenomeId": "program:" + evaluation_id,
                       "evaluationId": evaluation_id, "attempt": 1, "rootSeed": "7", "seedStream": "0"})


def measure(host: str, host_args: list[str], evaluations: int, warmup: int) -> dict[str, object]:
    total = evaluations + warmup
    evaluator_ns: list[int] = []

    def evaluate(source: str) -> dict[str, object]:
        started = time.perf_counter_ns()
        result = {"metrics": {"combined_score": float(len(source))}, "artifacts": {}}
        evaluator_ns.append(time.perf_counter_ns() - started)
        return result

    overhead_ns: list[int] = []
    round_trip_ns: list[int] = []
    with tempfile.TemporaryDirectory(prefix="worker-overhead-") as scratch:
        config = {"directory": os.path.join(scratch, "work"), "runId": "overhead", "compatibilityHash": "overhead-v1",
                  "limits": {"evaluations": str(total)}}
        worker = {"workerId": "overhead-1", "compatibilityHash": "overhead-v1"}
        with DurableWorkClient(config, host_path=host, host_args=host_args) as work:
            for index in range(1, total + 1):
                if not work.enqueue({"evaluationId": str(index), "attempt": 1, "canonicalGenomeId": f"program:{index}",
                                     "payload": envelope(str(index), f"def run():\n    return {index}\n"),
                                     "estimated": {"evaluations": "1"}, "maximum": {"evaluations": "1"}}):
                    raise RuntimeError(f"job {index} was not enqueued")
            for index in range(total):
                before = len(evaluator_ns)
                started = time.perf_counter_ns()
                dispositions = serve(work, worker, evaluate, provenance="overhead-v1", actual={"evaluations": "1"}, max_leases=1)
                elapsed = time.perf_counter_ns() - started
                if dispositions != ["accepted"] or len(evaluator_ns) != before + 1:
                    raise RuntimeError(f"lease {index + 1} was not evaluated and accepted exactly once: {dispositions}")
                # A read-only request: the IPC round trip alone, without the durable state write a claim or commit makes.
                started = time.perf_counter_ns()
                work.status()
                round_trip = time.perf_counter_ns() - started
                if index >= warmup:
                    overhead_ns.append(elapsed - evaluator_ns[-1])
                    round_trip_ns.append(round_trip)
            if serve(work, worker, evaluate, provenance="overhead-v1", actual={"evaluations": "1"}) != []:
                raise RuntimeError("work remained after every enqueued lease was served")
            status = work.status()
            if status["settled"] != str(total):
                raise RuntimeError(f"the host settled {status['settled']} of {total} leases")
    tenth = max(1, len(overhead_ns) // 10)
    return {"overhead": summarize(overhead_ns), "evaluator": summarize(evaluator_ns[warmup:]),
            "ipc_round_trip": summarize(round_trip_ns),
            # The host rewrites its whole durable state on every claim and commit, so overhead grows with retained work.
            "overhead_first_tenth": summarize(overhead_ns[:tenth]), "overhead_last_tenth": summarize(overhead_ns[-tenth:]),
            "settled": total}


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", required=True)
    parser.add_argument("--host-arg", action="append", default=[])
    parser.add_argument("--evaluations", type=int, default=950)
    parser.add_argument("--warmup", type=int, default=50)
    parser.add_argument("--gate-p95-ms", type=float)
    parser.add_argument("--output")
    args = parser.parse_args(argv)
    if args.evaluations < 20 or args.warmup < 0:
        parser.error("--evaluations must be at least 20 (a p95 needs samples) and --warmup non-negative")
    if args.evaluations + args.warmup > MAXIMUM_WORK_ITEMS:
        parser.error(f"the host retains at most {MAXIMUM_WORK_ITEMS} work items by default; reduce --evaluations or --warmup")
    measured = measure(args.host, args.host_arg, args.evaluations, args.warmup)
    report = {
        "schema": SCHEMA, "worker": "python", "definition": "serve(max_leases=1) wall time minus the evaluator's own time",
        "evaluations": args.evaluations, "warmup": args.warmup, "gate_p95_ms": args.gate_p95_ms,
        "python": sys.version.split()[0], "platform": platform.platform(), "machine": platform.machine(), **measured,
    }
    text = json.dumps(report, indent=2, sort_keys=True) + "\n"
    if args.output:
        output = Path(args.output)
        output.parent.mkdir(parents=True, exist_ok=True)
        with open(output, "x", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
    sys.stdout.write(text)
    if args.gate_p95_ms is not None and not measured["overhead"]["p95_ms"] < args.gate_p95_ms:
        print(f"worker overhead p95 {measured['overhead']['p95_ms']:.3f} ms is not below {args.gate_p95_ms} ms", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
