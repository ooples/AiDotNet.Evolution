"""Execute only the pinned upstream starting implementations; never start AlgoTuner."""
from __future__ import annotations

import base64
import copy
import hashlib
import importlib.metadata
import math
import os
from pathlib import Path
import platform
import subprocess
import sys
import time
import types

from protocol import catalog, digest, encode


def normalized_source(root, task):
    root = Path(root).resolve(strict=True)
    path = root / "AlgoTuneTasks" / task["id"] / (task["id"] + ".py")
    if not path.resolve(strict=True).is_relative_to(root) or path.stat().st_size > 128 * 1024:
        raise ValueError("Task path or source size is invalid")
    content = path.read_bytes().replace(b"\r\n", b"\n")
    blob = hashlib.sha1(b"blob " + str(len(content)).encode() + b"\0" + content).hexdigest()
    if blob != task["blob"]:
        raise ValueError("Upstream task source differs from pinned Git blob")
    return content


def load_task(root, task):
    source = normalized_source(root, task)
    # The upstream Task base imports configuration, dataset downloads and agent infrastructure.
    # None of these eleven audited per-problem implementations use those services. Supply only
    # their constructor/registration surface, preserving their generator, solve and validator code.
    class Task:
        def __init__(self, **kwargs):
            if kwargs:
                raise ValueError("Dataset/agent options are not supported by the reference bridge")
            self.task_name = type(self).__name__
            self.oracle = self.solve

    bridge = types.ModuleType("AlgoTuneTasks.base")
    bridge.Task = Task
    bridge.register_task = lambda name: lambda cls: cls
    package = types.ModuleType("AlgoTuneTasks")
    package.__path__ = []
    previous = {name: sys.modules.get(name) for name in ("AlgoTuneTasks", "AlgoTuneTasks.base")}
    try:
        sys.modules["AlgoTuneTasks"] = package
        sys.modules["AlgoTuneTasks.base"] = bridge
        module = types.ModuleType("pinned_algotune_" + task["id"])
        exec(compile(source, "pinned/" + task["id"] + ".py", "exec"), module.__dict__)
        return getattr(module, task["class"])(), hashlib.sha256(source).hexdigest()
    finally:
        for name, prior in previous.items():
            if prior is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = prior


def canonical(value):
    import numpy as np
    if isinstance(value, np.ndarray):
        if value.dtype.hasobject:
            raise ValueError("Object arrays are not part of the reference contract")
        contiguous = np.ascontiguousarray(value.astype(value.dtype.newbyteorder("<"), copy=False))
        return {"ndarray": base64.b64encode(contiguous.tobytes()).decode(), "dtype": contiguous.dtype.str,
                "shape": list(contiguous.shape)}
    if isinstance(value, np.generic):
        return canonical(value.item())
    if isinstance(value, bytes):
        return {"bytes": base64.b64encode(value).decode()}
    if isinstance(value, dict):
        return {"mapping": sorted([[canonical(key), canonical(item)] for key, item in value.items()], key=lambda item: encode(item[0]))}
    if isinstance(value, (list, tuple)):
        return {"sequence": [canonical(item) for item in value], "kind": type(value).__name__}
    if isinstance(value, float) and not math.isfinite(value):
        return {"float": "nan" if math.isnan(value) else "+inf" if value > 0 else "-inf"}
    if value is None or isinstance(value, (bool, str, int, float)):
        return value
    raise ValueError("Unsupported problem/result type: " + type(value).__name__)


def fingerprint(value):
    data = encode(canonical(value))
    if len(data) > 2 * 1024 * 1024:
        raise ValueError("Reference problem/result exceeds 2 MiB")
    return hashlib.sha256(data).hexdigest()


def finite_solution(value):
    import numpy as np
    if isinstance(value, np.ndarray):
        return not value.dtype.hasobject and bool(np.all(np.isfinite(value)))
    if isinstance(value, np.generic):
        return finite_solution(value.item())
    if isinstance(value, dict):
        return all(finite_solution(item) for item in value.values())
    if isinstance(value, (tuple, list)):
        return all(finite_solution(item) for item in value)
    return not isinstance(value, float) or math.isfinite(value)


def dependency_version(name):
    # Reporting an absent package must not discard an already-completed measurement; record the
    # absence instead, so the manifest still says exactly which environment produced the result.
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "not-installed"


def environment():
    return {"python": platform.python_version(), "platform": platform.platform(), "machine": platform.machine(),
            "cpu": platform.processor() or "unavailable", "logical_processors": os.cpu_count(),
            "dependencies": {name: dependency_version(name) for name in
                             ("numpy", "scipy", "networkx", "cryptography", "cffi", "pycparser")},
            "thread_limits": {name: os.environ.get(name) for name in
                              ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS")}}


def execute(root, task_id, seed):
    started = time.perf_counter()
    definition = catalog()
    task_definition = next(task for task in definition["algotune"] if task["id"] == task_id)
    if type(seed) is not int or not 0 <= seed < 2**32:
        raise ValueError("Expected an unsigned 32-bit instance seed")
    head = subprocess.run(["git", "-C", str(root), "rev-parse", "HEAD"], check=True,
                          capture_output=True, text=True, timeout=10).stdout.strip()
    if head != definition["upstream_revision"]:
        raise ValueError("Wrong AlgoTune checkout revision")
    task, source_hash = load_task(root, task_definition)
    generated = time.perf_counter()
    problem = task.generate_problem(task_definition["scale"], random_seed=seed)
    generation_seconds = time.perf_counter() - generated
    problem_hash = fingerprint(problem)
    samples = []
    validation_seconds = 0.0
    # One explicitly charged warm-up and three measured repetitions. Validation is separately
    # timed but included in total wall time. This measures only a starting solver, never speedup.
    for repeat in range(4):
        owned = copy.deepcopy(problem)
        before = time.perf_counter()
        solution = task.solve(owned)
        elapsed = time.perf_counter() - before
        validation_start = time.perf_counter()
        valid = finite_solution(solution) and bool(task.is_solution(copy.deepcopy(problem), copy.deepcopy(solution)))
        validation_seconds += time.perf_counter() - validation_start
        if fingerprint(problem) != problem_hash:
            raise ValueError("Reference validation mutated the retained instance")
        samples.append({"repetition": repeat, "warmup": repeat == 0, "elapsed_seconds": elapsed,
                        "valid": valid, "solution_hash": fingerprint(solution)})
    return {"schema": "aidotnet-algotune-reference-v1", "task": task_id, "family": task_definition["family"],
            "partition": task_definition["partition"], "seed": seed, "scale": task_definition["scale"],
            "status": "completed" if all(sample["valid"] for sample in samples) else "invalid-reference",
            "upstream_revision": head, "source_blob": task_definition["blob"], "source_sha256": source_hash,
            "evaluator_hash": digest({"upstream_source": source_hash, "bridge": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}),
            "starting_implementation": f"AlgoTuneTasks/{task_id}/{task_id}.py#{task_definition['class']}.solve",
            "problem_hash": problem_hash, "generator_calls": 1, "solver_calls": 4, "validator_calls": 4,
            "generation_seconds": generation_seconds, "validation_seconds": validation_seconds,
            "elapsed_seconds": time.perf_counter() - started, "samples": samples, "environment": environment(),
            "limitations": "Pinned upstream per-problem reference methods through an explicit minimal base bridge; not the full AlgoTuner harness, an evolved program or a speedup result. Validators are upstream oracles, not independent proofs."}


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("Usage: algotune_worker.py <pinned-checkout> <task-id> <instance-seed>")
    try:
        result = execute(Path(sys.argv[1]), sys.argv[2], int(sys.argv[3]))
        sys.stdout.buffer.write(encode(result))
        raise SystemExit(0 if result["status"] == "completed" else 1)
    except Exception as error:
        sys.stdout.buffer.write(encode({"status": "failed", "error": type(error).__name__, "consumption": "unknown"}))
        raise SystemExit(1)
