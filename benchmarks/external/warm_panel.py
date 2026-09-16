"""Catalog-partitioned inputs; trusted pinned reference validation stays on the host."""
import copy
import hashlib
import json
from pathlib import Path
import sys

from docker_sandbox import encode, MAX_REQUEST
from program_tasks import SUITE

sys.path.insert(0, str(Path(__file__).with_name("sandbox")))
from wire_codec import decode, encode as wire
from algotune_worker import finite_solution, load_task, fingerprint


CATALOG = json.loads((SUITE / "catalog-v1.json").read_bytes())
DEFINITIONS = {t["id"]: t for t in CATALOG["algotune"]}


def prepare_task(upstream, task_id, *, partition, seeds, scale=None):
    definition = DEFINITIONS[task_id]
    if definition["partition"] != partition or not seeds or len(set(seeds)) != len(seeds):
        raise ValueError("Task partition/instance schedule mismatch")
    if any(type(s) is not int or not 0 <= s < 2**32 for s in seeds):
        raise ValueError("Invalid instance seeds")
    scale = definition["scale"] if scale is None else scale
    if type(scale) is not int or not 1 <= scale <= 2048:
        raise ValueError("Invalid workload scale")
    reference, source_hash = load_task(upstream, definition)
    cases = [reference.generate_problem(scale, random_seed=s) for s in seeds]
    wires = [wire(p) for p in cases]
    if len(encode({"class": definition["class"], "problems": wires})) > MAX_REQUEST:
        raise ValueError("Panel batch exceeds isolated request bound")
    hashes = [fingerprint(p) for p in cases]
    references = [reference.solve(copy.deepcopy(p)) for p in cases]
    if not all(finite_solution(v) and reference.is_solution(copy.deepcopy(p), copy.deepcopy(v)) for p, v in zip(cases, references)):
        raise ValueError("Pinned original failed its own validator")
    def validate(outputs):
        if not isinstance(outputs, list) or len(outputs) != len(cases):
            return False
        try:
            for problem, output in zip(cases, outputs):
                value = decode(output)
                if not finite_solution(value) or not reference.is_solution(copy.deepcopy(problem), value):
                    return False
        except (ValueError, TypeError, KeyError, IndexError, OverflowError, RecursionError, AttributeError):
            return False
        if [fingerprint(p) for p in cases] != hashes:
            raise RuntimeError("Host validator changed retained cases")
        return True
    from algotune_worker import normalized_source
    return dict(initial=normalized_source(upstream, definition).decode(), cases=wires, validate=validate,
                metadata={**definition, "scale": scale, "instance_seeds": seeds, "source_sha256": source_hash,
                          "input_sha256": hashlib.sha256(encode(wires)).hexdigest(),
                          "validator": "pinned upstream host validator, not independent mathematical proof"})


def description(task):
    return (f"Optimize {task['class']}.solve(self, problem), preserving correctness and output interface. "
            "Return the full Python module. Only solve is required; Task construction/registration is bridged. "
            "Objective: minimize warm request-to-response time, INCLUDING input transport, deep copies and serialization, "
            "EXCLUDING Python startup/imports/solver construction. Fresh inputs arrive only after initialization. "
            f"Task {task['id']}, generator scale {task['scale']}. "
            "Python 3.13, NumPy 2.4.6, SciPy 1.17.1, NetworkX 3.6.1, cryptography 50.0.1. "
            "One CPU, 512MiB, nonroot, no network, readonly files. No answer hardcoding or seed assumptions. "
            "Do not change worker/protocol, access tools, or report your own timings.")
