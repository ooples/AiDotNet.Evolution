"""CPU-only suite orchestration. Registered final consumption is separate from contract smoke tests."""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import os
from pathlib import Path
import platform
import subprocess
import sys
import time

from protocol import PARTITIONS, digest, freeze, load_plan, materialize, prepare, read_json, write_new

HERE = Path(__file__).resolve().parent


def file_hash(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def runtime_contract(dll):
    dll = Path(dll).resolve(strict=True)
    return {"numeric_binary": file_hash(dll), "core_binary": file_hash(dll.with_name("AiDotNet.Evolution.dll")),
            "python_runtime": platform.python_version(),
            "dotnet_dependencies": file_hash(dll.with_suffix(".deps.json")),
            "worker": file_hash(HERE / "algotune_worker.py"), "protocol": file_hash(HERE / "protocol.py"),
            "orchestrator": file_hash(__file__),
            "python_dependencies": {name: importlib.metadata.version(name) for name in
                                    ("numpy", "scipy", "networkx", "cryptography", "cffi", "pycparser")}}


def run(directory, partition, dll, upstream, output_directory):
    directory = Path(directory)
    plan = load_plan(directory)
    contract = runtime_contract(dll)
    if partition == "final" and plan["mode"] == "registered":
        frozen = read_json(directory / "frozen.json")
        if frozen.get("runtime_contract") != contract:
            raise ValueError("Runtime artifacts changed after configuration selection")
    output = Path(output_directory)
    output.mkdir(parents=True, exist_ok=False)
    plan, configuration_hash, panels, selected = materialize(directory, partition, claim_final=True)
    request = {"schema": "aidotnet-numeric-suite-request-v1", "partition": partition, "mode": plan["mode"],
               "plan_hash": plan["plan_hash"], "configuration_hash": configuration_hash,
               "source_revision": plan["source_revision"], "budget": plan["budget"], "instances": panels["numeric"],
               "methods": selected["numeric_methods"]}
    write_new(output / "numeric-request.json", request)
    environment = dict(os.environ)
    environment.update({"DOTNET_PROCESSOR_COUNT": "2", "OMP_NUM_THREADS": "1", "OPENBLAS_NUM_THREADS": "1", "MKL_NUM_THREADS": "1",
                        "PYTHONHASHSEED": "0", "HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1"})
    started = time.perf_counter()
    numeric = None
    failures = []
    records = []
    completed = False
    try:
        process = subprocess.run(["dotnet", str(Path(dll).resolve()), "--suite", str((output / "numeric-request.json").resolve()),
                                  str((output / "numeric-result.json").resolve())], env=environment,
                                 capture_output=True, timeout=180)
        if (output / "numeric-result.json").exists():
            numeric = read_json(output / "numeric-result.json", 128 * 1024 * 1024)
        if process.returncode or numeric is None or numeric.get("status") != "completed":
            failures.append({"stage": "numeric", "exit_code": process.returncode, "diagnostic": process.stderr[-8192:].decode(errors="replace")})
        for index, instance in enumerate(panels["algotune"]):
            remaining = 300 - (time.perf_counter() - started)
            if remaining <= 0:
                failures.append({"stage": "campaign", "status": "deadline", "undispatched": panels["algotune"][index:]})
                break
            try:
                invocation_start = time.perf_counter()
                process = subprocess.run([sys.executable, str(HERE / "algotune_worker.py"), str(Path(upstream).resolve()),
                                          instance["id"], str(instance["seed"])], env=environment,
                                         capture_output=True, timeout=min(30, remaining))
                if len(process.stdout) > 2 * 1024 * 1024:
                    raise ValueError("Reference worker output exceeds 2 MiB")
                # Retain exact worker output before parsing, including errors. Filenames are admitted numeric ordinals.
                raw_path = output / f"reference-{index}.json"
                with raw_path.open("xb") as raw:
                    raw.write(process.stdout)
                result = read_json(raw_path, 2 * 1024 * 1024)
                records.append({"instance": instance, "exit_code": process.returncode,
                                "worker_wall_seconds": time.perf_counter() - invocation_start, "result": result})
                if process.returncode or result.get("status") != "completed":
                    failures.append({"stage": "reference", "task": instance["id"], "status": "failed",
                                     "diagnostic": process.stderr[-8192:].decode(errors="replace")})
            except subprocess.TimeoutExpired:
                records.append({"instance": instance, "result": {"status": "timed-out", "consumption": "unknown",
                                "maximum_worker_seconds": min(30, remaining)}})
                failures.append({"stage": "reference", "status": "timed-out", "undispatched": panels["algotune"][index + 1:]})
                break
        completed = not failures and len(records) == len(panels["algotune"])
    except subprocess.TimeoutExpired:
        failures.append({"stage": "numeric", "status": "timed-out", "consumption": "unknown", "maximum_worker_seconds": 180})
    except Exception as error:
        failures.append({"stage": "orchestration", "status": "failed", "error": type(error).__name__})
    finally:
        report = {"schema": "aidotnet-suite-result-v1", "plan_hash": plan["plan_hash"], "partition": partition,
                  "mode": plan["mode"], "configuration_hash": configuration_hash, "configuration": selected,
                  "source_revision": plan["source_revision"], "runtime_contract": contract,
                  "catalog_hash": plan["catalog_hash"], "status": "completed" if completed else "incomplete",
                  "elapsed_seconds": time.perf_counter() - started, "numeric": numeric, "program_references": records,
                  "failures": failures, "expected_program_instances": len(panels["algotune"]),
                  "limitations": "Reference-start and numeric protocol evaluation only; no evolved-program/LLM or competitive speedup claim. Private roots require trusted custody. Process deadlines bound trusted children, not malicious-code filesystem/network access."}
        write_new(output / "report.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    create = commands.add_parser("prepare")
    create.add_argument("directory")
    create.add_argument("--source-revision", required=True)
    create.add_argument("--instances", type=int, default=2)
    create.add_argument("--replicates", type=int, default=2)
    create.add_argument("--budget", type=int, default=64)
    create.add_argument("--contract-smoke", action="store_true")
    select = commands.add_parser("freeze")
    select.add_argument("directory")
    select.add_argument("configuration")
    select.add_argument("selection_report")
    execute = commands.add_parser("run")
    execute.add_argument("directory")
    execute.add_argument("partition", choices=PARTITIONS)
    execute.add_argument("--numeric-dll", required=True)
    execute.add_argument("--upstream", required=True)
    execute.add_argument("--output", required=True)
    args = parser.parse_args()
    if args.command == "prepare":
        result = prepare(args.directory, instances=args.instances, replicates=args.replicates, budget=args.budget,
                         source_revision=args.source_revision, smoke=args.contract_smoke)
        print(result["plan_hash"])
    elif args.command == "freeze":
        print(freeze(args.directory, args.configuration, args.selection_report)["configuration_hash"])
    else:
        result = run(args.directory, args.partition, args.numeric_dll, args.upstream, args.output)
        print(result["status"] + ": " + str(Path(args.output) / "report.json"))
        return 0 if result["status"] == "completed" else 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
