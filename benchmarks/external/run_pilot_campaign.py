"""V1-07 (#116): the live pilot campaign - real models, both arms, one frozen registration.

Everything the pilot needs already exists behind seams (`pilot_campaign`, `pilot_broker`,
`pilot_searchers`, `pilot_cells`); what this adds is the order it has to happen in when the calls
cost money, and the two refusals that stand between a command and a bill:

1. **The projection gate.** `freeze` draws the seeds itself and `project` prints the ceiling; the run
   refuses to start unless the operator hands that projection's own digest back with `--confirm`. So
   a campaign cannot launch without someone having read what it may spend, and running without
   `--confirm` is the way to see the number - it spends nothing.
2. **The canaries.** Each transport's fixed prompt must report exactly its recorded input-token
   baseline before any cell runs, which is how injected context (a CLAUDE.md, a changed system
   prompt) is caught rather than silently measured. A transport is warmed once first, because a cold
   first call can carry stale remote config, exactly as `smoke_openevolve_live.py` does it.

Spend is the broker's, per cell, with the frozen `Budget.ModelCalls` as the ceiling - see
`pilot_campaign.cell_broker`. `--stub` swaps the subscription transport for a loopback answer so the
whole path, gate included, can be rehearsed for nothing; it proves the wiring, never a result.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import sys
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "analysis"))

from claude_transport import ClaudeModelSet, reconcile_receipts  # noqa: E402
import campaign_registration as registration  # noqa: E402
import campaign_runner as runner  # noqa: E402
import pilot_broker  # noqa: E402
import pilot_campaign  # noqa: E402
import pilot_cells  # noqa: E402
import pilot_searchers  # noqa: E402

TASK_DIRECTORY = HERE / "symbolic_regression_task"
ADAPTER = HERE / "openevolve_adapter.py"
CANARIES = HERE / "claude_canary.json"
# The one model name all three arms send: our arm reads it from the task config verbatim, and
# openevolve_configs.TIERS maps it to itself, so the account sees one model rather than two.
MODEL = "sonnet"
# Measured on this account: a `claude -p` call carries ~31.6k cache-creation tokens whatever the
# prompt, and `cost_units` sums input, cache-creation, cache-read and output - so a call costs about
# this many of the broker's tokens. It is here so the two ceilings can be checked against each other
# rather than set independently: a token ceiling that binds before the call ceiling does not refuse a
# call, it shortens the search, and the row still reads as a completed cell.
# Measured over the 180 calls of the first real campaign: 478,903 broker tokens, **2,661 per
# call**. The 40,000 this started at came from a full interactive session's cache-creation cost
# and overestimated an isolated `claude -p` call by ~15x, which made the projection print 7.2M
# tokens for a campaign that spent 479k. Kept well above the measurement, because the ceiling
# this sizes must bind after the call ceiling rather than before it.
TOKENS_PER_CALL = 8_000


STUB_DIFF = "\n".join(["<<<<<<< SEARCH", "def predict(*xs):", "    return 0.0", "=======",
                       "def predict(*xs):", "    x = xs[0]", "    return (3.0 * x + 1.0) / (x * x + 2.0)",
                       ">>>>>>> REPLACE"])
STUB_FENCE = "```python\ndef predict(*xs):\n    x = xs[0]\n    return (3.0 * x + 1.0) / (x * x + 2.0)\n```"


def stub_provider(calls):
    """A loopback answer in whichever shape the prompt asked for, so one stub serves every arm.

    The imported config leaves OpenEvolve's `diff_based_evolution` default in place, so our arm and
    the recommended arm ask for SEARCH/REPLACE edits while the matched arm asks for a whole program.
    A stub that answered only one shape would have every proposal of the other arms abandoned after
    three calls - measured, and it reads as a working run.
    """
    def provider(system, messages, model=None):
        calls.append(model)
        text = (system or "") + " ".join(message.get("content", "") for message in messages)
        return (STUB_DIFF if "SEARCH" in text else STUB_FENCE), 30

    return provider


def transport(executable, evidence, max_calls, *, timeout):
    """The subscription transport for one model, canaries verified against the recorded baselines."""
    baselines = {name: row["canary_input_tokens"] for name, row
                 in json.loads(CANARIES.read_text(encoding="utf-8"))["models"].items()}
    if MODEL not in baselines:
        raise KeyError("No recorded canary baseline for " + MODEL)
    models = ClaudeModelSet(executable, [MODEL], evidence, max_calls, timeout=timeout)
    for name, single in models.transports.items():
        # Warm first, then hold it to the baseline: a cold first call can carry stale remote config.
        single.canary_input_tokens()
        single.canary_baseline = baselines[name]
    return models, models.verify_canaries()


def searchers_for(session, *, cli, upstream, python_executable, iterations, timeout):
    return {
        "aidotnet": pilot_searchers.ours(cli, TASK_DIRECTORY, python_executable,
                                         iterations=iterations, session=session, timeout=timeout),
        "openevolve-recommended": pilot_searchers.openevolve(ADAPTER, upstream, TASK_DIRECTORY, session,
                                                             mode="recommended", model=MODEL,
                                                             iterations=iterations, timeout=timeout),
        "openevolve-matched": pilot_searchers.openevolve(ADAPTER, upstream, TASK_DIRECTORY, session,
                                                         mode="matched", model=MODEL,
                                                         iterations=iterations, timeout=timeout),
    }


def transport_directory(evidence):
    """A fresh transport directory per attempt, because `ClaudeModelSet` refuses an existing one.

    A canary that fails, or a cell that dies, leaves the previous attempt's directory behind - so a
    fixed name would make the retry crash with `FileExistsError` instead of re-running, on a command
    someone is paying for. Both attempts' receipts are kept, which is what reconciliation wants.
    """
    stamp = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
    for suffix in [""] + ["-" + str(index) for index in range(2, 100)]:
        candidate = Path(evidence) / ("transport-" + stamp + suffix)
        if not candidate.exists():
            return candidate
    raise RuntimeError("a hundred attempts in one second is not a retry")


def summarise(rows, report, *, task, arguments, frozen, projection, wall, canaries, receipts):
    """The run's own account of itself, including what it could not account for.

    `sum(row["ModelCalls"] or 0)` reads a cell that failed before its broker could report as free,
    which is the same under-report the searchers' accounting exists to prevent - so the cells with no
    call count are counted beside the total rather than absorbed into it.
    """
    return {
        "story": "V1-07 (#116)",
        "task": task,
        "stub": arguments.stub,
        "registration_sha256": frozen["RegistrationSha256"],
        "projection_sha256": projection["ProjectionSha256"],
        "holdout_partition_sha256": frozen["Spec"]["TestPartitionHashes"][task],
        "iterations_per_cell": arguments.iterations,
        "wall_seconds": round(wall, 1),
        "cells": len(rows),
        "statuses": {status: sum(1 for row in rows if row["Status"] == status)
                     for status in sorted({row["Status"] for row in rows})},
        "model_calls": sum(row["ModelCalls"] or 0 for row in rows),
        "model_tokens": sum(row["ModelTokens"] or 0 for row in rows),
        "cells_with_unknown_spend": sum(1 for row in rows if row["ModelCalls"] is None),
        "canaries": canaries,
        # Coverage below 1.0 means an admitted call left no receipt, so the bill cannot be reconciled.
        "receipt_coverage": {name: record["coverage"] for name, record in receipts.items()},
        "endpoints": report["Endpoints"],
    }


def declare(task, arguments, holdout_seed):
    """The frozen spec, carrying the real sealed partition rather than a placeholder.

    `campaign_registration` takes `TestPartitionHashes` on trust and nothing downstream recomputes
    it, so a placeholder would commit to nothing. This is the hash of the rows the endpoint will
    actually be scored on, and it reveals nothing about them.
    """
    return pilot_campaign.spec(task, seed_count=arguments.seed_count, model=MODEL,
                               model_calls=arguments.model_calls, model_tokens=arguments.model_tokens,
                               wall_seconds=arguments.wall_seconds,
                               partition_hash=pilot_cells.holdout_seal(task, holdout_seed))


def describe(projection, arguments):
    balance = projection["PositionBalance"][arguments.task]
    return [
        "cells                %d (%d seeds x 3 arms, Williams balanced: %s)"
        % (projection["Cells"], arguments.seed_count, balance["Balanced"]),
        "ceiling              %d model calls, %d tokens, %d wall seconds"
        % (projection["MaxModelCalls"], projection["MaxModelTokens"], projection["MaxWallSeconds"]),
        "per cell             %d calls over %d iterations, which is the broker's own limit"
        % (arguments.model_calls, arguments.iterations),
        "rough cost           ~%d tokens if every call is served, at ~%dk per call"
        % (projection["MaxModelCalls"] * TOKENS_PER_CALL, TOKENS_PER_CALL // 1000),
        "sizing target        CI half width %.4g, frozen with the seeds" % arguments.target_half_width,
        "ProjectionSha256     " + projection["ProjectionSha256"],
    ]


def build_parser():
    """The entry point's parser, exposed so a test can read a default rather than restate it."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--task", default="rational_gain", help="a symbolic_regression_family task")
    parser.add_argument("--root", required=True, help="output directory; must not already exist")
    parser.add_argument("--cli", nargs="+", required=True,
                        help="the aidotnet-evolve command, e.g. dotnet <path>/aidotnet-evolve.dll")
    parser.add_argument("--upstream", required=True, help="the pinned OpenEvolve checkout")
    parser.add_argument("--python", default=sys.executable, help="interpreter for the evaluator")
    parser.add_argument("--executable", help="the Claude CLI; omit only with --stub")
    parser.add_argument("--holdout-seed", type=int, required=True,
                        help="the endpoint's seed; never reaches a search process")
    parser.add_argument("--seed-count", type=int, default=6, help="a multiple of 6 keeps positions balanced")
    parser.add_argument("--iterations", type=int, default=10)
    # 10 x 18 cells = 180 calls, which is what the authorised envelope buys at this account's rate.
    parser.add_argument("--model-calls", type=int, default=10, help="per cell; the broker's ceiling")
    parser.add_argument("--model-tokens", type=int, default=600_000,
                        help="per cell; must leave the call ceiling binding first")
    parser.add_argument("--wall-seconds", type=int, default=1800, help="per cell")
    parser.add_argument("--cell-timeout", type=int, default=1800)
    # V1-40 (#121): the gate campaign's N comes from the variance this pilot measures, against the
    # CI half width the pre-registration will declare. Reported, never acted on here.
    parser.add_argument("--target-half-width", type=float, default=0.05,
                        help="CI half width the gate campaign must reach; sizes its N")
    parser.add_argument("--confirm", help="the printed ProjectionSha256; without it nothing runs")
    parser.add_argument("--stub", action="store_true", help="rehearse the whole path for no spend")
    return parser


def frozen_half_width(path, requested):
    """The sizing target the first run froze, refusing a later run that changed it.

    The width is the denominator of the reported N, so a run free to change it after the seeds were
    drawn could pick the number the pilot reports - which is the one thing freezing a registration
    exists to prevent (review caught it). It is frozen beside the registration rather than inside
    it because `pilot_campaign.spec` is a closed field set the release gate validates: an undeclared
    knob cannot ride along into a frozen registration.
    """
    if not path.exists():
        raise SystemExit("Refusing " + str(path.parent) + ": it holds a frozen registration but no "
                         "frozen sizing target, so nothing says which width the reported N was for")
    frozen = json.loads(path.read_text(encoding="utf-8"))["TargetHalfWidth"]
    if frozen != requested:
        raise SystemExit("--target-half-width %r is not the %r this campaign froze with its seeds. "
                         "The width sizes the N the pilot reports, so it is not a choice left open "
                         "after the registration." % (requested, frozen))
    return frozen


def exit_status(failed, analysis):
    """Nonzero when the campaign has a hole in its paired design, or no size estimate to show for it.

    Both of those are still written to disk in full - the status is for the automated caller, which
    read a refused sizing as a successful campaign while every cell had completed, so a run with no
    number in it looked like a run with one (review caught it).
    """
    return 1 if failed or "Unachievable" in analysis else 0


def main():
    parser = build_parser()
    arguments = parser.parse_args()
    if not arguments.stub and not arguments.executable:
        parser.error("--executable is required unless --stub is given")

    if not (math.isfinite(arguments.target_half_width) and arguments.target_half_width > 0):
        raise SystemExit("--target-half-width must be a positive, finite number; it sizes the gate campaign")
    budget_tokens = arguments.model_calls * TOKENS_PER_CALL
    if arguments.model_tokens < budget_tokens:
        raise SystemExit("--model-tokens %d is below %d calls x ~%d tokens: the token ceiling would bind "
                         "first and silently shorten every cell instead of refusing a call"
                         % (arguments.model_tokens, arguments.model_calls, TOKENS_PER_CALL))
    root = Path(arguments.root)
    registration_path = root / "registration" / "registration.json"
    sizing_path = root / "registration" / "sizing.json"
    if registration_path.exists():
        # The second run is the one that launches, and it must use the seeds the first run drew -
        # re-freezing would draw new ones after the operator had seen the projection, which is the
        # whole thing a pre-registration exists to prevent.
        frozen = registration.load_registration(registration_path)
        projection = runner.project(frozen)
        declared_seal = pilot_cells.holdout_seal(arguments.task, arguments.holdout_seed)
        if frozen["Spec"]["TestPartitionHashes"].get(arguments.task) != declared_seal:
            raise SystemExit("This --holdout-seed does not match the partition the registration sealed; "
                             "the endpoint would be scored on rows nobody committed to.")
    elif root.exists():
        raise SystemExit("Refusing " + str(root) + ": it exists but holds no frozen registration")
    else:
        frozen, _, projection = pilot_campaign.freeze(
            declare(arguments.task, arguments, arguments.holdout_seed), root / "registration")
        sizing_path.write_text(json.dumps({"TargetHalfWidth": arguments.target_half_width}, indent=1)
                               + chr(10), encoding="utf-8")
    half_width = frozen_half_width(sizing_path, arguments.target_half_width)
    for line in describe(projection, arguments):
        print(line)
    if arguments.confirm != projection["ProjectionSha256"]:
        print("\nNothing ran. Re-run the same command with --confirm " + projection["ProjectionSha256"])
        print("The registration is frozen at " + str(registration_path) + "; its seeds are already")
        print("drawn, so the confirming run reuses them rather than drawing new ones.")
        raise SystemExit(2)

    evidence = root / "evidence"
    evidence.mkdir(parents=True, exist_ok=True)
    # One transport directory per attempt. `ClaudeModelSet` refuses a directory that already exists,
    # and a canary that fails (or a cell that dies) leaves one behind - so a single fixed name would
    # make the second attempt crash with FileExistsError instead of re-running, on a command someone
    # is paying for. Both attempts' receipts are kept, which is also what reconciliation wants.
    transports = transport_directory(evidence)
    initial = (TASK_DIRECTORY / "initial_program.py").read_text(encoding="utf-8")
    calls, canaries, models = [], None, None
    if arguments.stub:
        provider = stub_provider(calls)
    else:
        # The campaign's own ceiling plus the canaries: one warm call and one verify per model, and
        # one model. A looser number here would make the transport's cap - the last backstop against
        # a runaway, since the broker's is per cell - stop meaning anything.
        models, canaries = transport(arguments.executable, transports,
                                     projection["MaxModelCalls"] + 4,
                                     timeout=min(arguments.wall_seconds, 900))
        provider = models.generate
        print("canaries             " + json.dumps(canaries))

    session = pilot_campaign.cell_broker(frozen, provider, arguments.task, initial)
    searchers = searchers_for(session, cli=arguments.cli, upstream=arguments.upstream,
                              python_executable=arguments.python, iterations=arguments.iterations,
                              timeout=arguments.cell_timeout)
    started = time.monotonic()
    rows, report = pilot_campaign.run(frozen, registration_path, projection, root / "state",
                                      arguments.task, searchers, holdout_seed=arguments.holdout_seed,
                                      confirm=arguments.confirm)
    wall = time.monotonic() - started

    receipts = ({name: reconcile_receipts(transports / name) for name in models.transports}
                if models is not None else {})
    summary = summarise(rows, report, task=arguments.task, arguments=arguments, frozen=frozen,
                        projection=projection, wall=wall, canaries=canaries, receipts=receipts)
    # The raw results are written before the sizing is computed, because the sizing can refuse:
    # an endpoint needing more than the cap raises, and a campaign that cost real money would
    # otherwise lose rows.json, report.json and summary.json to a projection off them.
    (evidence / "rows.json").write_text(json.dumps(rows, indent=1) + chr(10), encoding="utf-8")
    (evidence / "report.json").write_text(json.dumps(report, indent=1) + chr(10), encoding="utf-8")
    # The ranking depends on the rows alone, so it is written here rather than after the sizing:
    # the projection can refuse, and where we lose is evidence in its own right.
    losses = pilot_campaign.ranked_losses(frozen, rows)
    (evidence / "ranked-losses.json").write_text(json.dumps(losses, indent=1) + chr(10), encoding="utf-8")
    try:
        analysis = pilot_campaign.power(frozen, rows, report, half_width=half_width)
    except ValueError as error:
        analysis = {"Schema": "evolution-power-analysis-v1", "Unachievable": str(error),
                    "TargetHalfWidth": half_width}
    summary["power_analysis"] = analysis
    (evidence / "power-analysis.json").write_text(json.dumps(analysis, indent=1) + chr(10), encoding="utf-8")
    (evidence / "summary.json").write_text(json.dumps(summary, indent=1, allow_nan=False) + "\n", encoding="utf-8")
    print("\n" + "\n".join(pilot_campaign.report_lines(report)))
    print(chr(10) + chr(10).join(pilot_campaign.loss_lines(losses)))
    if "Unachievable" in analysis:
        print(chr(10) + "sizing refused: " + analysis["Unachievable"])
        print("the campaign's rows, report and summary are written; only the projection is missing")
    else:
        print(chr(10) + chr(10).join(pilot_campaign.power_lines(analysis)))
    print("\n%d cells in %.1fs, %d model calls, statuses %s"
          % (len(rows), wall, summary["model_calls"], json.dumps(summary["statuses"])))
    if summary["cells_with_unknown_spend"]:
        print("%d cell(s) reported no call count, so the total above is a floor, not the bill"
              % summary["cells_with_unknown_spend"])
    for name, coverage in summary["receipt_coverage"].items():
        print("receipts %-8s coverage %s" % (name, coverage))
    failed = [row for row in rows if row["Status"] != "completed"]
    for row in failed:
        print("FAILED %-24s seed %s %s calls=%s" % (row["Arm"], row["Seed"], row.get("Reason"), row["ModelCalls"]))
    raise SystemExit(exit_status(failed, analysis))


if __name__ == "__main__":
    main()
