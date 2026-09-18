"""Pinned upstream starting programs with independent host-side oracles.

The pilot deliberately uses only existing DEVELOPMENT families. It cannot consume
US-01's final partition or justify an unseen-family/AlgoTune leaderboard claim.
"""
import base64
import hashlib
import json
from pathlib import Path
import random
import sys

SUITE = Path(__file__).resolve().parents[1] / "suite"
sys.path.insert(0, str(SUITE))
from algotune_worker import normalized_source  # trusted source verification only

TASKS = {
    "base64_encoding": {"class": "Base64Encoding", "sizes": [0, 32768, 524288]},
    "sha256_hashing": {"class": "Sha256Hashing", "sizes": [0, 32768, 524288]},
    "count_connected_components": {"class": "CountConnectedComponents", "sizes": [0, 64, 512]},
}


def initial_program(upstream, task_id):
    catalog = json.loads((SUITE / "catalog-v1.json").read_text())
    definition = next(t for t in catalog["algotune"] if t["id"] == task_id)
    if task_id not in TASKS or definition["partition"] != "development":
        raise ValueError("Only declared development tasks are permitted in the pilot")
    source = normalized_source(upstream, definition).decode("utf-8")
    return source, {**definition, "upstream_revision": catalog["upstream_revision"],
                    "source_sha256": hashlib.sha256(source.encode()).hexdigest()}


def problems(task_id, seed):
    if task_id not in TASKS or type(seed) is not int or not 0 <= seed < 2**64:
        raise ValueError("Invalid task/instance seed")
    rng = random.Random(seed)
    if task_id in ("base64_encoding", "sha256_hashing"):
        return [{"plaintext": {"$bytes": base64.b64encode(rng.randbytes(n)).decode()}} for n in TASKS[task_id]["sizes"]]
    cases = []
    for n in TASKS[task_id]["sizes"]:
        # Fresh sparse components plus isolates/self-loops/duplicate edges, unlike the upstream dense-only generator.
        edges = []
        for i in range(1, n):
            if i % 17 and rng.random() < 0.8:
                edges.append([i, i - 1])
        if n:
            edges.extend([[0, 0], [0, 0]])
        rng.shuffle(edges)
        cases.append({"num_nodes": n, "edges": edges})
    return cases


def expected(task_id, cases):
    outputs = []
    for problem in cases:
        if task_id == "sha256_hashing":
            # Separate host API from the upstream cryptography Hash object; shared backend lineage is possible.
            value = hashlib.sha256(base64.b64decode(problem["plaintext"]["$bytes"], validate=True)).digest()
            outputs.append({"digest": {"$bytes": base64.b64encode(value).decode()}})
        elif task_id == "base64_encoding":
            # Separate bit-level encoder rather than invoking the evolved/upstream b64encode implementation.
            alphabet = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
            data = base64.b64decode(problem["plaintext"]["$bytes"], validate=True)
            result = bytearray()
            for offset in range(0, len(data), 3):
                chunk = data[offset:offset + 3]
                value = int.from_bytes(chunk.ljust(3, b"\0"), "big")
                result.extend(alphabet[(value >> shift) & 63] for shift in (18, 12, 6, 0))
                for index in range(3 - len(chunk)):
                    result[-1 - index] = ord("=")
            outputs.append({"encoded_data": {"$bytes": base64.b64encode(bytes(result)).decode()}})
        elif task_id == "count_connected_components":
            # Union-find oracle independent of the baseline NetworkX traversal.
            parent = list(range(problem["num_nodes"]))
            def root(node):
                while parent[node] != node:
                    parent[node] = parent[parent[node]]
                    node = parent[node]
                return node
            for a, b in problem["edges"]:
                parent[root(a)] = root(b)
            outputs.append({"number_connected_components": len({root(i) for i in range(len(parent))})})
        else:
            raise ValueError("No independent oracle for task")
    return outputs


def description(task_id):
    return (f"Optimize class {TASKS[task_id]['class']}.solve(self, problem), preserving its exact output schema and correctness. "
            "Return a complete Python module with that class. Only solve is required; no Task framework services are available. "
            "The current imports and AlgoTuneTasks.base registration bridge are supported. Python 3.13, NumPy 2.4.6, "
            "SciPy 1.17.1, NetworkX 3.6.1 and cryptography 50.0.1 are available. No network/filesystem persistence. "
            "Objective: minimize complete fresh-process batch latency INCLUDING imports and serialization, not just solve time. "
            "Inputs include empty/boundary cases and vary up to 512 KiB of bytes or 512 graph nodes. "
            "Do not hardcode answers or assume generation seeds. No tools or self-evaluation.")
