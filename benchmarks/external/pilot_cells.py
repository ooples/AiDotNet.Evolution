"""The execute_cell campaign_runner has never had: one pilot cell, run and scored on held-out data.

V1-07 (#116) is a paired pilot, and `campaign_runner.run` takes an `execute_cell(cell, directory,
resume)` that it has no production implementation of - its only callers have been its own test and
the CI line that runs that test. This is that implementation for the symbolic-regression family.

The split of responsibility is the point:

- **A searcher** produces a candidate program for one arm. One per arm, injected, so the real run
  hands in our CLI and the OpenEvolve adapter while a test hands in a deterministic stub and the
  whole cell path is exercised without spending anything.
- **This module** owns the endpoint. It scores the winner on the **sealed** test split through
  `symbolic_regression_family.score`, which refuses a split whose rows no longer match their seal.
  A searcher cannot report its own number, and a cell whose seal fails is `failed`, never a quiet
  value.

`Value` is the held-out NMSE, which the registration declares minimizing with a failure value - so a
cell that raises, times out, or returns an unscorable program is `failed` and the analysis substitutes
that failure value rather than dropping the cell.
"""
from __future__ import annotations

import importlib.util
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import symbolic_regression_family as family  # noqa: E402

ARM_OURS = "aidotnet"


def load_predict(program_path):
    """Loads `predict` from a candidate file, raising if it is absent or unloadable."""
    spec = importlib.util.spec_from_file_location("pilot_candidate", program_path)
    if spec is None or spec.loader is None:
        raise ImportError("cannot load candidate " + str(program_path))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    predict = getattr(module, "predict", None)
    if not callable(predict):
        raise AttributeError("the candidate defines no predict(*xs)")
    return predict


def score_held_out(task, holdout_seed, program_path):
    """The endpoint: held-out NMSE on a split built from a seed the candidate never saw.

    `holdout_seed` belongs to this side of the seam. Review found that deriving it from the cell's
    selection seed let a candidate rebuild the endpoint's rows from its own environment, so it is now
    passed in and the search environment never carries it.
    """
    split = family.holdout(task, holdout_seed)
    predict = load_predict(program_path)
    if family.seal(split["test"]) != split["test_seal"]:
        raise ValueError("The held-out split does not match its seal, so its score would not be held out.")
    return family.nmse(predict, split["test"])


def holdout_seal(task, holdout_seed):
    """The sealed test partition's SHA-256, for the registration to commit to before launch.

    `campaign_registration` takes `TestPartitionHashes` on trust - nothing downstream recomputes it -
    so a placeholder there buys nothing: the commitment is only worth something if it is the hash of
    the rows the endpoint will actually be scored on. This is the one place that knows both, and the
    hash reveals nothing about the rows.
    """
    return family.holdout(task, holdout_seed)["test_seal"]


def executor(task, searchers, *, holdout_seed):
    """Returns an `execute_cell` for one family, dispatching on the cell's arm.

    `searchers` maps an arm name to `search(cell, directory, resume) -> dict` carrying at least
    `program` (a path) and, for a real run, `model_calls` and `model_tokens`. An arm with no searcher
    is a refusal at launch rather than a failed cell: a missing arm is a campaign that was not
    configured, not a result. A searcher that raises may carry an `accounting` attribute on the
    exception; that is how a cell that spent and then failed still reports its spend.
    """
    if task not in family.TASKS:
        raise KeyError("Unknown task: " + task)
    if not searchers:
        raise ValueError("A pilot needs at least one arm's searcher")
    if type(holdout_seed) is not int:
        raise TypeError("The endpoint needs an explicit integer holdout seed")

    def execute_cell(cell, directory, resume):
        arm = cell["Arm"]
        if arm not in searchers:
            raise KeyError("No searcher declared for arm " + arm)
        try:
            outcome = searchers[arm](cell, Path(directory), resume)
            program = outcome["program"]
            value = score_held_out(task, holdout_seed, program)
            if not math.isfinite(value):
                return _failed(outcome, "non-finite held-out score")
        except Exception as error:  # noqa: BLE001 - one cell's failure is that cell's outcome
            # A searcher that failed after spending attaches what it spent to the exception
            # (`pilot_searchers.SearchFailed`). Without this the row reported no calls for a cell
            # that really was billed, and the campaign's spend came out under the truth.
            return _failed(locals().get("outcome") or getattr(error, "accounting", None),
                           type(error).__name__)
        return {
            "Status": "completed",
            "Value": value,
            "ModelCalls": outcome.get("model_calls"),
            "ModelTokens": outcome.get("model_tokens"),
            "ThrottleSeconds": float(outcome.get("throttle_seconds", 0.0)),
            "LostCalls": int(outcome.get("lost_calls", 0)),
        }

    return execute_cell


def _failed(outcome, reason):
    outcome = outcome or {}
    return {
        "Status": "failed",
        "Reason": reason,
        "ModelCalls": outcome.get("model_calls"),
        "ModelTokens": outcome.get("model_tokens"),
        "ThrottleSeconds": float(outcome.get("throttle_seconds", 0.0)),
        "LostCalls": int(outcome.get("lost_calls", 0)),
    }
