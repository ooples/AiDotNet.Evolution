"""Prepare or execute one locked, artifact-pinned numeric comparison; no builds."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

from analyze import load_json, require
from design import execute, freeze, plan_design, write_new


def runtime_contract(dll):
    dll = Path(dll).resolve(strict=True)
    paths = [dll, dll.with_name("AiDotNet.Evolution.dll"), dll.with_suffix(".deps.json"), dll.with_suffix(".runtimeconfig.json"),
             Path(__file__).resolve()]
    contract = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}
    runtime = subprocess.run(["dotnet", "--list-runtimes"], capture_output=True, check=True, timeout=15)
    environment = {k: v for k, v in os.environ.items() if k.startswith(("DOTNET_", "COMPlus_"))}
    contract["runtime-environment"] = hashlib.sha256(json.dumps(
        [sys.version, runtime.stdout.decode(errors="strict"), environment], sort_keys=True).encode()).hexdigest()
    return contract


def run(directory, identity, dll):
    directory, dll = Path(directory).resolve(), Path(dll).resolve(strict=True)
    contract = runtime_contract(dll)
    def runner(schedule):
        request = directory / "schedule.json"
        output = directory / "numeric-result.json"
        write_new(request, schedule)
        environment = dict(os.environ, DOTNET_PROCESSOR_COUNT="2")
        with (directory / "stdout.log").open("xb") as stdout, (directory / "stderr.log").open("xb") as stderr:
            process = subprocess.run(["dotnet", str(dll), "--analysis-campaign", str(request), str(output)],
                                     env=environment, stdout=stdout, stderr=stderr, timeout=180)
        # Even an unsuccessful controller may have retained valid per-run work;
        # analyze those rows instead of dropping them solely on its exit code.
        campaign, _ = load_json(output, 256 * 1024 * 1024)
        campaign["ControllerExitCode"] = process.returncode
        require(runtime_contract(dll) == contract, "Runtime artifacts changed during execution.")
        require(all(campaign.get("Artifacts", {}).get(name) == contract[name]
                    for name in (dll.name, "AiDotNet.Evolution.dll")), "Runner artifact receipts differ.")
        if process.returncode:
            # Nonzero with attractive completed rows is not an admissible win.
            for row in campaign.get("Runs", []):
                row["Status"] = "controller-failed"
        return campaign
    return execute(directory, identity, contract, runner)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare = commands.add_parser("prepare")
    prepare.add_argument("--pilot", required=True, type=Path)
    prepare.add_argument("--analysis-plan", required=True, type=Path)
    prepare.add_argument("--effect", required=True, type=float)
    prepare.add_argument("--maximum-runs", type=int, default=1000)
    prepare.add_argument("--directory", required=True, type=Path)
    prepare.add_argument("--evaluator", required=True, type=Path)
    execute_parser = commands.add_parser("run")
    execute_parser.add_argument("--directory", required=True, type=Path)
    execute_parser.add_argument("--expected-hash", required=True)
    execute_parser.add_argument("--evaluator", required=True, type=Path)
    args = parser.parse_args()
    if args.command == "prepare":
        campaign, _ = load_json(args.pilot, 256 * 1024 * 1024)
        plan, _ = load_json(args.analysis_plan, 1024 * 1024)
        require(plan["Protocol"] == "numeric-development-v3-diagonal-cma", "The shipped fixed runner supports numeric v3 only.")
        design = plan_design(campaign, plan, effect=args.effect, maximum_runs=args.maximum_runs)
        require(design["RequiredRunsPerTaskMethod"] * plan["Budget"] * len(plan["Tasks"]) * len(plan["Methods"]) <= 2_000_000,
                "Fixed schedule exceeds the shipped controller's two-million-evaluation bound.")
        registration = freeze(args.directory, design, runtime_contract(args.evaluator))
        print(registration["RegistrationSha256"])
    else:
        report = run(args.directory, args.expected_hash, args.evaluator)
        print(json.dumps({"runs": len(report["Runs"]), "confirmatory_eligible": False}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
