"""The real searchers behind `pilot_cells.executor`'s seam: our CLI, and OpenEvolve's runner.

`pilot_cells` owns the endpoint and refuses to let a searcher report its own score, so all a
searcher owes is a candidate program and an honest account of what it spent. This module provides
our arm; the OpenEvolve arms follow the same contract.

**Accounting sums two sources, not one.** `aidotnet-evolve run` reports `ModelUsage` from the
variation operator and, separately, a `MetaPrompt` block when instruction evolution is configured -
and the CLI says in its own comment that `ModelUsage` does **not** include those calls. A searcher
that read `ModelUsage` alone would under-report a meta-prompt arm's spend, and the campaign's budget
check would pass on the under-report. So `model_calls` is the sum, and a cell that cannot be
accounted for is a failure rather than a cheap-looking success.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
from pathlib import Path


def _usage(summary):
    """Total model calls and tokens, over every source the CLI reports them from."""
    usage = summary.get("ModelUsage") or {}
    calls = usage.get("ChatCalls")
    tokens = usage.get("TotalTokens")
    if calls is None or tokens is None:
        raise ValueError("the run reported no ModelUsage, so its spend cannot be accounted")
    meta = summary.get("MetaPrompt")
    if meta:
        # Documented in RunCommand: instruction evolution calls the model on its own cadence and
        # those calls are not the variation operator's.
        calls += meta.get("ProposalCalls", 0)
    return int(calls), int(tokens)


def ours(cli, task_directory, python_executable, *, iterations, endpoint=None, timeout=1800):
    """Returns a searcher that runs our CLI over the task's unmodified OpenEvolve config.

    `endpoint`, when given, replaces the config's `api_base` so a test can point the arm at a
    loopback stub and exercise the whole path without spending anything.
    """
    task = Path(task_directory).resolve(strict=True)
    config_source = task / "config.yaml"
    evaluator = task / "evaluator.py"
    initial = task / "initial_program.py"
    for path in (config_source, evaluator, initial):
        if not path.exists():
            raise FileNotFoundError("the pilot task is incomplete: " + str(path))

    def search(cell, directory, resume):
        directory = Path(directory)
        output = directory / "out"
        config = directory / "config.yaml"
        text = config_source.read_text(encoding="utf-8")
        if endpoint is not None:
            text = text.replace("models:", 'api_base: "' + endpoint + '"\n  models:', 1)
        config.write_text(text, encoding="utf-8", newline="\n")

        environment = dict(os.environ,
                           EVOLUTION_SR_TASK=str(cell["Family"]),
                           EVOLUTION_SR_SEED=str(cell["Seed"]))
        if resume and output.exists():
            shutil.rmtree(output)  # a resumed cell re-runs: the engine owns its own resume, not this
        completed = subprocess.run(
            [cli, "run", "--openevolve-config", str(config), str(initial), str(evaluator),
             "--iterations", str(iterations), "--output", str(output), "--python", python_executable],
            capture_output=True, text=True, timeout=timeout, env=environment, cwd=str(directory))
        if completed.returncode != 0:
            raise RuntimeError("the CLI exited " + str(completed.returncode) + ": " + completed.stderr[-400:])

        summary = json.loads(completed.stdout[completed.stdout.index("{"):completed.stdout.rindex("}") + 1])
        best = output / "best.py"
        if not best.exists():
            raise FileNotFoundError("the run produced no best program at " + str(best))
        calls, tokens = _usage(summary)
        return {"program": str(best), "model_calls": calls, "model_tokens": tokens,
                "best_quality": summary.get("BestQuality"), "stop_reason": summary.get("StopReason")}

    return search
