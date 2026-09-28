"""V1-72: per-evaluation cost against archive size, ours vs pinned OpenEvolve, null LLM and evaluator.

Ours: one process per (size, repeat). The benchmark fills an archive of exactly `size` elites and times the
evaluations after the fill from inside the run, so no startup cost is included.
OpenEvolve: its population cannot be pre-filled, so each (size, repeat) runs twice in fresh processes, for `size`
and `size + M` iterations with population_size = size, and reports (T(size + M) - T(size)) / M. Every program the
second run adds past `size` meets a full population, which is the steady state being measured.
Usage: python run_scaling.py --upstream <openevolve checkout> --output <json>
"""
import argparse
import json
import statistics
import subprocess
import sys
import tempfile
from pathlib import Path

import psutil

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def run(command, timeout=3600):
    # Files, not pipes: a pool worker that outlives its parent would hold a pipe open forever. Anything the run left
    # behind is killed before the next measurement starts.
    with tempfile.TemporaryFile("w+", encoding="utf-8") as stdout, tempfile.TemporaryFile("w+", encoding="utf-8") as stderr:
        process = subprocess.Popen(command, stdout=stdout, stderr=stderr, text=True)
        tree = psutil.Process(process.pid)
        try:
            code = process.wait(timeout=timeout)
        finally:
            try:
                leftovers = tree.children(recursive=True)
            except psutil.NoSuchProcess:
                leftovers = []
            for child in leftovers:
                try:
                    child.kill()
                except psutil.NoSuchProcess:
                    pass
            psutil.wait_procs(leftovers, timeout=30)
        stdout.seek(0)
        stderr.seek(0)
        output, errors = stdout.read(), stderr.read()
    if code != 0:
        raise RuntimeError(f"{command[1:4]} failed: {errors[-800:]}")
    return json.loads(output.strip().splitlines()[-1])


def ours(size, measured):
    dll = ROOT / "benchmarks/EvolutionScaling/bin/Release/net10.0/EvolutionScaling.dll"
    return run(["dotnet", str(dll), str(size), str(measured)])


def theirs(upstream, size, iterations):
    return run([sys.executable, str(HERE / "openevolve_scaling_run.py"), str(upstream), str(iterations), str(size)])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--ours-sizes", default="100,1000,10000,50000")
    parser.add_argument("--openevolve-sizes", default="100,1000,5000")
    parser.add_argument("--ours-measured", type=int, default=20000)
    parser.add_argument("--openevolve-measured", type=int, default=200)
    parser.add_argument("--repeats", type=int, default=5)
    args = parser.parse_args()
    rows = []
    for repeat in range(args.repeats):
        for size in [int(s) for s in args.ours_sizes.split(",")]:
            result = ours(size, args.ours_measured)
            rows.append(dict(system="aidotnet", size=size, repeat=repeat, us_per_eval=result["MicrosecondsPerEvaluation"], raw=result))
        for size in [int(s) for s in args.openevolve_sizes.split(",")]:
            for attempt in range(3):
                small = theirs(args.upstream, size, size)
                large = theirs(args.upstream, size, size + args.openevolve_measured)
                extra = large["Evaluations"] - small["Evaluations"]
                if extra < args.openevolve_measured // 2:
                    raise RuntimeError(f"openevolve size={size} completed too few evaluations to measure ({extra})")
                per_eval = (large["Seconds"] - small["Seconds"]) / extra
                if per_eval > 0:  # a non-positive difference is noise; re-measure, never average it in
                    break
            else:
                raise RuntimeError(f"openevolve size={size} gave a non-positive difference three times")
            rows.append(dict(system="openevolve", size=size, repeat=repeat, us_per_eval=per_eval * 1e6, small=small, large=large))
    summary = {}
    for system in ("aidotnet", "openevolve"):
        sizes = sorted({r["size"] for r in rows if r["system"] == system})
        medians = {s: statistics.median(r["us_per_eval"] for r in rows if r["system"] == system and r["size"] == s) for s in sizes}
        summary[system] = dict(us_per_eval_median=medians, growth_largest_over_smallest=medians[sizes[-1]] / medians[sizes[0]])
    result = dict(story="V1-72 #177", repeats=args.repeats, ours_measured=args.ours_measured,
                  openevolve_measured=args.openevolve_measured, summary=summary, rows=rows)
    Path(args.output).write_text(json.dumps(result, indent=1) + "\n", encoding="utf-8")
    print(json.dumps(summary, indent=1))


if __name__ == "__main__":
    main()
