"""The real searchers behind `pilot_cells.executor`'s seam: our CLI, and OpenEvolve's runner.

`pilot_cells` owns the endpoint and refuses to let a searcher report its own score, so all a
searcher owes is a candidate program and an honest account of what it spent.

**Both arms are metered by a broker, and that is also why both evaluate through it.** The broker
is an OpenAI-shaped shim in front of the subscription account, and it admits a model call only
after a valid evaluation has come back through it (`program_broker.py:272-278`) - so an arm
that scored candidates locally would have every model call refused. Pointing both arms at
`pilot_task_evaluator` therefore costs nothing and removes an asymmetry: one evaluation callback,
one set of selection rows, and the only difference left between the arms is the search itself.

**Spend is the broker's own count, never a self-report.** Each cell gets its own broker, from
`pilot_broker.session` - a broker cannot be wider than a cell (see that function for the two reasons)
- so the spend is simply what that broker admitted. Our CLI also self-reports `ModelUsage` (plus a
separate `MetaPrompt` block, which it says `ModelUsage` excludes) and that is recorded beside the
measured count as a cross-check: the broker is a ceiling, so a self-report above it means the
measurement is wrong, not that the run was cheap.

A searcher that fails after spending attaches the spend to the exception (`SearchFailed`), because
a failed cell that reports no calls under-reports a real bill.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import openevolve_adapter
import openevolve_configs
import pilot_task_evaluator
import yaml


class SearchFailed(RuntimeError):
    """A searcher's failure, carrying the spend the cell must still report."""

    def __init__(self, message, accounting=None):
        super().__init__(message)
        self.accounting = dict(accounting or {})


def _spend(broker):
    """What this cell's broker actually admitted, plus the work it could not account for."""
    return {"model_calls": int(broker.attempted["model"]), "model_tokens": int(broker.model_tokens),
            "evaluations": int(broker.attempted["evaluate"]),
            "unaccounted_calls": int(broker.unknown_attempts["model"]),
            "throttle_seconds": float(sum(row.get("throttle_seconds", 0.0) for row in broker.rows))}


def _usage(summary):
    """What the CLI says it spent, over every source it reports from, or None if it said nothing.

    Three numbers, not two: `ChatCalls` counts requests the engine SENT, and a request the provider
    refused is one of them. Only `ChatCalls - ProviderErrors` were served, and the broker counts
    what it served - so comparing the two without the refusals compares different quantities.
    """
    usage = summary.get("ModelUsage") or {}
    calls, tokens = usage.get("ChatCalls"), usage.get("TotalTokens")
    if calls is None or tokens is None:
        return None
    refused = int(usage.get("ProviderErrors") or 0)
    meta = summary.get("MetaPrompt")
    if meta:
        # Documented in RunCommand: instruction evolution calls the model on its own cadence and
        # those calls are not the variation operator's, so ModelUsage does not include them.
        calls += meta.get("ProposalCalls", 0)
    return int(calls), int(tokens), refused


def _refuse_if_closed(accounting):
    """A cell whose broker closed is a failed cell, not a cheap one.

    The broker marks itself closed only when work went wrong - a provider error, an evaluation that
    did not reconcile, a token overrun. Running out of budget does not close it (that refusal is
    raised before the recording path), so a closed broker never means "the cell finished its
    allowance". An arm can still exit 0 after a refused proposal, so this is checked before the
    exit code: otherwise a run that lost half its calls would be reported as a result.
    """
    if accounting.get("broker_closed"):
        raise SearchFailed("the broker closed during the cell, so its result is not admissible",
                           accounting)
    if accounting.get("unaccounted_calls"):
        raise SearchFailed("%d model call(s) could not be accounted" % accounting["unaccounted_calls"],
                           accounting)


def _environment(broker):
    """The child environment: the broker, and nothing about the data.

    Neither the task nor either seed is here. The broker's evaluate callback is already bound to the
    cell's task and selection seed, so telling the search process which rows it is being scored on
    would only hand a candidate the means to reconstruct them - and review found that it could, when
    the selection seed was readable: the held-out rows were rebuilt and their seal matched.
    """
    return dict(os.environ,
                EVOLUTION_BROKER_ENDPOINT=broker.endpoint,
                EVOLUTION_BROKER_CAPABILITY=broker.capability)


def ours(cli, task_directory, python_executable, *, iterations, session, timeout=1800):
    """Returns a searcher that runs our CLI over the task's unmodified OpenEvolve config.

    `cli` is a command prefix, not just an executable: a string for an installed
    `aidotnet-evolve`, or a sequence like `["dotnet", "<path>/aidotnet-evolve.dll"]` for a locally
    built one. A dry run caught this - passing `"dotnet"` alone produced `dotnet run ...`, which the
    SDK read as its own `run` command and answered "Couldn't find a project to run", and CI builds
    the dll rather than installing the tool.

    The config file is handed over **as written**: the broker endpoint reaches the arm through
    `OPENAI_API_BASE`/`OPENAI_API_KEY`, which is the fallback our importer declares for an unset
    `llm.api_base` (`OpenEvolveConfigCatalog.cs:31-32`), so the pilot does not have to rewrite the
    task's config to point it at the shim and nobody has to wonder what was rewritten.
    """
    task = Path(task_directory).resolve(strict=True)
    config = task / "config.yaml"
    delegate = Path(__file__).resolve().with_name("pilot_task_evaluator.py")
    initial = task / "initial_program.py"
    for path in (config, delegate, initial):
        if not path.exists():
            raise FileNotFoundError("the pilot task is incomplete: " + str(path))
    if yaml.safe_load(config.read_text(encoding="utf-8")).get("llm", {}).get("api_base") is not None:
        raise ValueError("the task config pins its own api_base, so the broker would not be reached")

    def search(cell, directory, resume):
        directory = Path(directory)
        # campaign_runner creates the cell directory before calling, but a searcher called directly
        # should not depend on that; a test found this by invoking one outside the runner.
        directory.mkdir(parents=True, exist_ok=True)
        output = directory / "out"
        if resume and output.exists():
            shutil.rmtree(output)  # a resumed cell re-runs: the engine owns its own resume, not this
        prefix = [cli] if isinstance(cli, (str, Path)) else list(cli)
        with session(cell) as broker:
            # The CLI scrubs its evaluator's environment, so the broker is written into the
            # evaluator file rather than passed through it.
            evaluator = directory / "cell_evaluator.py"
            evaluator.write_text(pilot_task_evaluator.CELL_EVALUATOR.format(
                external=str(delegate.parent), endpoint=broker.endpoint, capability=broker.capability),
                encoding="utf-8", newline="\n")
            environment = dict(_environment(broker),
                               OPENAI_API_BASE=broker.endpoint + "/v1", OPENAI_API_KEY=broker.capability)
            try:
                completed = subprocess.run(
                    [*[str(part) for part in prefix], "run", "--openevolve-config", str(config), str(initial),
                     str(evaluator), "--iterations", str(iterations), "--output", str(output),
                     "--python", python_executable],
                    capture_output=True, text=True, timeout=timeout, env=environment, cwd=str(directory),
                    check=False)
            except subprocess.TimeoutExpired as error:
                # Running to the wall limit is the most expensive way to fail, so losing the spend
                # here would understate the bill by the most.
                raise SearchFailed("the CLI did not finish within %ss" % timeout,
                                   dict(_spend(broker), broker_closed=broker.closed)) from error
            accounting = dict(_spend(broker), broker_closed=broker.closed)
        _refuse_if_closed(accounting)
        if completed.returncode != 0:
            raise SearchFailed("the CLI exited " + str(completed.returncode) + ": " + completed.stderr[-400:],
                               accounting)
        try:
            summary = json.loads(completed.stdout[completed.stdout.index("{"):completed.stdout.rindex("}") + 1])
        except ValueError as error:
            raise SearchFailed("the CLI printed no run summary: " + str(error), accounting) from error
        reported = _usage(summary)
        if reported is not None:
            sent, tokens, refused = reported
            accounting["reported_model_calls"], accounting["reported_model_tokens"] = sent, tokens
            accounting["reported_provider_errors"] = refused
            # The cross-check that matters is SERVED against admitted: a refused request is the
            # broker's ceiling working, not spend nobody metered. Comparing sent against admitted
            # failed 2 of our 6 cells in the damped_oscillator pilot at 13 sent / 3 refused / 10
            # admitted - a cell penalised for retrying an unusable answer, on exactly the tasks
            # where answers are unusable.
            if sent - refused > accounting["model_calls"]:
                raise SearchFailed(
                    "the CLI served %d calls (%d sent, %d refused) but the broker admitted %d"
                    % (sent - refused, sent, refused, accounting["model_calls"]), accounting)
            # The other direction is deliberately NOT checked. `ModelUsage` is the variation
            # operator's usage alone - an llmFeedback judge's calls go through the same broker and
            # are not in it - so the broker legitimately serves more than the engine reports here.
            # A reverse check failed the existing cross-check test, which is what caught it.
        best = output / "best.py"
        if not best.exists():
            raise SearchFailed("the run produced no best program at " + str(best), accounting)
        return {"program": str(best), "best_quality": summary.get("BestQuality"),
                "stop_reason": summary.get("StopReason"), **accounting}

    return search


def openevolve(adapter, upstream, task_directory, session, *, mode, model, iterations, timeout=1800):
    """Returns a searcher that runs the pinned, unmodified OpenEvolve controller for one arm.

    `session` is `pilot_broker.session`'s `open_for`: one broker per cell, which is also what meters
    this arm.

    **The config record is built here, per cell, and the adapter refuses the run without it.** Its
    native modes exist to run a *built* comparison config, so `--config-record` is required, not
    optional (`openevolve_adapter.py:89-93`), and the record has to be rebuilt for every cell
    because the paired seed is inside it: the adapter rejects a config whose `random_seed` is not
    the seed it was told to run. Both records are built from the task's own `config.yaml`, so the
    recommended arm is this task as a user of OpenEvolve would configure it, with only the model
    names swapped to Claude aliases and the shared-broker overrides forced - and `changes` and
    `differences` record every one of them.

    Two things differ from our arm, and both are places an arm could be made to look cheap:

    - **The winner is inline, not a file.** `adapter-result.json` carries `best: {code, metrics}`, so
      the code is written out here for the executor to load. A run whose `best` is null produced no
      candidate and is a failure, not an empty program that would score as a broken one.
    - **The adapter reports no call count at all**, so the broker is the only account of what this
      arm spent; it is also what our arm is charged by, so the two are comparable.
    """
    if mode not in openevolve_adapter.NATIVE_MODES:
        raise ValueError("An OpenEvolve pilot arm is recommended or matched, not " + str(mode))
    task = Path(task_directory).resolve(strict=True)
    initial = task / "initial_program.py"
    source = task / "config.yaml"
    for path in (initial, source):
        if not path.exists():
            raise FileNotFoundError("the pilot task is incomplete: " + str(path))
    declared = yaml.safe_load(source.read_text(encoding="utf-8"))
    if mode == "matched":
        # Our arm's features, but this task's models and this task's prompt: a difference between
        # the two OpenEvolve arms is then the config shape alone.
        unmapped = sorted(entry["name"] for entry in declared["llm"]["models"]
                          if entry["name"] not in openevolve_configs.TIERS)
        if unmapped:
            raise ValueError("The task declares models with no Claude tier: " + ", ".join(unmapped))
        models = {openevolve_configs.TIERS[entry["name"]]: float(entry["weight"])
                  for entry in declared["llm"]["models"]}
        system_message = declared["prompt"]["system_message"]

    def build(seed):
        if mode == "recommended":
            return openevolve_configs.recommended(declared, iterations=iterations, seed=seed)
        return openevolve_configs.matched(models, system_message, iterations=iterations, seed=seed)

    def search(cell, directory, resume):
        directory = Path(directory)
        directory.mkdir(parents=True, exist_ok=True)
        output = directory / "out"
        record = directory / "record.json"
        record.write_text(json.dumps(build(int(cell["Seed"])), indent=1), encoding="utf-8")
        command = [sys.executable, str(adapter), "--upstream", str(upstream), "--initial", str(initial),
                   "--output", str(output), "--model", model, "--iterations", str(iterations),
                   # --task is the task description for the adapter's own prompt modes; a native
                   # mode never reads it, because the config carries its own system message.
                   "--seed", str(cell["Seed"]), "--task", str(initial), "--mode", mode,
                   "--config-record", str(record)]
        if resume:
            command.append("--resume")
        with session(cell) as broker:
            try:
                completed = subprocess.run(command, capture_output=True, text=True, timeout=timeout,
                                           env=_environment(broker), cwd=str(directory), check=False)
            except subprocess.TimeoutExpired as error:
                raise SearchFailed("the adapter did not finish within %ss" % timeout,
                                   dict(_spend(broker), broker_closed=broker.closed)) from error
            accounting = dict(_spend(broker), broker_closed=broker.closed)
        _refuse_if_closed(accounting)
        if completed.returncode != 0:
            raise SearchFailed("the adapter exited " + str(completed.returncode) + ": "
                               + completed.stderr[-400:], accounting)
        try:
            result = json.loads((output / "adapter-result.json").read_text(encoding="utf-8"))
            best = result.get("best") or {}
            code, metrics = best.get("code"), best.get("metrics")
            changes, differences = result["changes"], result["differences"]
        except (OSError, ValueError, KeyError) as error:
            # Exit 0 with no readable result is still a cell that spent, and the spend
            # outlives the unreadable file.
            raise SearchFailed("the adapter left no readable result: " + type(error).__name__
                               + ": " + str(error)[:200], accounting) from error
        if not code:
            raise SearchFailed("the OpenEvolve run produced no best program", accounting)
        program = directory / "candidate.py"
        program.write_text(code, encoding="utf-8", newline="\n")
        return {"program": str(program), "best_metrics": metrics, "mode": mode,
                "changes": changes, "differences": differences, **accounting}

    return search
