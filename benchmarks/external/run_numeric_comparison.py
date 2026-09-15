"""Run core and SciPy against one frozen development/contract-smoke suite request.

Registered selection/final comparisons require the campaign's custody controller;
this entry point deliberately cannot turn public test seeds into holdout evidence.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import time

from suite_baseline import run_one
from ribs_baseline import run_one as run_cma_me

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "suite"))
from protocol import read_json, write_new


def run(request_path, dll, output_directory, modes=("controlled",)):
    request = read_json(request_path)
    if request.get("mode") != "contract-smoke" and request.get("partition") != "development":
        raise ValueError("This entry point does not own registered holdout custody")
    if (not modes or len(set(modes)) != len(modes)
            or any(mode not in ("controlled", "native-sized") for mode in modes)):
        raise ValueError("Declare distinct controlled/native-sized tracks")
    if "native-sized" in modes and request.get("budget", 0) < 120:
        raise ValueError("Native-sized track requires at least 120 evaluations")
    required = {"RandomSearch", "HillClimb", "FixedMapElites", "AdaptiveMapElites", "DiagonalCma"}
    if not required.issubset(request.get("methods", [])):
        raise ValueError("Numeric comparison must retain random, hill-climb, MAP-Elites and diagonal-CMA controls")
    dll = Path(dll).resolve(strict=True)
    output = Path(output_directory)
    output.mkdir(parents=True, exist_ok=False)
    # Preserve exactly the request bytes consumed by the core runner.
    write_new(output / "request.json", request)
    started = time.perf_counter()
    report = dict(schema="aidotnet-numeric-comparison-v1", evidence_class="contract-only",
                  status="failed", request=request, modes=list(modes), core=None, external=[], failures=[],
                  binaries={path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                            for path in (dll, dll.with_name("AiDotNet.Evolution.dll"), dll.with_suffix(".deps.json"))},
                  adapters={path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                            for path in (Path(__file__), Path(__file__).with_name("suite_baseline.py"),
                                         Path(__file__).with_name("scipy_baseline.py"), Path(__file__).with_name("ribs_baseline.py"))},
                  limitations=["Native-sized is not untouched SciPy defaults and is reported separately",
                               "DiagonalCma is the repository's diagonal control, not an external CMA-ME implementation",
                               "No OpenEvolve/program/LLM comparison or superiority claim is made by this numeric runner",
                               "Elapsed time is observed, not a matched CPU-time budget; task work units are not money"])
    try:
        child = subprocess.run(["dotnet", str(dll), "--suite", str((output / "request.json").resolve()),
                                str((output / "core.json").resolve())], capture_output=True, timeout=180)
        report["core_exit_code"] = child.returncode
        report["core_diagnostic"] = child.stderr[-8192:].decode(errors="replace")
        core = read_json(output / "core.json", 128 * 1024 * 1024)
        report["core"] = core
        if child.returncode or core.get("status") != "completed":
            raise ValueError("Core campaign failed; retained raw results")
        pairs = {}
        for row in core["runs"]:
            key = (row["task_id"], row["instance_seed"], row["search_seed"])
            initial_hash = row["measurement"]["initial_population_hash"]
            if key in pairs and pairs[key] != initial_hash:
                raise ValueError("Core methods received different initial information")
            pairs[key] = initial_hash
        for mode in modes:
            jobs = [(key, identity, method) for key, identity in pairs.items()
                    for method in (("scipy", "cma-me") if mode == "controlled" else ("scipy",))]
            for (task, instance_seed, search_seed), initial_hash, method in jobs:
                if time.perf_counter() - started > 300:
                    report["external"].append(dict(task=task, instance_seed=instance_seed, search_seed=search_seed,
                                                   mode=mode, method=method, status="not-started", reason="campaign-time-cap"))
                    continue
                row = (run_cma_me(dll, task, instance_seed, search_seed, request["budget"], initial_hash) if method == "cma-me"
                       else run_one(dll, task, instance_seed, search_seed, request["budget"], initial_hash, mode=mode))
                report["external"].append(row)
        if all(row["status"] == "completed" for row in report["external"]):
            report["status"] = "completed"
    except Exception as exception:
        if isinstance(exception, MemoryError):
            raise
        report["failures"].append(dict(type=type(exception).__name__, message=str(exception)[:500]))
    report["elapsed_seconds"] = time.perf_counter() - started
    write_new(output / "comparison.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", type=Path, required=True)
    parser.add_argument("--evaluator", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--include-native-sized", action="store_true")
    args = parser.parse_args()
    modes = ("controlled", "native-sized") if args.include_native_sized else ("controlled",)
    report = run(args.request, args.evaluator, args.output, modes)
    print(json.dumps({"status": report["status"], "external_runs": len(report["external"])}))
    return 0 if report["status"] == "completed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
