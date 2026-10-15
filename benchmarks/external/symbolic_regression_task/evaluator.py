"""OpenEvolve-contract evaluator for the symbolic-regression pilot task.

`evaluate(program_path)` is the whole contract, as in OpenEvolve: load the candidate, score it, and
return a metrics dict whose `combined_score` is higher-is-better. Both pilot arms run this exact
file - ours through `aidotnet-evolve run --openevolve-config`, OpenEvolve's through its own runner -
so neither searches a task the other does not.

**The held-out split is never read here.** Selection sees train and validation only; the pilot scores
the sealed test split afterwards with `symbolic_regression_family.score`, which refuses a split whose
rows no longer match their seal. An evaluator that could see the test rows would make the pilot's
endpoint a training score.

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
    split = family.build(TASK, SEED)
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
