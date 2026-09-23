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
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def once(system, n, workers, upstream):
    if system == "aidotnet":
        command = ["dotnet", str(ROOT / "benchmarks/EvolutionOverhead/bin/Release/net10.0/EvolutionOverhead.dll"), str(n), str(workers), "1"]
    else:
        command = [sys.executable, str(HERE / "openevolve_null_run.py"), str(upstream), str(n), str(workers), "1"]
    out = subprocess.run(command, capture_output=True, text=True, timeout=900)
    if out.returncode != 0:
        raise RuntimeError(f"{system} n={n} w={workers} failed: {out.stderr[-500:]}")
    return json.loads(out.stdout.strip().splitlines()[-1])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--workers", default="1,2,4,8,16")
    parser.add_argument("--repeats", type=int, default=9)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    sizes = {"aidotnet": 2000, "openevolve": 100}
    rows, cells = [], {}
    for workers in [int(w) for w in args.workers.split(",")]:
        for repeat in range(args.repeats):
            for system, n in sizes.items():  # alternate systems within a repeat, fresh processes each time
                small, large = once(system, n, workers, args.upstream), once(system, 2 * n, workers, args.upstream)
                per_eval = (large["Seconds"] - small["Seconds"]) / n
                rows.append(dict(system=system, workers=workers, repeat=repeat, n=n, small=small, large=large,
                                 per_eval_seconds=per_eval))
                cells.setdefault((system, workers), []).append(dict(per_eval=per_eval, memory=large["PeakWorkingSetBytes"]))
    rng = random.Random(7)
    summary = []
    for workers in sorted({w for _, w in cells}):
        ours = [c["per_eval"] for c in cells[("aidotnet", workers)]]
        theirs = [c["per_eval"] for c in cells[("openevolve", workers)]]
        ratios = []
        for _ in range(4000):
            idx = [rng.randrange(len(ours)) for _ in ours]
            ratios.append(statistics.median(ours[i] for i in idx) / statistics.median(theirs[i] for i in idx))
        ratios.sort()
        summary.append(dict(workers=workers,
                            aidotnet_us_per_eval=statistics.median(ours) * 1e6,
                            openevolve_us_per_eval=statistics.median(theirs) * 1e6,
                            ratio_ours_over_theirs=statistics.median(ours) / statistics.median(theirs),
                            ratio_ci95=[ratios[int(0.025 * len(ratios))], ratios[int(0.975 * len(ratios)) - 1]],
                            aidotnet_peak_mb=statistics.median(c["memory"] for c in cells[("aidotnet", workers)]) / 2**20,
                            openevolve_peak_mb=statistics.median(c["memory"] for c in cells[("openevolve", workers)]) / 2**20))
    base = {s: next(x for x in summary if x["workers"] == min(c["workers"] for c in summary)) for s in ("aidotnet",)}
    result = dict(story="V1-30 #120", repeats=args.repeats, sizes=sizes, method="(T(2N)-T(N))/N, fresh processes, null LLM and evaluator",
                  headline=False, note="A C# controller vs a Python one is expected to favour us (R12); not a headline claim.",
                  summary=summary, rows=rows)
    Path(args.output).write_text(json.dumps(result, indent=1) + "\n", encoding="utf-8")
    for s in summary:
        print(json.dumps({k: (round(v, 3) if isinstance(v, float) else v) for k, v in s.items()}))


if __name__ == "__main__":
    main()