"""V1-72: per-evaluation cost against archive size, ours vs pinned OpenEvolve, null LLM and evaluator.

Ours: one process per (size, repeat). The benchmark fills an archive of exactly `size` elites and times the
evaluations after the fill from inside the run, so no startup cost is included.
OpenEvolve: its population cannot be pre-filled, so each (size, repeat) runs twice in fresh processes, for a baseline
that fills the population (at least `size` iterations, extended until it reports `size` programs) and for the baseline
plus M, with population_size = size, and reports (T(baseline + M) - T(baseline)) / (evaluations added). Every program the
second run adds past the baseline meets a full population, which is the steady state being measured.
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


# OpenEvolve occasionally deadlocks with its process pool (V1-70 saw one run in seven hang); a run that exceeds this
# is killed, counted, and re-measured rather than aborting the campaign.
HANG_SECONDS = 900


class Hung(Exception):
    pass


def run(command, timeout=HANG_SECONDS):
    # Files, not pipes: a pool worker that outlives its parent would hold a pipe open forever. Anything the run left
    # behind is killed before the next measurement starts.
    with tempfile.TemporaryFile("w+", encoding="utf-8") as stdout, tempfile.TemporaryFile("w+", encoding="utf-8") as stderr:
        process = subprocess.Popen(command, stdout=stdout, stderr=stderr, text=True)
        tree = psutil.Process(process.pid)
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            code = None
        finally:
            try:
                leftovers = tree.children(recursive=True)
            except psutil.NoSuchProcess:
                leftovers = []
            for child in leftovers + [tree]:
                try:
                    child.kill()
                except psutil.NoSuchProcess:
                    pass
            psutil.wait_procs(leftovers, timeout=30)
        stdout.seek(0)
        stderr.seek(0)
        output, errors = stdout.read(), stderr.read()
    if code is None:
        raise Hung(f"{command[1:4]} exceeded {timeout} s")
    if code != 0:
        raise RuntimeError(f"{command[1:4]} failed: {errors[-800:]}")
    return json.loads(output.strip().splitlines()[-1])


OURS_DLL = ROOT / "benchmarks/EvolutionScaling/bin/Release/net10.0/EvolutionScaling.dll"


def ours(size, measured):
    return run(["dotnet", str(OURS_DLL), str(size), str(measured)])


def theirs(upstream, size, iterations):
    return run([sys.executable, str(HERE / "openevolve_scaling_run.py"), str(upstream), str(iterations), str(size)])


def full_population(upstream, size):
    """Runs just enough iterations to fill the population, so the measured iterations meet a full one.

    `size` iterations are not enough: some proposals are rejected, leaving e.g. 98 of 100 programs. The baseline is
    extended by the shortfall (with headroom) until the run reports a full population.
    """
    iterations = size
    for _ in range(6):
        result = theirs(upstream, size, iterations)
        if result["Programs"] >= size:
            return result, iterations
        iterations += max(10, 2 * (size - result["Programs"]))
    raise RuntimeError(f"openevolve size={size} never filled its population (last: {result['Programs']} programs)")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--ours-sizes", default="100,1000,10000,50000")
    parser.add_argument("--openevolve-sizes", default="100,1000,5000")
    parser.add_argument("--ours-measured", type=int, default=20000)
    parser.add_argument("--openevolve-measured", type=int, default=200)
    parser.add_argument("--repeats", type=int, default=5)
    # A published copy outside the source tree, so rebuilding or cleaning the tree cannot pull it from under a long run.
    parser.add_argument("--ours-dll", default=None)
    args = parser.parse_args()
    global OURS_DLL
    if args.ours_dll:
        OURS_DLL = Path(args.ours_dll)
    if not OURS_DLL.is_file():
        raise SystemExit(f"benchmark binary not found: {OURS_DLL}")
    rows, hangs = [], []
    for repeat in range(args.repeats):
        for size in [int(s) for s in args.ours_sizes.split(",")]:
            result = ours(size, args.ours_measured)
            rows.append(dict(system="aidotnet", size=size, repeat=repeat, us_per_eval=result["MicrosecondsPerEvaluation"], raw=result))
        for size in [int(s) for s in args.openevolve_sizes.split(",")]:
            for attempt in range(3):
                try:
                    small, baseline = full_population(args.upstream, size)
                    large = theirs(args.upstream, size, baseline + args.openevolve_measured)
                except Hung as hung:
                    hangs.append(dict(size=size, repeat=repeat, detail=str(hung)))
                    continue
                extra = large["Evaluations"] - small["Evaluations"]
                if extra < args.openevolve_measured // 2:
                    raise RuntimeError(f"openevolve size={size} completed too few evaluations to measure ({extra})")
                per_eval = (large["Seconds"] - small["Seconds"]) / extra
                if per_eval > 0:  # a non-positive difference is noise; re-measure, never average it in
                    break
            else:
                raise RuntimeError(f"openevolve size={size} could not be measured in three attempts (hung or non-positive difference)")
            rows.append(dict(system="openevolve", size=size, repeat=repeat, us_per_eval=per_eval * 1e6, small=small, large=large))
        # Written after every repeat, so a failure late in a long run loses at most the repeat in progress.
        summary = write(args, rows, hangs, completed_repeats=repeat + 1)
    print(json.dumps(summary, indent=1))


def write(args, rows, hangs, completed_repeats):
    summary = {}
    for system in ("aidotnet", "openevolve"):
        sizes = sorted({r["size"] for r in rows if r["system"] == system})
        medians = {s: statistics.median(r["us_per_eval"] for r in rows if r["system"] == system and r["size"] == s) for s in sizes}
        summary[system] = dict(us_per_eval_median=medians, growth_largest_over_smallest=medians[sizes[-1]] / medians[sizes[0]])
    result = dict(story="V1-72 #177", repeats=args.repeats, completed_repeats=completed_repeats, ours_measured=args.ours_measured,
                  openevolve_measured=args.openevolve_measured, summary=summary, hangs=hangs, rows=rows)
    Path(args.output).write_text(json.dumps(result, indent=1) + "\n", encoding="utf-8")
    return summary


if __name__ == "__main__":
    main()
