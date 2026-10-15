"""The evaluator our pilot arm is scored by: a thin delegate to the cell's broker.

The task ships a standalone evaluator (`symbolic_regression_task/evaluator.py`) that scores a
candidate in its own process. Our arm cannot use it, for a reason the broker enforces rather than
documents: a model call is admitted only once a valid evaluation has come back **through the
broker** (`program_broker.py:272-278`), so an arm that scored locally would have every one of its
model calls refused. Pointing our arm's evaluator here also removes an asymmetry rather than adding
one - `openevolve_adapter.evaluate` is this same delegate - so both arms are scored by one callback
on one set of selection rows, and the only difference left between them is the search.

**The endpoint is bound in, not read from the environment.** Our CLI scrubs the environment of every
candidate and evaluator process it launches and adds back only what it was told to
(`ProcessProgramExecutionEngine.ScrubEnvironment`), so an evaluator that read `EVOLUTION_BROKER_*`
would find nothing there. A dry run showed what that looks like: all six model calls were refused
for want of a valid evaluation, and the earlier dry run that *appeared* to work had been scoring the
default task rather than the cell's, because the task and seed it read from the environment were
scrubbed away too and it fell back to its defaults. Nothing is read from the environment now: the
task and the selection seed live with the broker's callback, so the search process is never told
which rows it is being scored on - and `bind` is what the per-cell evaluator file calls.
"""
from __future__ import annotations

from pathlib import Path
import sys

# This file is imported by path, so its directory is not on sys.path the way a __main__ script's
# would be; the task's own evaluator does the same for the same reason.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from program_broker import request  # noqa: E402

MAX_CODE_BYTES = 64 * 1024

# What `pilot_searchers.ours` writes into a cell directory, with the cell's own broker baked in.
CELL_EVALUATOR = '''"""Generated for one pilot cell: the broker's evaluator, under OpenEvolve's contract."""
import sys

sys.path.insert(0, {external!r})
import pilot_task_evaluator

evaluate = pilot_task_evaluator.bind({endpoint!r}, {capability!r})
'''


def bind(endpoint, capability):
    """Returns `evaluate(program_path)` in OpenEvolve's contract, scored by that broker."""
    def evaluate(program_path):
        code = Path(program_path).read_text(encoding="utf-8")
        if len(code.encode()) > MAX_CODE_BYTES:
            # Truncating would score a program that is not the one on disk, and the endpoint is
            # scored from disk - so this refuses instead of quietly evaluating something else.
            raise ValueError("candidate exceeds the broker's 64 KiB limit: " + str(program_path))
        result = request(endpoint, capability, "evaluate", {"code": code})
        if result["status"] != "valid":
            return {"combined_score": 0.0, "status": result["status"]}
        return {"combined_score": result["quality"], "status": "valid",
                "selection_nmse": result.get("selection_nmse")}

    return evaluate
