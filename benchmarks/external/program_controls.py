"""Population ablations sharing the same injected model and isolated evaluator.

This module never executes candidate code. The evaluator must provide its own
process/container isolation; a Python function call is not a sandbox.
"""
from __future__ import annotations

import hashlib
import math
import time


def candidate_hash(code):
    if not isinstance(code, str) or not code.strip() or len(code.encode("utf-8")) > 64 * 1024:
        raise ValueError("Expected nonempty candidate text up to 64 KiB")
    return hashlib.sha256(code.encode("utf-8")).hexdigest()


CONTROLLED_SYSTEM = ("Optimize the supplied Python program for this task. Return the complete program in a python code fence. "
                     "Preserve its interface and correctness. Do not access tools or evaluate it yourself.")
CONTROLLED_USER = "Parent program:\n```python\n{current_program}\n```"


def controlled_prompt(task, parent, feedback):
    # Deliberately parent-only: native fitness formatting/history is not smuggled into the controlled track.
    return (CONTROLLED_SYSTEM + "\nTask:\n" + task,
            [{"role": "user", "content": CONTROLLED_USER.format(current_program=parent)}])


def extract_program(response):
    if not isinstance(response, str) or len(response.encode("utf-8")) > 64 * 1024:
        raise ValueError("Invalid model response")
    # Exactly one full program, no best-effort multiple-block concatenation.
    blocks = response.split("```")
    if len(blocks) != 3 or blocks[0].strip() or blocks[2].strip():
        raise ValueError("Expected exactly one fenced full program")
    language, separator, code = blocks[1].partition("\n")
    if not separator or language.strip().lower() != "python":
        raise ValueError("Expected a python fence")
    candidate_hash(code)
    return code


def run_control(method, task, initial, generate, evaluate, *, model_calls, evaluations, seconds):
    if method not in ("one-shot", "single-parent"):
        raise ValueError("Unknown population ablation")
    if (type(model_calls) is not int or not 1 <= model_calls <= 64
            or type(evaluations) is not int or not 2 <= evaluations <= 65
            or type(seconds) not in (int, float) or not math.isfinite(seconds) or not 0 < seconds <= 3600):
        raise ValueError("Invalid declared budget")
    identity = candidate_hash(initial)
    report = dict(method=method, status="running", initial_program_hash=identity,
                  limits=dict(model_calls=model_calls, evaluations=evaluations, admission_seconds=seconds),
                  model_attempts=[], evaluation_attempts=[], unknown_work=False, best=None)
    started = time.monotonic()

    def measure(code):
        # Record dispatch before invoking code; thrown/unknown work cannot vanish.
        attempt = dict(candidate_hash=candidate_hash(code), status="dispatched", receipt=None)
        report["evaluation_attempts"].append(attempt)
        receipt = evaluate(code)
        if (not isinstance(receipt, dict) or receipt.get("status") not in ("valid", "invalid")
                or receipt.get("candidate_hash") != attempt["candidate_hash"]
                or receipt.get("unknown_work") is not False
                or type(receipt.get("work_units")) not in (int, float)
                or not math.isfinite(receipt["work_units"]) or receipt["work_units"] < 0):
            raise ValueError("Missing or unreconciled evaluator receipt")
        quality = receipt.get("quality")
        if receipt["status"] == "valid" and (type(quality) not in (int, float) or not math.isfinite(quality)):
            raise ValueError("Valid candidate requires finite maximization quality")
        attempt.update(status="completed", receipt=receipt)
        if receipt["status"] == "valid" and (report["best"] is None or quality > report["best"]["quality"]):
            report["best"] = dict(code=code, candidate_hash=attempt["candidate_hash"], quality=quality)
        return receipt

    try:
        initial_receipt = measure(initial)
        if initial_receipt["status"] != "valid":
            raise ValueError("Initial program must pass the shared evaluator")
        count = 1 if method == "one-shot" else model_calls
        for _ in range(count):
            if len(report["evaluation_attempts"]) >= evaluations:
                report["status"] = "evaluation-cap"
                break
            if time.monotonic() - started >= seconds:
                report["status"] = "admission-time-cap"
                break
            parent = report["best"]
            system, messages = controlled_prompt(task, parent["code"], str(parent["quality"]))
            attempt = dict(parent_hash=parent["candidate_hash"], system=system, messages=messages, status="dispatched")
            report["model_attempts"].append(attempt)
            response = generate(system, messages)
            attempt.update(status="completed", response=response)
            try:
                code = extract_program(response)
            except ValueError as error:
                attempt.update(status="invalid-response", error=str(error))
                continue
            if time.monotonic() - started >= seconds:
                report["status"] = "admission-time-cap"
                break
            measure(code)
        else:
            report["status"] = "completed"
    except Exception as error:
        if isinstance(error, MemoryError):
            raise
        report.update(status="failed", error=type(error).__name__ + ": " + str(error)[:300],
                      unknown_work=any(row["status"] == "dispatched" for key in ("model_attempts", "evaluation_attempts")
                                       for row in report[key]))
    report["elapsed_seconds"] = time.monotonic() - started
    report["observed_evaluator_work"] = sum(row["receipt"]["work_units"] for row in report["evaluation_attempts"] if row["receipt"])
    return report
