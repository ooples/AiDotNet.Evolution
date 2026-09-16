"""Matched-population SciPy baseline using the existing C# objective over bounded JSONL."""

import argparse
import hashlib
import json
from pathlib import Path
import platform
import queue
import re
import subprocess
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "analysis"))
from analyze import load_json, require, integer  # noqa: E402

METHOD = "ScipyDifferentialEvolutionMatched8"
PROTOCOL = "numeric-development-v4-external"
TASKS = ["Sphere", "ShiftedQuadratic", "AnisotropicQuadratic", "RippledQuadratic"]
CORE_METHODS = ["RandomSearch", "HillClimb", "FixedMapElites", "AdaptiveMapElites", "UniformPortfolioMapElites", "DiagonalCma"]
SETTINGS = dict(strategy="best1bin", mutation=[0.5, 1.0], recombination=0.7, tol=0.01, atol=0,
                polish=False, updating="immediate", workers=1, vectorized=False,
                initial_population=8, bounds="eight unit coordinates mapped by the shared C# evaluator",
                rng="numpy.random.default_rng(paired seed)", tuning_trials=0)


class Bridge:
    """One local evaluator process per run; no shell, model credentials or candidate code execution."""

    def __init__(self, dll, task, seed, budget, timeout=30):
        self.timeout = timeout
        self.deadline = time.monotonic() + 180
        self.process = subprocess.Popen(["dotnet", str(dll), "--numeric-service", task, str(seed), str(budget)],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                        text=True, encoding="utf-8", bufsize=1)
        self.messages = queue.Queue()
        self.last = None

        def read():
            try:
                while True:
                    line = self.process.stdout.readline(64 * 1024 * 1024 + 1)
                    if not line or len(line) > 64 * 1024 * 1024:
                        self.messages.put(RuntimeError("Evaluator disconnected or response exceeded its bound."))
                        return
                    self.messages.put(line)
            except (OSError, ValueError) as error:
                self.messages.put(error)

        self.reader = threading.Thread(target=read, daemon=True)
        self.reader.start()

    def receive(self):
        remaining = min(self.timeout, self.deadline - time.monotonic())
        if remaining <= 0:
            raise TimeoutError("Evaluator run deadline exceeded.")
        try:
            message = self.messages.get(timeout=remaining)
        except queue.Empty as error:
            raise TimeoutError("Evaluator response deadline exceeded.") from error
        if isinstance(message, Exception):
            raise message
        self.last = json.loads(message)
        require(isinstance(self.last, dict), "Invalid evaluator response.")
        if self.last.get("Kind") == "error":
            raise RuntimeError("Evaluator rejected request: " + self.last.get("Error", "unknown"))
        return self.last

    def request(self, value):
        self.process.stdin.write(json.dumps(value, allow_nan=False, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        return self.receive()

    def close(self):
        if self.process.poll() is None:
            self.process.kill()
        self.process.wait(timeout=10)
        self.process.stdin.close()
        self.reader.join(timeout=2)
        self.process.stdout.close()


def versions():
    import numpy
    import scipy
    require(numpy.__version__ == "2.4.6" and scipy.__version__ == "1.17.1", "Install benchmarks/external/requirements.txt in an isolated environment.")
    return dict(Python=platform.python_version(), NumPy=numpy.__version__, SciPy=scipy.__version__)


def run_one(dll, task, seed, budget, expected_hash, optimizer=None, source_revision=None):
    import numpy as np
    if optimizer is None:
        from scipy.optimize import differential_evolution
        optimizer = differential_evolution
    bridge = None
    manifest = None
    summary = None
    samples = []
    losses = []
    dispatched = 0
    termination = "failed"
    error = None
    nfev = None
    try:
        bridge = Bridge(dll, task, seed, budget)
        manifest = bridge.receive()
        require(manifest.get("Kind") == "manifest" and manifest.get("Protocol") == "numeric-objective-service-v1"
                and manifest.get("Task") == task and manifest.get("Seed") == seed and manifest.get("Budget") == budget,
                "Evaluator manifest mismatch.")
        require(manifest.get("InitialPopulationHash") == expected_hash, "Paired initial populations differ.")
        if source_revision is not None:
            require(manifest.get("AssemblyVersion", "").endswith("+" + source_revision), "Evaluator binary source revision differs.")
        initial = np.array(manifest["InitialUnits"], dtype=float)
        require(initial.shape == (8, 8), "Unexpected initialization dimensions.")

        def objective(units):
            nonlocal dispatched
            require(dispatched < budget, "Optimizer exceeded the evaluator cap.")
            dispatched += 1
            response = bridge.request(units.tolist())
            require(response.get("Kind") == "measurement" and response.get("EvaluationId") == len(losses)
                    and response.get("CostUnits") == 1, "Invalid measurement receipt.")
            loss = response["Loss"]
            require(type(loss) in (float, int) and np.isfinite(loss) and loss >= 0, "Invalid measured loss.")
            if len(losses) < 8:
                require(response.get("GenomeHash") == manifest["InitialGenomeHashes"][len(losses)], "Initialization round-trip changed.")
            losses.append(loss)
            samples.append(dict(EvaluationId=len(losses) - 1, Status="Completed", BestLoss=min(losses), Attempts=1, CostUnits=1, DiagnosticCodes=[]))
            return loss

        result = optimizer(objective, [(0, 1)] * 8, init=initial, maxiter=budget // 8 - 1,
                           rng=np.random.default_rng(seed), strategy="best1bin", mutation=(0.5, 1.0),
                           recombination=0.7, tol=0.01, atol=0, polish=False, updating="immediate", workers=1, vectorized=False)
        nfev = int(result.nfev)
        summary = bridge.request(None)
        require(summary.get("Kind") == "summary", "Missing final independent accounting.")
        require(bridge.process.wait(timeout=10) == 0, "Evaluator did not terminate successfully.")
        require(nfev == dispatched == len(losses) == summary["EvaluatorCalls"] and 8 <= nfev <= budget,
                "Optimizer/controller/evaluator counters differ.")
        require(summary["Samples"] == samples and result.fun == summary["BestLoss"] == min(losses), "Measured evidence differs.")
        resources = summary["Resources"]
        require(resources["Spent"]["cost_units"] == nfev and resources["Spent"]["proposal_calls"] == nfev - 8
                and not resources["Unknown"] and not resources["MaximumViolated"]
                and all(value == 0 for value in resources["Reserved"].values()), "Unreconciled independent ledger.")
        termination = "evaluation-cap" if nfev == budget else "converged" if bool(result.success) else "failed"
        require(termination != "failed", "Unexpected early optimizer termination.")
    except Exception as exception:
        if isinstance(exception, MemoryError):
            raise
        error = type(exception).__name__ + ": " + str(exception)[:300]
        if bridge is not None and isinstance(bridge.last, dict) and bridge.last.get("Kind") in ("summary", "error"):
            summary = bridge.last
    finally:
        if bridge is not None:
            bridge.close()
    known = summary is not None
    resources = summary["Resources"] if known else dict(Spent={"cost_units": len(losses), "proposal_calls": max(0, len(losses) - 8)},
                                                       Reserved={}, Unknown=1, MaximumViolated=False)
    return dict(Task=task, Method=METHOD, Seed=seed, InitialPopulationHash=expected_hash,
                Status="completed" if termination != "failed" else "failed", StopReason=termination, Error=error,
                EvaluatorCalls=summary["EvaluatorCalls"] if known else len(losses), Proposals=dispatched,
                IndependentEvaluatorCalls=summary["EvaluatorCalls"] if known else None, ControllerDispatches=dispatched,
                OptimizerNfev=nfev, UnknownWork=not known, FinalLoss=min(losses) if losses else None,
                Samples=summary["Samples"] if known else samples, Resources=resources, EvaluatorManifest=manifest)


def validate_campaign(campaign):
    require(campaign.get("SchemaVersion") == 2 and campaign.get("Protocol") == "numeric-development-v3-diagonal-cma"
            and campaign.get("Partition") == "development", "Expected numeric development protocol v3.")
    require(campaign.get("Methods") == CORE_METHODS and campaign.get("TaskCount") == 4
            and campaign.get("Dimensions") == 8 and campaign.get("InitialPopulation") == 8, "Unexpected source campaign.")
    require(isinstance(campaign.get("SourceRevision"), str) and re.fullmatch(r"[0-9a-f]{40}", campaign["SourceRevision"]), "Pin the complete source revision.")
    seeds, budget = campaign.get("Seeds"), campaign.get("Budget")
    require(integer(seeds, 1, 1000) and integer(budget, 8, 1000000) and budget % 8 == 0
            and seeds * budget * len(TASKS) <= 2_000_000, "External campaign exceeds declared work bounds or whole population generations.")
    expected = {(task, method, seed) for task in TASKS for method in CORE_METHODS for seed in range(seeds)}
    rows = campaign.get("Runs")
    require(isinstance(rows, list) and len(rows) == len(expected), "Missing scheduled core runs.")
    hashes = {}
    for row in rows:
        key = (row.get("Task"), row.get("Method"), row.get("Seed"))
        require(key in expected, "Duplicate or unplanned core run.")
        expected.remove(key)
        identity = row.get("InitialPopulationHash")
        require(isinstance(identity, str) and re.fullmatch(r"[0-9a-f]{64}", identity), "Missing initial population identity.")
        pair = (key[0], key[2])
        require(pair not in hashes or hashes[pair] == identity, "Core initial populations differ.")
        hashes[pair] = identity
    return hashes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--evaluator", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--allow-working-tree-smoke", action="store_true", help="Explicitly unverified development smoke only.")
    args = parser.parse_args()
    environment = versions()
    campaign, digest = load_json(args.baseline, 128 * 1024 * 1024)
    hashes = validate_campaign(campaign)
    require(args.evaluator.is_file(), "Build the C# quality executable first.")
    repo = Path(__file__).resolve().parents[2]
    if not args.allow_working_tree_smoke:
        head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()
        changes = subprocess.check_output(["git", "status", "--porcelain", "--untracked-files=all", "--",
                                           "src", "benchmarks", "Directory.Build.props", "Directory.Build.targets"], cwd=repo, text=True)
        require(head == campaign["SourceRevision"] and not changes.strip(), "Use a clean, source-pinned campaign or explicitly label a working-tree smoke.")
    # Reserve the artifact before dispatch. Failures stay in their scheduled rows; never overwrite previous evidence.
    with args.output.open("x", encoding="utf-8") as output:
        runs = []
        for task in TASKS:
            for seed in range(campaign["Seeds"]):
                row = run_one(args.evaluator.resolve(), task, seed, campaign["Budget"], hashes[(task, seed)],
                              source_revision=None if args.allow_working_tree_smoke else campaign["SourceRevision"])
                runs.append(row)
                print(f"{task}/{seed}: {row['Status']}, calls={row['EvaluatorCalls']}, stop={row['StopReason']}", file=sys.stderr)
        report = dict(campaign, Protocol=PROTOCOL, Methods=CORE_METHODS + [METHOD], BaselineInputSha256=digest,
                      ExternalEnvironment=environment, ExternalConfiguration=SETTINGS,
                      WorkingTreeSmoke=args.allow_working_tree_smoke,
                      EvaluatorBinarySha256=hashlib.sha256(args.evaluator.read_bytes()).hexdigest(),
                      Comparability="Same C# objectives, exact initial genomes and evaluator cap. Valid convergence may use fewer calls; no polishing. Proposal metering counts objective dispatches after initialization, not internal arithmetic.",
                      Limitations="Development-only controlled eight-member population, not SciPy native population defaults, OpenEvolve, wall time, peak RAM or confirmatory evidence.",
                      Runs=[dict(row, StopReason="evaluation-cap" if row["Status"] == "completed" else "failed") for row in campaign["Runs"]] + runs)
        json.dump(report, output, indent=2, allow_nan=False)
        output.write("\n")
    return 0 if all(row["Status"] == "completed" for row in runs) else 1


if __name__ == "__main__":
    sys.exit(main())
