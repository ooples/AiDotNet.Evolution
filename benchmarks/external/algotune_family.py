"""V1-04b AlgoTune family: partitioned inputs, a sealed test partition, speedup, and oracle mutants.

Composes the existing pieces rather than duplicating them: `warm_panel.prepare_task` (pinned
upstream generator, reference and the layered independent + upstream validator) and
`WarmEvaluator` (the sandboxed timer every arm shares).

- PARTITIONS. Every catalogue task has dev and validation input seeds that are public, and a
  test partition whose seeds are drawn from `secrets` at seal time. The seal is written to a
  custody file that only the campaign runner reads; the repository and the pre-registration
  (V1-05 TestPartitionHashes) carry only its SHA-256, so no search can see a test input.
- SPEEDUP. reference_seconds / candidate_seconds on the same cases in the same sandbox,
  interleaved per sample so drift hits both sides alike. An invalid candidate has no
  speedup (US-03: a fast wrong program cannot win).
- HEADROOM. A task is eligible only on DEV evidence (R7), so an engine is never tuned on,
  or a task chosen from, test results.
- MUTANTS. Seeded corruptions of the reference's own correct outputs; the oracle must
  reject every one (target: 0 false accepts).
"""
from __future__ import annotations

import copy
import hashlib
import json
from pathlib import Path
import secrets
import statistics

from warm_panel import DEFINITIONS, prepare_task

FAMILY = "algotune"
DEV_SEEDS = tuple(range(1000, 1008))
VALIDATION_SEEDS = tuple(range(2000, 2008))
TEST_SIZE = 8


def tasks(partition=None):
    """Catalogue task ids, optionally one task-level partition (development/selection/final)."""
    return sorted(t for t, d in DEFINITIONS.items() if partition is None or d["partition"] == partition)


def seal(directory, task_ids=None):
    """Draw every task's test seeds once; returns the seal record whose hash is registered."""
    task_ids = sorted(task_ids or tasks())
    unknown = set(task_ids) - set(DEFINITIONS)
    if unknown:
        raise ValueError(f"Unknown AlgoTune tasks {sorted(unknown)}")
    reserved = set(DEV_SEEDS) | set(VALIDATION_SEEDS)
    partitions = {}
    for task in task_ids:
        seeds = []
        while len(seeds) < TEST_SIZE:
            candidate = secrets.randbits(32)
            if candidate not in reserved and candidate not in seeds:
                seeds.append(candidate)
        partitions[task] = seeds
    record = dict(Family=FAMILY, Schema="evolution-sealed-test-partition-v1", TestSeeds=partitions,
                  DevSeeds=list(DEV_SEEDS), ValidationSeeds=list(VALIDATION_SEEDS))
    body = json.dumps(record, sort_keys=True, separators=(",", ":")).encode()
    record_hash = hashlib.sha256(body).hexdigest()
    path = Path(directory) / f"{FAMILY}-test-partition.json"
    Path(directory).mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(body)
    return dict(path=str(path), sha256=record_hash)


def open_seal(path, expected_sha256):
    """Only the campaign runner calls this, with the hash frozen in the pre-registration."""
    body = Path(path).read_bytes()
    if hashlib.sha256(body).hexdigest() != expected_sha256:
        raise ValueError("Sealed test partition does not match its registered hash")
    return json.loads(body)


def panel(upstream, task, partition, *, sealed=None, contract="strict-upstream-v1"):
    """A prepared task panel for one input partition. Test inputs require the opened seal."""
    if partition == "dev":
        seeds = list(DEV_SEEDS)
    elif partition == "validation":
        seeds = list(VALIDATION_SEEDS)
    elif partition == "test":
        if sealed is None or task not in sealed["TestSeeds"]:
            raise ValueError("Test inputs are available only through the opened seal")
        seeds = sealed["TestSeeds"][task]
    else:
        raise ValueError("partition must be dev, validation or test")
    prepared = prepare_task(upstream, task, partition=DEFINITIONS[task]["partition"], seeds=seeds, contract=contract)
    prepared["metadata"]["input_partition"] = partition
    return prepared


def speedup(reference_timer, candidate_timer, samples=5):
    """Median of interleaved per-sample ratios; None if the candidate is ever invalid."""
    ratios = []
    for _ in range(samples):
        reference = reference_timer()
        candidate = candidate_timer()
        if reference["status"] != "valid":
            raise RuntimeError("The pinned reference failed its own oracle; the panel is unusable")
        if candidate["status"] != "valid":
            return dict(speedup=None, ratios=ratios, reason="invalid candidate cannot score")
        ratios.append(reference["duration_seconds"] / candidate["duration_seconds"])
    return dict(speedup=statistics.median(ratios), ratios=ratios, reason=None)


def eligible(dev_results, *, minimum_speedup=1.05):
    """R7: a task is eligible only if DEV search found a valid, faster program. Evidence
    from any other partition is refused, so eligibility can never be chosen from test data."""
    if any(result.get("input_partition") != "dev" for result in dev_results):
        raise ValueError("Eligibility may use dev-partition evidence only")
    return any(result.get("speedup") is not None and result["speedup"] >= minimum_speedup for result in dev_results)


def _mutants(value, rng_seed):
    """Deterministic corruptions of one correct output, each of which must be wrong."""
    import random
    rng = random.Random(rng_seed)
    out = []

    def walk(node, path):
        if isinstance(node, bool):
            yield path, "flip"
        elif isinstance(node, (int, float)):
            yield path, "number"
        elif isinstance(node, (bytes, bytearray)) and node:
            yield path, "bytes"
        elif isinstance(node, dict):
            for key in sorted(node):
                yield from walk(node[key], path + [key])
        elif isinstance(node, (list, tuple)):
            if node:
                yield path, "drop"
            for index, item in enumerate(node[:8]):
                yield from walk(item, path + [index])
        else:
            try:
                import numpy
                if isinstance(node, numpy.ndarray) and node.size:
                    yield path, "array"
            except ImportError:
                pass

    for path, kind in list(walk(value, []))[:12]:
        mutated = copy.deepcopy(value)
        parent, key = None, None
        node = mutated
        for step in path:
            parent, key, node = node, step, node[step]
        if kind == "flip":
            replacement = not node
        elif kind == "number":
            replacement = node + (1 if isinstance(node, int) else max(1e-3, abs(node) * 0.1 + rng.random()))
        elif kind == "bytes":
            data = bytearray(node)
            data[rng.randrange(len(data))] ^= 0xFF
            replacement = bytes(data)
        elif kind == "drop":
            replacement = list(node)[:-1]
        else:
            replacement = node.copy()
            replacement.flat[rng.randrange(replacement.size)] += 1.0
        if parent is None:
            mutated = replacement
        else:
            parent[key] = replacement
        out.append((".".join(map(str, path)) or "<root>", kind, mutated))
    return out


def mutant_suite(upstream, task, contract="strict-upstream-v1"):
    """False accepts of seeded mutants of the reference's correct dev outputs (target 0)."""
    from warm_panel import load_task, validator
    reference, _ = load_task(upstream, DEFINITIONS[task])
    accepted, total, kinds = [], 0, {}
    for seed in DEV_SEEDS[:3]:
        problem = reference.generate_problem(DEFINITIONS[task]["scale"], random_seed=seed)
        check = validator(task, problem, reference, contract=contract)
        correct = reference.solve(copy.deepcopy(problem))
        if not check(correct):
            raise RuntimeError(f"{task}: the reference fails its own oracle")
        for path, kind, mutated in _mutants(correct, seed):
            total += 1
            kinds[kind] = kinds.get(kind, 0) + 1
            if check(mutated):
                accepted.append(dict(seed=seed, path=path, kind=kind))
    edge = tolerance_edges(reference, task, check_factory=lambda p: validator(task, p, reference, contract=contract))
    return dict(task=task, mutants=total + edge["beyond"], false_accepts=len(accepted) + edge["beyond_accepted"],
                accepted=accepted, kinds=dict(kinds, tolerance_edge=edge["beyond"]),
                within_tolerance_rejected=edge["within_rejected"], within_tolerance=edge["within"])


def tolerance_edges(reference, task, check_factory):
    """Float outputs perturbed at 10x the declared tolerance must be rejected, and at 0.1x
    accepted: gross mutants alone cannot show that the tolerance is tight, nor that it is
    not so strict that correct rounding fails."""
    import numpy
    from program_correctness import ATOL, RTOL
    beyond = beyond_accepted = within = within_rejected = 0
    for seed in DEV_SEEDS[:3]:
        problem = reference.generate_problem(DEFINITIONS[task]["scale"], random_seed=seed)
        check = check_factory(problem)
        correct = reference.solve(copy.deepcopy(problem))
        for scale, is_beyond in ((10.0, True), (0.1, False)):
            mutated = copy.deepcopy(correct)
            target = _first_float(mutated)
            if target is None:
                break
            container, key, value = target
            container[key] = value + scale * (ATOL + RTOL * abs(value))
            accepted = check(mutated)
            if is_beyond:
                beyond += 1
                beyond_accepted += bool(accepted)
            else:
                within += 1
                within_rejected += not accepted
    return dict(beyond=beyond, beyond_accepted=beyond_accepted, within=within, within_rejected=within_rejected)


def _first_float(node):
    import numpy
    if isinstance(node, dict):
        for key in sorted(node):
            value = node[key]
            if isinstance(value, float):
                return node, key, value
            found = _first_float(value)
            if found:
                return found
    elif isinstance(node, list):
        for index, value in enumerate(node):
            if isinstance(value, float):
                return node, index, value
            found = _first_float(value)
            if found:
                return found
    elif isinstance(node, numpy.ndarray) and node.dtype.kind == "f" and node.size:
        flat = node.reshape(-1)
        return flat, 0, float(flat[0])
    return None