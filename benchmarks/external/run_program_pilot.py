"""Development-only real program comparison. Never creates a release/confirmation claim.

The plan is written before model dispatch. All six tracks and failures are retained.
Live transport is explicitly opt-in; scripted mode tests plumbing, not model efficacy.
"""
import argparse
import hashlib
import json
from pathlib import Path
import random
import subprocess
import time

from codex_transport import CodexTransport
from docker_sandbox import DockerSandbox, encode
from isolated_program_evaluator import IsolatedProgramEvaluator
from program_controls import candidate_hash
from program_tasks import TASKS, description, initial_program
from run_program_comparison import run_campaign


def run(output, image, upstream, openevolve, dll, *, codex=None, model="gpt-6-astra", tasks=None, samples=3):
    task_ids = list(TASKS) if tasks is None else tasks
    if not task_ids or len(set(task_ids)) != len(task_ids) or any(t not in TASKS for t in task_ids) or not 1 <= samples <= 9:
        raise ValueError("Invalid development pilot panel")
    root = Path(output).resolve()
    root.mkdir(parents=True, exist_ok=False)
    source_revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=Path(__file__).parent, text=True).strip()
    sources = {task: initial_program(upstream, task) for task in task_ids}
    implementation = Path(__file__).parent
    files = [*implementation.glob("*.py"), implementation / "sandbox" / "worker.py",
             implementation / "sandbox" / "Dockerfile", *Path(dll).parent.glob("*.dll")]
    artifacts = {str(path.resolve()): hashlib.sha256(path.read_bytes()).hexdigest() for path in files}
    plan = {"schema": "evolution-program-pilot-v1", "evidence_class": "development-only",
            "source_revision": source_revision, "artifacts_sha256": artifacts,
            "image": image, "tasks": {t: s[1] for t, s in sources.items()},
            "task_order": task_ids, "search_seeds": [37], "iterations_per_track": 2, "samples_per_evaluation": samples,
            "model_calls_maximum": len(task_ids) * 11, "model_tokens_per_track": 100000,
            "model": model if codex else "scripted-no-provider", "resolved_model": "unreported" if codex else "not-applicable",
            "primary_endpoint": "fresh-process batch runtime including imports and serialization",
            "budget_semantics": "equal declared per-track caps; actual tokens/evaluation seconds reported separately, NOT equal reconciled cost",
            "claim": "none", "confirmation": "fresh-instance diagnostic only; not a sealed registered final campaign",
            "tuning": {"trials": 0, "configuration": "fixed disclosed adapter presets, NOT equally tuned native configurations"}}
    (root / "plan.json").write_bytes(encode(plan))
    sandbox = DockerSandbox(image, root / "sandbox")
    transport = CodexTransport(codex, model, root / "model", plan["model_calls_maximum"]) if codex else None
    report = {"plan_sha256": hashlib.sha256(encode(plan)).hexdigest(), "claim": "none", "planned_tasks": task_ids,
              "status": "running", "tasks": [], "evidence_class": "development-only"}
    (root / "report.json").write_bytes(encode(report))
    started = time.monotonic()
    for task_id in task_ids:
        source, provenance = sources[task_id]
        evaluator = IsolatedProgramEvaluator(sandbox, task_id, 9137, samples=samples)
        if transport:
            generate = transport.generate_metered
        else:
            def generate(system, messages):
                # No efficacy claim: preserve the same original solver, but distinct harmless source identities.
                nonce = candidate_hash(json.dumps(messages, sort_keys=True))[:16]
                return {"text": "```python\n" + source + "\n# scripted-" + nonce + "\n```",
                        "cost_units": 0, "cost_metric": "reported_input_plus_output_tokens"}
        result = run_campaign(root / task_id, dll, openevolve, source, description(task_id), plan["model"], generate,
                              evaluator, iterations=2, seed=37, evidence_class="development-experiment",
                              evaluator_manifest=evaluator.manifest)
        # Selection is frozen before fresh-instance diagnostic inputs are generated or scored.
        selections = [{"mode": row["mode"], "method": row["method"], "hash": row["selected_hash"]} for row in result["runs"]]
        (root / task_id / "selections.json").write_bytes(encode(selections))
        fresh = IsolatedProgramEvaluator(sandbox, task_id, 491837, samples=samples, phase="confirmation")
        pairs = []
        for index, row in enumerate(result["runs"]):
            order = ["original", "selected"]
            random.Random(371 + index).shuffle(order)
            receipts = {}
            for role in order:
                receipts[role] = fresh(source if role == "original" else row["selected_code"])
            original, selected = receipts["original"], receipts["selected"]
            if original["status"] != "valid":
                raise RuntimeError("The original program failed independent fresh-instance validation")
            fallback = (selected["status"] != "valid" or row.get("fallback") is not None or
                        row["selected_hash"] == candidate_hash(source))
            # Comparing two noisy timings of the unchanged fallback cannot manufacture an improvement.
            ratio = 1.0 if fallback else original["duration_seconds"] / selected["duration_seconds"]
            pairs.append({"mode": row["mode"], "method": row["method"], "search_status": row["status"], "order": order,
                          "original": original, "selected": selected, "speedup": ratio,
                          "fallback": fallback, "deployed_hash": candidate_hash(source) if fallback else row["selected_hash"],
                          "model_tokens": row["actual_model_tokens"],
                          "search_evaluation_seconds": sum(r["result"]["work_units"] for r in row["receipts"]
                              if r["operation"] == "evaluate" and r["status"] == "completed"),
                          "search_evaluator_setup_seconds": evaluator.oracle_setup_seconds,
                          "diagnostic_oracle_setup_seconds": fresh.oracle_setup_seconds})
            (root / task_id / "diagnostic-pairs.json").write_bytes(encode(pairs))
        report["tasks"].append({"task": task_id, "status": result["status"], "pairs": pairs, "source": provenance})
        (root / "report.json").write_bytes(encode(report))
        if transport and transport.failed:
            report["not_run"] = task_ids[len(report["tasks"]):]
            break
    report["status"] = "completed" if len(report["tasks"]) == len(task_ids) and all(t["status"] == "completed" for t in report["tasks"]) else "failed"
    report["elapsed_seconds"] = time.monotonic() - started
    report["model_calls"] = transport.calls if transport else 0
    report["evaluator_attempts"] = len(sandbox.rows)
    report["unknown_evaluator_attempts"] = sum(r["unknown_work"] for r in sandbox.rows)
    (root / "report.json").write_bytes(encode(report))
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("output", "upstream", "openevolve", "dll"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--codex", type=Path)
    parser.add_argument("--model", default="gpt-6-astra")
    parser.add_argument("--task", action="append", choices=list(TASKS))
    parser.add_argument("--samples", type=int, default=3)
    args = parser.parse_args()
    try:
        result = run(args.output, args.image, args.upstream, args.openevolve, args.dll,
                     codex=args.codex, model=args.model, tasks=args.task, samples=args.samples)
    except Exception as error:
        if args.output.is_dir():
            failure = args.output / "failure.json"
            with failure.open("xb") as stream:
                stream.write(encode({"status": "failed", "error": type(error).__name__, "claim": "none"}))
        raise
    print(json.dumps({key: result[key] for key in ("status", "model_calls", "evaluator_attempts", "claim")}))
    return 0 if result["status"] == "completed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
