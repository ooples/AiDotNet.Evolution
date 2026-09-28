"""V1-30: engine overhead per evaluation, ours vs pinned OpenEvolve, null evaluators, fresh processes.

Steady-state cost per evaluation is (T(2N) - T(N)) / N per system, which cancels process and pool
startup. Each (system, workers, N) cell runs `repeats` times in fresh processes; the ratio's 95%
interval is a bootstrap over repeat-paired medians. Not a headline claim: a C# controller against
a Python one is expected to win on overhead (R12).
"""
import argparse
import json
import random
import statistics
import subprocess
import sys
import tempfile
from pathlib import Path

import psutil

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def once(system, n, workers, upstream):
    if system == "aidotnet":
        command = ["dotnet", str(ROOT / "benchmarks/EvolutionOverhead/bin/Release/net10.0/EvolutionOverhead.dll"), str(n), str(workers), "1"]
    else:
        command = [sys.executable, str(HERE / "openevolve_null_run.py"), str(upstream), str(n), str(workers), "1"]
    # Output goes to files, not pipes: a pool worker that outlives its run inherits the pipe, and a pipe reader then
    # waits for an end-of-file that never comes (the first V1-70 rerun hung that way for over an hour). Every process
    # the run left behind is killed before the next one starts, so a stray worker cannot skew later timings either.
    with tempfile.TemporaryFile("w+", encoding="utf-8") as stdout, tempfile.TemporaryFile("w+", encoding="utf-8") as stderr:
        process = subprocess.Popen(command, stdout=stdout, stderr=stderr, text=True)
        tree = psutil.Process(process.pid)
        try:
            returncode = process.wait(timeout=900)
        finally:
            leftovers = []
            try:
                leftovers = tree.children(recursive=True)
            except psutil.NoSuchProcess:
                pass
            for child in leftovers + [tree]:
                try:
                    child.kill()
                except psutil.NoSuchProcess:
                    pass
            psutil.wait_procs(leftovers, timeout=30)
        stdout.seek(0)
        stderr.seek(0)
        output, errors = stdout.read(), stderr.read()
    if returncode != 0:
        raise RuntimeError(f"{system} n={n} w={workers} failed: {errors[-500:]}")
    return json.loads(output.strip().splitlines()[-1])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--workers", default="1,2,4,8,16")
    parser.add_argument("--repeats", type=int, default=9)
    parser.add_argument("--output", required=True)
    parser.add_argument("--ours-n", type=int, default=8000)
    parser.add_argument("--openevolve-n", type=int, default=300)
    args = parser.parse_args()
    # Large enough that T(2N) - T(N) is well clear of timing noise; the first campaign's N = 100 for OpenEvolve let a
    # difference go negative, which produced impossible negative ratios in its interval (V1-70). OpenEvolve's 2N must
    # stay below its population cap (1000) so no program is evicted before it is counted.
    sizes = {"aidotnet": args.ours_n, "openevolve": args.openevolve_n}
    rows, cells = [], {}
    for workers in [int(w) for w in args.workers.split(",")]:
        for repeat in range(args.repeats):
            for system, n in sizes.items():  # alternate systems within a repeat, fresh processes each time
                for attempt in range(3):
                    small, large = once(system, n, workers, args.upstream), once(system, 2 * n, workers, args.upstream)
                    # Divided by the evaluations each run actually completed, not the number requested: an iteration
                    # OpenEvolve drops is not orchestrated work and must not dilute its cost per evaluation.
                    extra = large["Evaluations"] - small["Evaluations"]
                    if extra < n // 2:
                        raise RuntimeError(f"{system} w={workers} completed too few evaluations to measure ({extra} of ~{n})")
                    per_eval = (large["Seconds"] - small["Seconds"]) / extra
                    # A non-positive difference is noise swamping the signal; it is re-measured, never averaged in.
                    if per_eval > 0:
                        break
                else:
                    raise RuntimeError(f"{system} w={workers} gave a non-positive difference three times; raise N")
                rows.append(dict(system=system, workers=workers, repeat=repeat, n=n, small=small, large=large,
                                 per_eval_seconds=per_eval))
                cells.setdefault((system, workers), []).append(dict(per_eval=per_eval, memory=large["PeakWorkingSetBytes"]))
    rng = random.Random(7)
    summary = []
    for workers in sorted({w for _, w in cells}):
        ours = [c["per_eval"] for c in cells[("aidotnet", workers)]]
        theirs = [c["per_eval"] for c in cells[("openevolve", workers)]]
        # Paired by repeat (the two systems run back to back in each), then a percentile bootstrap of the median ratio.
        paired = [o / t for o, t in zip(ours, theirs)]
        ratios = []
        for _ in range(4000):
            ratios.append(statistics.median(paired[rng.randrange(len(paired))] for _ in paired))
        ratios.sort()
        summary.append(dict(workers=workers,
                            aidotnet_us_per_eval=statistics.median(ours) * 1e6,
                            openevolve_us_per_eval=statistics.median(theirs) * 1e6,
                            ratio_ours_over_theirs=statistics.median(paired),
                            ratio_ci95=[ratios[int(0.025 * len(ratios))], ratios[int(0.975 * len(ratios)) - 1]],
                            aidotnet_peak_mb=statistics.median(c["memory"] for c in cells[("aidotnet", workers)]) / 2**20,
                            openevolve_peak_mb=statistics.median(c["memory"] for c in cells[("openevolve", workers)]) / 2**20))
    result = dict(story="V1-70 #175 (rerun of V1-30 #120)", repeats=args.repeats, sizes=sizes,
                  method="(T(2N)-T(N))/N, fresh processes, null LLM and evaluator; ratio = median of per-repeat paired ratios, 95% percentile bootstrap",
                  headline=False, note="A C# controller vs a Python one is expected to favour us (R12); not a headline claim.",
                  summary=summary, rows=rows)
    Path(args.output).write_text(json.dumps(result, indent=1) + "\n", encoding="utf-8")
    for s in summary:
        print(json.dumps({k: (round(v, 3) if isinstance(v, float) else v) for k, v in s.items()}))


if __name__ == "__main__":
    main()