"""V1-07 (#116): the pilot driver - registration, budget confirmation, both arms, one report.

What this adds over the pieces it composes is the order they have to happen in, and the fact that
none of it is optional:

- `campaign_registration.register` **draws the seeds itself** and hashes the frozen registration, so
  the operator cannot choose them after seeing anything.
- `campaign_runner.project` prints the budget ceiling and `run` refuses to launch unless the
  projection's own digest is handed back, so a campaign cannot start without someone having read what
  it may spend.
- The broker is opened **once per cell**, with the frozen `Budget` as its ceiling, so the registered
  budget is what actually stops a cell rather than a number in a file. A broker cannot be wider than
  a cell anyway - see `pilot_broker.session`.
- `campaign_registration.analyze` turns the rows into per-endpoint statistics; this never computes a
  verdict of its own.

Searchers are injected. The real run passes `pilot_searchers.ours` and `.openevolve`; a test passes
stubs and exercises the whole path for nothing.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "analysis"))
import campaign_registration as registration  # noqa: E402
import campaign_runner as runner  # noqa: E402
import pilot_broker  # noqa: E402
import pilot_cells  # noqa: E402
import power_analysis  # noqa: E402


def spec(task, *, seed_count, model, model_calls, model_tokens, wall_seconds,
         bootstrap_samples=1000, bootstrap_seed=11, familywise_alpha=0.05, partition_hash=None):
    """The campaign as declared, in the shape `register` validates.

    The field set is **closed** - `register` refuses a spec with any extra key, which is how an
    undeclared knob cannot ride along into a frozen registration. So the per-arm iteration count is
    not here: it is a searcher argument, and what the registration fixes is the ceiling,
    `Budget.ModelCalls` per cell, which the projection multiplies out before launch.
    """
    return {
        "Campaign": "v1-07-pilot-" + task,
        "Budget": {"ModelTokens": model_tokens, "ModelCalls": model_calls, "WallSeconds": wall_seconds},
        "Models": [model],
        "SeedCount": seed_count,
        "BootstrapSamples": bootstrap_samples,
        "BootstrapSeed": bootstrap_seed,
        "FamilywiseAlpha": familywise_alpha,
        "Families": [{"Family": task, "Configs": ["recommended", "matched"],
                      "Endpoint": {"Metric": "nmse", "Direction": "minimize", "FailureValue": 1e6}}],
        "TestPartitionHashes": {task: partition_hash or ("0" * 64)},
        "Published": None,
    }


def freeze(declared, directory):
    """Freezes the registration and returns `(frozen, registration_path, projection)`.

    `register` refuses a directory that already exists, so the path is returned rather than
    recomputed by a caller that might point at a different one.
    """
    directory = Path(directory)
    frozen = registration.register(declared, directory)
    return frozen, directory / "registration.json", runner.project(frozen)


def cell_broker(frozen, provider, task, initial, **options):
    """The per-cell broker, with the frozen registration's own budget as its ceiling.

    The budget is registered per cell (`Budget.ModelCalls`, which `project` multiplies out over the
    schedule), and this is where it becomes enforcement: the broker refuses the call after the last
    one the campaign declared, so an arm cannot overspend its cell even if its own stopping rule
    would let it. `evaluations` is the same ceiling plus two, because an arm evaluates the seed
    before it proposes anything and may evaluate once more than it proposed; a tighter cap would
    silently leave a candidate unscored rather than refusing a call.
    """
    budget = frozen["Spec"]["Budget"]
    return pilot_broker.session(provider, task, model_calls=budget["ModelCalls"],
                                evaluations=budget["ModelCalls"] + 2, seconds=budget["WallSeconds"],
                                initial=initial, model_tokens=budget["ModelTokens"], **options)


def run(frozen, registration_path, projection, state, task, searchers, *, holdout_seed, confirm, log=print):
    """Runs every cell once and returns `(rows, report)`. `confirm` must be the projection's digest."""
    if confirm != projection["ProjectionSha256"]:
        raise ValueError("Launch refused: pass the printed ProjectionSha256 as confirmation")
    execute = pilot_cells.executor(task, searchers, holdout_seed=holdout_seed)
    rows = runner.run(Path(registration_path), state, execute,
                      confirm=projection["ProjectionSha256"], log=log)
    return rows, registration.analyze(frozen, rows)


def power(frozen, rows, report, *, half_width, confidence=0.95):
    """V1-07's second criterion: per-endpoint variance and the N a target CI width needs.

    The pilot exists to size the gate campaign, and that number comes from the variance it measured
    rather than from an effect anyone anticipated - `power_analysis` reports two estimators of it and
    takes the larger, because six seeds cannot say which is right. Separate from `analyze` on
    purpose: the report is byte-reproducible evidence and this is a projection off it.
    """
    return power_analysis.analyse(frozen, rows, report, half_width=half_width, confidence=confidence)


def power_lines(analysis):
    return power_analysis.lines(analysis)


def ranked_losses(frozen, rows):
    """V1-07's third criterion: every cell the competitor won, worst margin first.

    The report says whether we are superior; it does not say *where we lose*, and a mean difference
    of +0.33 can be an arm that wins four cells and is beaten badly in two. This ranks the beaten
    cells so the ordering - not an impression of the table - is what sets workstream C's priority.

    The endpoint's own `Direction` decides what losing means, so a maximised metric is not silently
    read as a minimised one. A pair where either side did not complete is **not** a loss and not a
    win: it is incomparable, counted separately rather than dropped, because a dropped pair would
    quietly shorten the denominator the ranking is read against.
    """
    cells = {(row["Family"], row["Arm"], row["Seed"]): row for row in rows}
    out = []
    for family in frozen["Spec"]["Families"]:
        name = family["Family"]
        minimise = family["Endpoint"]["Direction"] == "minimize"
        for config in family["Configs"]:
            losses, incomparable = [], 0
            for seed in frozen["Seeds"]:
                mine = cells.get((name, registration.OURS, seed))
                yours = cells.get((name, "openevolve-" + config, seed))
                if (mine is None or yours is None or mine["Status"] != "completed"
                        or yours["Status"] != "completed" or mine["Value"] is None or yours["Value"] is None):
                    incomparable += 1
                    continue
                ours, theirs = float(mine["Value"]), float(yours["Value"])
                margin = ours - theirs if minimise else theirs - ours
                if margin > 0:
                    losses.append({"Seed": seed, "Ours": ours, "Theirs": theirs, "Margin": margin})
            # Worst margin first, and the seed breaks a tie so the order is a function of the rows
            # rather than of the dict iteration that produced them.
            losses.sort(key=lambda loss: (-loss["Margin"], loss["Seed"]))
            out.append({"Family": name, "Config": config, "Metric": family["Endpoint"]["Metric"],
                        "Direction": family["Endpoint"]["Direction"], "ComparablePairs": len(frozen["Seeds"]) - incomparable,
                        "IncomparablePairs": incomparable, "LostCells": len(losses),
                        "WorstMargin": losses[0]["Margin"] if losses else None, "Losses": losses})
    # The endpoint we lose most often comes first, and a tie there is broken by the worst single
    # margin: that pair is the ordering workstream C is meant to read top-down.
    out.sort(key=lambda row: (-row["LostCells"], -(row["WorstMargin"] or 0.0), row["Family"], row["Config"]))
    return {"Schema": "evolution-pilot-losses-v1", "RegistrationSha256": frozen["RegistrationSha256"],
            "Endpoints": out}


def loss_lines(losses):
    """The ranking as text, because this is the list a priority call is actually made from."""
    lines = []
    for row in losses["Endpoints"]:
        lines.append("%-22s %-12s lost %d/%d cells%s worst=%s"
                     % (row["Family"], row["Config"], row["LostCells"], row["ComparablePairs"],
                        "" if not row["IncomparablePairs"] else
                        " (%d incomparable)" % row["IncomparablePairs"],
                        "none" if row["WorstMargin"] is None else "%+.4f" % row["WorstMargin"]))
        for loss in row["Losses"]:
            lines.append("    seed %-11s ours=%.4f theirs=%.4f margin=%+.4f"
                         % (loss["Seed"], loss["Ours"], loss["Theirs"], loss["Margin"]))
    if not any(row["LostCells"] for row in losses["Endpoints"]):
        lines.append("no cell was lost on any endpoint")
    return lines


def report_lines(report):
    """One line per endpoint, so a reader sees the losses as plainly as the wins."""
    lines = []
    for endpoint in report["Endpoints"]:
        lines.append("%-22s %-12s mean_diff=%+.4f p=%.4g superior=%s failures=%s" % (
            endpoint["Family"], endpoint["Config"], endpoint["MeanDifference"], endpoint["OneSidedP"],
            endpoint["Superior"], json.dumps(endpoint["Failures"], sort_keys=True)))
    return lines
