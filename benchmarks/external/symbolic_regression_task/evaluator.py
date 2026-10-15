"""OpenEvolve-contract evaluator for the symbolic-regression pilot task.

`evaluate(program_path)` is the whole contract, as in OpenEvolve: load the candidate, score it, and
return a metrics dict whose `combined_score` is higher-is-better.

**The metered pilot does not run this file; `pilot_task_evaluator.py` does.** Both arms' model calls
go through one broker, and the broker admits a call only after a valid evaluation has come back
through it, so a locally scoring evaluator would have had every call refused. This file is the same
metric (`family.selection`, mean of train and validation NMSE) in standalone form: it is what makes
the directory an ordinary OpenEvolve task that either engine can run with no broker at all, which
is the V1-56 (#169) capability, and it is what a reader should check the brokered delegate against.

**The held-out split is never built here, and cannot be rebuilt from here.** This calls
`family.selection`, which constructs train and validation only. Review found that constructing the
whole split was not enough on its own: a candidate can `import symbolic_regression_family` and rebuild
the held-out rows from `EVOLUTION_SR_TASK` and `EVOLUTION_SR_SEED`, because the old held-out seed was
`seed + TEST_SEED_OFFSET`. Reproduced before fixing - the rebuilt seal matched. The endpoint's seed is
now chosen by whoever scores the endpoint and is never placed in this process's environment.

**Residual risk, declared.** A candidate still executes in this process when it is imported, so it can
replace `family.nmse` with something that returns 0.0 and be handed `combined_score: 1.0`. The seal and
the separate holdout seed do not close that; only a restricted process does, and this repo has one
(`isolated_program_evaluator.py`, `ProgramSandboxOptions`). Until the pilot runs behind it, this
evaluator trusts the candidate not to subvert its own scorer, and a campaign that cares about an
adversarial candidate must not use this path.

`EVOLUTION_SR_TASK` and `EVOLUTION_SR_SEED` choose the task and the seed, so one file serves every
cell of the campaign. NMSE is minimizing, so the reported score is 1 / (1 + nmse): higher is better,
1.0 is exact, and a candidate that cannot be scored reports 0.0 rather than raising, which is how
OpenEvolve's own examples treat a broken candidate.
"""
import importlib.util
import math
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import symbolic_regression_family as family  # noqa: E402

TASK = os.environ.get("EVOLUTION_SR_TASK", "damped_oscillator")
SEED = int(os.environ.get("EVOLUTION_SR_SEED", "5000"))


def _load(program_path):
    spec = importlib.util.spec_from_file_location("sr_candidate", program_path)
    if spec is None or spec.loader is None:
        raise ImportError("cannot load candidate " + str(program_path))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    predict = getattr(module, "predict", None)
    if not callable(predict):
        raise AttributeError("the candidate defines no predict(*xs)")
    return predict


def evaluate(program_path):
    split = family.selection(TASK, SEED)
    try:
        predict = _load(program_path)
        selection = (family.nmse(predict, split["train"]) + family.nmse(predict, split["validation"])) / 2.0
    except Exception as error:  # noqa: BLE001 - a broken candidate scores worst, as upstream's examples do
        return {"combined_score": 0.0, "error": type(error).__name__, "task": TASK, "seed": SEED}
    if not math.isfinite(selection):
        return {"combined_score": 0.0, "selection_nmse": "inf", "task": TASK, "seed": SEED}
    return {
        "combined_score": 1.0 / (1.0 + selection),
        "selection_nmse": selection,
        "task": TASK,
        "seed": SEED,
    }
