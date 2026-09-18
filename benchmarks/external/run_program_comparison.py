"""Common program-comparison controller; CLI runs contract fixtures only.

Real experiments use run_campaign with a trusted, independently isolated evaluator
and a declared provider. The CLI cannot execute generated candidate programs.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import random
import subprocess
import sys
import time

from codex_transport import MAX_OUTPUT, kill_owned
from program_broker import ProgramBroker, request
from program_controls import candidate_hash, controlled_prompt, run_control


def run_child(command, environment, directory, timeout):
    process = None
    started = time.monotonic()
    try:
        with (directory / "stdout.txt").open("xb") as stdout, (directory / "stderr.txt").open("xb") as stderr:
            process = subprocess.Popen(command, env=environment, stdout=stdout, stderr=stderr,
                                       start_new_session=os.name != "nt")
            while process.poll() is None:
                if time.monotonic() - started > timeout or max(stdout.tell(), stderr.tell()) > MAX_OUTPUT:
                    raise TimeoutError("Optimizer process time/output bound exceeded")
                time.sleep(0.05)
            return process.returncode
    finally:
        if process is not None:
            kill_owned(process)


def run_campaign(output, aidotnet_dll, upstream, initial, task, model, generate, evaluate, *,
                 iterations=2, seed=37, model_tokens=100000, evolution_profile="uniform", openevolve_profile="default",
                 tracks=None, evidence_class, evaluator_manifest):
    if (type(iterations) is not int or not 1 <= iterations <= 64 or type(seed) is not int or not 0 <= seed < 2**32
            or evolution_profile not in ("uniform", "best")
            or openevolve_profile not in ("default", "best")
            or evidence_class not in ("contract-only", "development-experiment")):
        raise ValueError("Invalid bounded program campaign")
    if not isinstance(evaluator_manifest, dict) or not evaluator_manifest.get("identity"):
        raise ValueError("Declare the evaluator identity and isolation contract")
    allowed = [("controlled", "aidotnet"), ("controlled", "openevolve"), ("controlled", "one-shot"),
               ("controlled", "single-parent"), ("native-bounded", "aidotnet"), ("native-bounded", "openevolve")]
    schedule = allowed if tracks is None else [tuple(t) for t in tracks]
    if not schedule or len(schedule) != len(set(schedule)) or any(t not in allowed for t in schedule):
        raise ValueError("Invalid declared controller panel")
    initial_hash = candidate_hash(initial)
    dll = Path(aidotnet_dll).resolve(strict=True)
    root = Path(output)
    root.mkdir(parents=True, exist_ok=False)
    source, description = root / "initial.py", root / "task.txt"
    source.write_text(initial, encoding="utf-8", newline="")
    description.write_text(task, encoding="utf-8", newline="")
    report = dict(schema="aidotnet-program-comparison-v1", evidence_class=evidence_class, status="running",
                  requested_model=model, initial_program_hash=initial_hash, seed=seed, iterations=iterations,
                  evolution_profile=evolution_profile, openevolve_profile=openevolve_profile,
                  language="python", evaluator=evaluator_manifest, tuning={"trials": 0, "work": 0},
                  hardware={"os": platform.platform(), "machine": platform.machine(), "processors": os.cpu_count()},
                  consumer_binary_sha256=hashlib.sha256(dll.read_bytes()).hexdigest(), runs=[],
                  limitations=["Controlled prompts contain task and parent only; native prompts are preserved separately",
                               "Same provider instance/evaluator callback for every system; caller owns evaluator isolation",
                               "One-shot intentionally consumes one model call; unused allowance is not silently spent",
                               "No registered holdout or competitive superiority claim follows from this controller"])
    schedule = [("controlled", "aidotnet"), ("controlled", "openevolve"), ("controlled", "one-shot"),
                ("controlled", "single-parent"), ("native-bounded", "aidotnet"), ("native-bounded", "openevolve")]
    if evidence_class == "development-experiment":
        random.Random(seed).shuffle(schedule)
    report["schedule"] = schedule
    for index, (mode, method) in enumerate(schedule):
        track_started = time.monotonic()
        directory = root / f"{index}-{mode}-{method}"
        directory.mkdir()
        row = dict(mode=mode, method=method, status="failed", model_call_cap=iterations, evaluation_cap=iterations + 1,
                   model_token_cap=model_tokens)
        with ProgramBroker(generate, evaluate, model_calls=iterations, evaluations=iterations + 1,
                           seconds=300, initial=initial, model_tokens=model_tokens) as broker:
            try:
                if method in ("one-shot", "single-parent"):
                    row["control"] = run_control(method, task, initial,
                        lambda system, messages: request(broker.endpoint, broker.capability, "model", {"system": system, "messages": messages}),
                        lambda code: request(broker.endpoint, broker.capability, "evaluate", {"code": code}),
                        model_calls=iterations, evaluations=iterations + 1, seconds=300)
                    if row["control"]["status"] not in ("completed", "evaluation-cap"):
                        raise ValueError("Population ablation did not complete")
                else:
                    environment = dict(os.environ)
                    environment.update(EVOLUTION_BROKER_ENDPOINT=broker.endpoint, EVOLUTION_BROKER_CAPABILITY=broker.capability,
                                       DOTNET_PROCESSOR_COUNT="2", OMP_NUM_THREADS="1", OPENBLAS_NUM_THREADS="1", MKL_NUM_THREADS="1")
                    for name in ("OPENAI_API_KEY", "CODEX_API_KEY"):
                        environment.pop(name, None)
                    if method == "aidotnet":
                        command = ["dotnet", str(dll), str(source.resolve()), str((directory / "adapter-result.json").resolve()),
                                   model, str(iterations), str(seed), str(description.resolve()), mode, evolution_profile]
                    else:
                        command = [sys.executable, "-X", "utf8", str(Path(__file__).with_name("openevolve_adapter.py")),
                                   "--upstream", str(Path(upstream).resolve()), "--initial", str(source.resolve()),
                                   "--output", str((directory / "upstream").resolve()), "--model", model,
                                   "--iterations", str(iterations), "--seed", str(seed), "--task", str(description.resolve()), "--mode", mode,
                                   "--selection-profile", openevolve_profile]
                    row["exit_code"] = run_child(command, environment, directory, 315)
                    if row["exit_code"]:
                        raise ValueError("Optimizer exited unsuccessfully")
                if broker.closed or any(receipt["status"] != "completed" for receipt in broker.rows):
                    raise ValueError("Independent work accounting is incomplete")
                models = [receipt for receipt in broker.rows if receipt["operation"] == "model"]
                if len(models) != (1 if method == "one-shot" else iterations):
                    raise ValueError("Scheduled model calls were not delivered")
                if evidence_class == "contract-only" and sum(receipt["operation"] == "evaluate" for receipt in broker.rows) != len(models) + 1:
                    raise ValueError("The valid contract fixture did not deliver every candidate evaluation")
                if mode == "controlled":
                    expected_system, expected_messages = controlled_prompt(task, initial, None)
                    if models[0]["request"] != {"system": expected_system, "messages": expected_messages}:
                        raise ValueError("Controlled first prompts differ despite identical initial information")
                if method in ("one-shot", "single-parent"):
                    selected = row["control"]["best"]["code"]
                elif method == "aidotnet":
                    selected = json.loads((directory / "adapter-result.json").read_text())["best"]
                else:
                    selected = json.loads((directory / "upstream/adapter-result.json").read_text())["best"]["code"]
                if not isinstance(selected, str) or not any(
                        receipt["operation"] == "evaluate" and receipt["status"] == "completed" and
                        receipt["result"]["status"] == "valid" and receipt["request"]["code"] == selected
                        for receipt in broker.rows):
                    raise ValueError("The selected program has no matching valid search receipt")
                row["selected_code"] = selected
                row["selected_hash"] = candidate_hash(selected)
                row["status"] = "completed"
            except Exception as error:
                if isinstance(error, MemoryError):
                    raise
                row["error"] = type(error).__name__ + ": " + str(error)[:400]
        # Join in-flight broker work before copying scalar totals; late receipts
        # must not be retained beside a stale, pre-shutdown token count.
        row["receipts"] = broker.rows
        row["actual_model_tokens"] = broker.model_tokens
        row["search_wall_seconds"] = time.monotonic() - track_started
        row["independent_counters"] = {"attempted": dict(broker.attempted), "unknown": dict(broker.unknown_attempts),
                                       "model_tokens": broker.model_tokens, "evaluation_seconds": broker.evaluation_seconds}
        if row["status"] != "completed":
            row["selected_code"], row["selected_hash"] = initial, initial_hash
            row["fallback"] = "original-after-failed-search"
        report["runs"].append(row)
        (directory / "receipts.json").write_text(json.dumps(row, indent=2, allow_nan=False), encoding="utf-8")
    report["status"] = "completed" if all(row["status"] == "completed" for row in report["runs"]) else "failed"
    (root / "comparison.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--aidotnet", required=True, type=Path)
    parser.add_argument("--upstream", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    initial = "def solve(x):\n    return x + 1\n"
    def generate(system, messages):
        # Deterministic for identical inputs, but changes with the parent so the
        # valid-proposal fixture does not exercise duplicate rejection instead.
        name = "one_" + candidate_hash(json.dumps(messages, sort_keys=True))[:16]
        evolved = f"def solve(x):\n    {name} = 1\n    return {name} + x\n"
        return {"text": "```python\n" + evolved + "```", "cost_units": 0,
                "cost_metric": "reported_input_plus_output_tokens"}

    def evaluate(code):
        return dict(candidate_hash=candidate_hash(code), status="valid", quality=1.0, work_units=1, unknown_work=False)

    report = run_campaign(args.output, args.aidotnet, args.upstream, initial, "Contract fixture only.",
                          "contract-fixture-no-provider", generate, evaluate,
                          evidence_class="contract-only", evaluator_manifest={"identity": "nonexecuting-fixture-v1",
                          "isolation": "Candidate code is not executed; all fixed fixture scores are 1.0", "libraries": []})
    print(json.dumps({"status": report["status"], "runs": len(report["runs"])}))
    return 0 if report["status"] == "completed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
