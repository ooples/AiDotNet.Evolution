"""E-4 (#347): the capped pilot on AlphaEvolve problems - real models, three arms, one frozen registration.

    python run_alphaevolve_pilot.py --root DIR --cli dotnet <aidotnet-evolve.dll> --upstream <openevolve checkout>
        --executable <claude.exe> [--problem c5-erdos-overlap ...] [--seed-count 3] [--iterations 25]

The same order and the same two refusals as `run_pilot_campaign.py`: the run without `--confirm` freezes the seeds,
prints the projection and spends nothing; the confirming run must hand the projection's digest back; and each model's
canary must read its recorded baseline before any cell runs. `--stub` rehearses the whole path against a loopback
model for nothing - it proves the wiring, never a result.

Each problem is one family with two endpoints (ours against OpenEvolve `recommended`, and against `matched`), so a
five-problem pilot has ten endpoints under one Holm correction. The endpoint is the verifier's combined_score for the
program the search returned: the value as a fraction of AlphaEvolve's published one, higher is better, 1.0 matches it.
"""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import shutil
import sys
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "analysis"))

from claude_transport import reconcile_receipts  # noqa: E402
import alphaevolve_pilot as pilot  # noqa: E402
import alphaevolve_tasks as tasks  # noqa: E402
import alphaevolve_tasks_smoke as smoke  # noqa: E402
import campaign_registration as registration  # noqa: E402
import campaign_runner as runner  # noqa: E402
import pilot_campaign  # noqa: E402
import pilot_searchers  # noqa: E402
import program_broker  # noqa: E402
import run_pilot_campaign as symbolic  # noqa: E402

ADAPTER = HERE / "openevolve_adapter.py"
MODEL = symbolic.MODEL


def searchers_for(session, directories, *, cli, upstream, python_executable, iterations, timeout):
    """`{problem: {arm: search}}`, each problem's arms reading that problem's own task directory."""
    return {problem: {
        "aidotnet": pilot_searchers.ours(cli, directory, python_executable, iterations=iterations,
                                         session=session, timeout=timeout),
        "openevolve-recommended": pilot_searchers.openevolve(ADAPTER, upstream, directory, session,
                                                             mode="recommended", model=MODEL,
                                                             iterations=iterations, timeout=timeout),
        "openevolve-matched": pilot_searchers.openevolve(ADAPTER, upstream, directory, session,
                                                         mode="matched", model=MODEL,
                                                         iterations=iterations, timeout=timeout),
    } for problem, directory in directories.items()}


# Every file `tasks.write` puts in a problem's directory. A directory missing any of them (a crash mid-write) is
# regenerated rather than handed to a searcher that would fail on it cell after cell.
TASK_FILES = ("config.yaml", "initial_program.py", "evaluator.py", "support/floors.json") + tuple(
    "support/" + name for name in tasks.SUPPORT)


def task_directories(root, problems):
    """The generated task directories, written once beside the registration and reused by the confirming run."""
    base = Path(root) / "tasks"
    for problem in problems:
        directory = base / problem
        if directory.is_symlink():
            raise SystemExit("Refusing " + str(directory) + ": a task directory must not be a link")
        if directory.is_dir() and not all((directory / name).is_file() for name in TASK_FILES):
            # Only an incomplete directory is removed, and only after checking that it is one.
            shutil.rmtree(directory)
        elif directory.exists() and not directory.is_dir():
            raise SystemExit("Refusing " + str(directory) + ": it is not a task directory")
    missing = [problem for problem in problems if not (base / problem).exists()]
    if missing:
        tasks.write(base, missing, evaluator_timeout=pilot.ENGINE_EVALUATOR_TIMEOUT)
    return {problem: base / problem for problem in problems}


def resolve_python(parser, value):
    """`--python` as an absolute path: a bare name through PATH, anything with a separator from the working directory."""
    separators = [os.sep] + ([os.altsep] if os.altsep else [])
    if any(separator in value for separator in separators):
        return str(Path(value).absolute())
    found = shutil.which(value)
    if found is None:
        parser.error("--python %r is not on PATH" % value)
    return str(Path(found).absolute())


def frozen_iterations(path, requested):
    """The per-cell iteration count the first run froze, refusing a resume that changed it (or that cannot say).

    Frozen beside the registration rather than in it, as the sizing target is: `pilot_campaign.spec` is a closed
    field set. Without it a resumed campaign could mix cells searched for different numbers of iterations.
    """
    if not path.exists():
        raise SystemExit("Refusing " + str(path.parent) + ": it holds a frozen registration but no frozen iteration "
                         "count, so nothing says how long its cells searched")
    frozen = json.loads(path.read_text(encoding="utf-8"))["Iterations"]
    if frozen != requested:
        raise SystemExit("--iterations %r is not the %r this campaign froze; every cell searches for the same "
                         "number of iterations" % (requested, frozen))
    return frozen


def check_provider_mode(state, stub):
    """Records whether cells run against the stub or real models, and refuses to mix the two in one state directory.

    `campaign_runner` reuses finished cells, so a real run resumed over stub cells (or the reverse) would report
    loopback results as evidence. A state directory that already holds cells but no recorded mode is refused too.
    """
    mode = "stub" if stub else "models"
    path = Path(state) / "provider-mode.json"
    if path.exists():
        recorded = json.loads(path.read_text(encoding="utf-8"))["Mode"]
        if recorded != mode:
            raise SystemExit("Refusing " + str(state) + ": its cells ran against %s, and this run uses %s"
                             % (recorded, mode))
        return
    if Path(state).exists() and any(Path(state).iterdir()):
        raise SystemExit("Refusing " + str(state) + ": it holds cells but no recorded provider mode")
    Path(state).mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"Mode": mode}, indent=1) + "\n", encoding="utf-8")


def stub_answers(problems):
    """For `--stub`: each problem's whole-program answer, a construction that beats its seed."""
    return {problem: smoke.answer(smoke.improved(problem)) for problem in problems}


def describe(projection, arguments, problems):
    return [
        "problems             " + ", ".join(problems),
        "cells                %d (%d problems x %d seeds x 3 arms)" % (projection["Cells"], len(problems),
                                                                       arguments.seed_count),
        "ceiling              %d model calls, %d tokens, %d wall seconds"
        % (projection["MaxModelCalls"], projection["MaxModelTokens"], projection["MaxWallSeconds"]),
        "per cell             %d calls over %d iterations, which is the broker's own limit"
        % (arguments.model_calls, arguments.iterations),
        "construction limit   %d s in the broker; the engines' evaluator timeout is %d s"
        % (pilot.CONSTRUCT_TIMEOUT, pilot.ENGINE_EVALUATOR_TIMEOUT),
        "sizing target        CI half width %.4g, frozen with the seeds" % arguments.target_half_width,
        "ProjectionSha256     " + projection["ProjectionSha256"],
    ]


def summarise(rows, report, *, problems, arguments, frozen, projection, wall, canaries, receipts):
    return {
        "story": "E-4 (#347)",
        "problems": problems,
        "stub": arguments.stub,
        "registration_sha256": frozen["RegistrationSha256"],
        "projection_sha256": projection["ProjectionSha256"],
        "verifier_seals": frozen["Spec"]["TestPartitionHashes"],
        "iterations_per_cell": arguments.iterations,
        "construction_limit_seconds": pilot.CONSTRUCT_TIMEOUT,
        "wall_seconds": round(wall, 1),
        "cells": len(rows),
        "statuses": {status: sum(1 for row in rows if row["Status"] == status)
                     for status in sorted({row["Status"] for row in rows})},
        "model_calls": sum(row["ModelCalls"] or 0 for row in rows),
        "model_tokens": sum(row["ModelTokens"] or 0 for row in rows),
        "cells_with_unknown_spend": sum(1 for row in rows if row["ModelCalls"] is None),
        "canaries": canaries,
        "hardware": symbolic.describe_hardware(),
        "receipt_coverage": {name: record["coverage"] for name, record in receipts.items()},
        "endpoints": report["Endpoints"],
    }


def build_parser():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", required=True, help="output directory; must not already exist on the first run")
    parser.add_argument("--problem", action="append", choices=tasks.TASKS, help="repeat; default every task")
    parser.add_argument("--cli", nargs="+", required=True,
                        help="the aidotnet-evolve command, e.g. dotnet <path>/aidotnet-evolve.dll")
    parser.add_argument("--upstream", required=True, help="the pinned OpenEvolve checkout")
    parser.add_argument("--python", default=sys.executable, help="interpreter for constructions and the evaluator")
    parser.add_argument("--executable", help="the Claude CLI; omit only with --stub")
    # Three arms need six orders for a Williams balanced design, so seeds come in sixes (campaign_runner refuses any
    # other count at launch, after the projection was confirmed; this refuses it before anything is frozen).
    parser.add_argument("--seed-count", type=int, default=6, help="a positive multiple of 6")
    parser.add_argument("--iterations", type=int, default=25)
    parser.add_argument("--model-calls", type=int, default=35, help="per cell; the broker's ceiling")
    parser.add_argument("--model-tokens", type=int, default=1_100_000,
                        help="per cell; must leave the call ceiling binding first")
    parser.add_argument("--wall-seconds", type=int, default=3600, help="per cell")
    parser.add_argument("--cell-timeout", type=int, default=3600)
    parser.add_argument("--target-half-width", type=float, default=0.02,
                        help="CI half width of the published ratio the gate campaign must reach; sizes its N")
    parser.add_argument("--confirm", help="the printed ProjectionSha256; without it nothing runs")
    parser.add_argument("--stub", action="store_true", help="rehearse the whole path for no spend")
    return parser


def main(argv=None):
    parser = build_parser()
    arguments = parser.parse_args(argv)
    if not arguments.stub and not arguments.executable:
        parser.error("--executable is required unless --stub is given")
    problems = list(dict.fromkeys(arguments.problem or tasks.TASKS))
    if arguments.seed_count < 6 or arguments.seed_count % 6:
        raise SystemExit("--seed-count %d does not complete the three-arm position design; use a multiple of 6"
                         % arguments.seed_count)
    if not (math.isfinite(arguments.target_half_width) and arguments.target_half_width > 0):
        raise SystemExit("--target-half-width must be a positive, finite number; it sizes the gate campaign")
    most_calls = min(program_broker.MAX_MODEL_CALLS, program_broker.MAX_EVALUATIONS - pilot_campaign.EVALUATION_HEADROOM)
    if not 1 <= arguments.model_calls <= most_calls:
        raise SystemExit("--model-calls %d is outside the broker's 1..%d per cell" % (arguments.model_calls, most_calls))
    if not 0 < arguments.wall_seconds <= program_broker.MAX_SECONDS:
        raise SystemExit("--wall-seconds %r is outside the broker's (0, %d] per cell"
                         % (arguments.wall_seconds, program_broker.MAX_SECONDS))
    if arguments.model_tokens < arguments.model_calls * symbolic.TOKENS_PER_CALL:
        raise SystemExit("--model-tokens %d is below %d calls x ~%d tokens: the token ceiling would bind first"
                         % (arguments.model_tokens, arguments.model_calls, symbolic.TOKENS_PER_CALL))

    # Absolute, because every subprocess runs in a different working directory (see run_pilot_campaign.main).
    root = Path(arguments.root).resolve()
    arguments.upstream = str(Path(arguments.upstream).resolve())
    arguments.python = resolve_python(parser, arguments.python)
    registration_path = root / "registration" / "registration.json"
    sizing_path = root / "registration" / "sizing.json"
    iterations_path = root / "registration" / "iterations.json"
    if registration_path.exists():
        frozen = registration.load_registration(registration_path)
        projection = runner.project(frozen)
        registered = [family["Family"] for family in frozen["Spec"]["Families"]]
        if registered != problems:
            raise SystemExit("--problem %s is not the %s this registration froze" % (problems, registered))
        for problem in problems:
            if frozen["Spec"]["TestPartitionHashes"][problem] != pilot.verifier_seal(problem):
                raise SystemExit("The verifier for %s changed since the registration sealed it" % problem)
    elif root.exists():
        raise SystemExit("Refusing " + str(root) + ": it exists but holds no frozen registration")
    else:
        declared = pilot.spec(problems, seed_count=arguments.seed_count, model=MODEL,
                              model_calls=arguments.model_calls, model_tokens=arguments.model_tokens,
                              wall_seconds=arguments.wall_seconds)
        frozen, _, projection = pilot_campaign.freeze(declared, root / "registration")
        sizing_path.write_text(json.dumps({"TargetHalfWidth": arguments.target_half_width}, indent=1) + "\n",
                               encoding="utf-8")
        iterations_path.write_text(json.dumps({"Iterations": arguments.iterations}, indent=1) + "\n",
                                   encoding="utf-8")
    half_width = symbolic.frozen_half_width(sizing_path, arguments.target_half_width)
    frozen_iterations(iterations_path, arguments.iterations)
    directories = task_directories(root, problems)
    for line in describe(projection, arguments, problems):
        print(line)
    if arguments.confirm != projection["ProjectionSha256"]:
        print("\nNothing ran. Re-run the same command with --confirm " + projection["ProjectionSha256"])
        print("The registration is frozen at " + str(registration_path) + "; its seeds are already drawn.")
        return 2

    evidence = root / "evidence"
    evidence.mkdir(parents=True, exist_ok=True)
    transports = symbolic.transport_directory(evidence)
    initials = {problem: (directory / "initial_program.py").read_text(encoding="utf-8")
                for problem, directory in directories.items()}
    calls, canaries, models = [], None, None
    check_provider_mode(root / "state", arguments.stub)
    if arguments.stub:
        provider = pilot.stub_provider(calls, stub_answers(problems))
    else:
        models, canaries = symbolic.transport(arguments.executable, transports, projection["MaxModelCalls"] + 4,
                                              timeout=min(arguments.wall_seconds, 900))
        provider = models.generate
        print("canaries             " + json.dumps(canaries))

    session = pilot.cell_broker(frozen, provider, initials=initials, python=arguments.python)
    searchers = searchers_for(session, directories, cli=arguments.cli, upstream=arguments.upstream,
                              python_executable=arguments.python, iterations=arguments.iterations,
                              timeout=arguments.cell_timeout)
    started = time.monotonic()
    execute = pilot.executor(searchers, python=arguments.python)
    rows = runner.run(registration_path, root / "state", execute, confirm=projection["ProjectionSha256"], log=print)
    report = registration.analyze(frozen, rows)
    wall = time.monotonic() - started

    receipts = ({name: reconcile_receipts(transports / name) for name in models.transports}
                if models is not None else {})
    summary = summarise(rows, report, problems=problems, arguments=arguments, frozen=frozen, projection=projection,
                        wall=wall, canaries=canaries, receipts=receipts)
    # Raw results before the sizing, because the sizing can refuse (see run_pilot_campaign.main).
    (evidence / "rows.json").write_text(json.dumps(rows, indent=1) + "\n", encoding="utf-8")
    (evidence / "report.json").write_text(json.dumps(report, indent=1) + "\n", encoding="utf-8")
    losses = pilot_campaign.ranked_losses(frozen, rows)
    (evidence / "ranked-losses.json").write_text(json.dumps(losses, indent=1) + "\n", encoding="utf-8")
    try:
        analysis = pilot_campaign.power(frozen, rows, report, half_width=half_width)
    except ValueError as error:
        analysis = {"Schema": "evolution-power-analysis-v1", "Unachievable": str(error), "TargetHalfWidth": half_width}
    summary["power_analysis"] = analysis
    (evidence / "power-analysis.json").write_text(json.dumps(analysis, indent=1) + "\n", encoding="utf-8")
    (evidence / "summary.json").write_text(json.dumps(summary, indent=1, allow_nan=False) + "\n", encoding="utf-8")
    print("\n" + "\n".join(pilot_campaign.report_lines(report)))
    print("\n" + "\n".join(pilot_campaign.loss_lines(losses)))
    if "Unachievable" in analysis:
        print("\nsizing refused: " + analysis["Unachievable"])
    else:
        print("\n" + "\n".join(pilot_campaign.power_lines(analysis)))
    print("\n%d cells in %.1fs, %d model calls, statuses %s"
          % (len(rows), wall, summary["model_calls"], json.dumps(summary["statuses"])))
    for name, coverage in summary["receipt_coverage"].items():
        print("receipts %-8s coverage %s" % (name, coverage))
    failed = [row for row in rows if row["Status"] != "completed"]
    for row in failed:
        print("FAILED %s %-24s seed %s %s calls=%s"
              % (row["Family"], row["Arm"], row["Seed"], row.get("Reason"), row["ModelCalls"]))
    return symbolic.exit_status(failed, analysis)


if __name__ == "__main__":
    sys.exit(main())
