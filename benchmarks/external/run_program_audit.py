"""Post-selection adversarial correctness audit; never feeds results back into search."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import random

from docker_sandbox import DockerSandbox, encode
from program_controls import candidate_hash
from program_tasks import TASKS, expected


def cases(task, seed):
    rng = random.Random(seed)
    if task in ("base64_encoding", "sha256_hashing"):
        sizes = [*range(66), 65535, 65536, 65537, 524287, 524288]
        return [{"plaintext": {"$bytes": base64.b64encode(rng.randbytes(n)).decode()}} for n in sizes]
    if task != "count_connected_components":
        raise ValueError("Unknown audit task")
    result = [{"num_nodes": 0, "edges": []}, {"num_nodes": 1, "edges": [[0, 0]]},
              {"num_nodes": 512, "edges": []},
              {"num_nodes": 100, "edges": [[a, b] for a in range(100) for b in range(a)]}]
    for n in (3, 64, 512):
        permutation = list(range(n))
        rng.shuffle(permutation)
        edges = [[permutation[i], permutation[(i + 1) % n]] for i in range(n)]
        edges.extend([edges[0], edges[0][::-1], [permutation[0], permutation[0]]])
        rng.shuffle(edges)
        result.append({"num_nodes": n, "edges": edges})
    result.append({"num_nodes": 128, "edges": [[a, b] for a in range(80) for b in range(a)
                                               if a // 20 == b // 20]})
    return result


def run(pilot, output, image):
    pilot, root = Path(pilot).resolve(), Path(output).resolve()
    report_path = pilot / "report.json"
    report = json.loads(report_path.read_bytes())
    tasks = [row["task"] for row in report.get("tasks", [])]
    if (report.get("status") not in ("completed", "failed") or report.get("not_run") or
            not tasks or len(set(tasks)) != len(tasks) or tasks != report.get("planned_tasks")):
        raise ValueError("Require a stopped complete-panel pilot before audit")
    root.mkdir(parents=True, exist_ok=False)
    selected = {}
    for task in report["tasks"]:
        comparison = json.loads((pilot / task["task"] / "comparison.json").read_bytes())
        schedule = {(r["mode"], r["method"]) for r in comparison["runs"]}
        required = {("controlled", method) for method in ("aidotnet", "openevolve", "one-shot", "single-parent")}
        required.update({("native-bounded", method) for method in ("aidotnet", "openevolve")})
        if len(comparison["runs"]) != 6 or schedule != required:
            raise ValueError("Audit requires every scheduled comparison track, including failures")
        selected[task["task"]] = comparison["runs"]
    # Binding all selected hashes and input hashes precedes any candidate audit execution.
    problems = {task: cases(task, 863791) for task in selected}
    plan = {"schema": "evolution-post-selection-audit-v1", "claim": "correctness-only",
            "pilot_report_sha256": hashlib.sha256(report_path.read_bytes()).hexdigest(),
            "script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "case_counts": {task: len(value) for task, value in problems.items()},
            "inputs_sha256": {task: hashlib.sha256(encode(value)).hexdigest() for task, value in problems.items()},
            "selections": {task: [r["selected_hash"] for r in rows] for task, rows in selected.items()}}
    (root / "plan.json").write_bytes(encode(plan))
    sandbox = DockerSandbox(image, root / "sandbox")
    result = {"plan_sha256": hashlib.sha256(encode(plan)).hexdigest(), "status": "running", "runs": []}
    (root / "report.json").write_bytes(encode(result))
    for task, rows in selected.items():
        answer = encode(expected(task, problems[task]))
        for row in rows:
            if candidate_hash(row["selected_code"]) != row["selected_hash"]:
                raise ValueError("Selected source changed after search")
            receipt = sandbox.run(row["selected_code"], {"class": TASKS[task]["class"], "problems": problems[task]}, phase="adversarial")
            valid = receipt["status"] == "completed" and encode(receipt.get("output")) == answer
            result["runs"].append({"task": task, "method": row["method"], "mode": row["mode"],
                                   "selected_hash": row["selected_hash"], "valid": valid,
                                   "deployment": "eligible" if valid else "original-fallback-required",
                                   "oracle_sha256": hashlib.sha256(answer).hexdigest(), "receipt_id": receipt["id"]})
            (root / "report.json").write_bytes(encode(result))
    result["status"] = "passed" if all(row["valid"] for row in result["runs"]) else "failed"
    (root / "report.json").write_bytes(encode(result))
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("pilot", "output", "image"):
        parser.add_argument("--" + name, required=True)
    args = parser.parse_args()
    result = run(args.pilot, args.output, args.image)
    print(json.dumps({"status": result["status"], "audited_selections": len(result["runs"])}))
    raise SystemExit(0 if result["status"] == "passed" else 1)
