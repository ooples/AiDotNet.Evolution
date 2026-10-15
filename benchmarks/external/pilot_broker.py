"""The two callbacks the shared broker needs for a symbolic-regression pilot cell.

`openevolve_adapter.py` reaches a model only through `EVOLUTION_BROKER_ENDPOINT`, so the broker is
where both arms' spend is metered and where the task's evaluator actually runs. The broker's
contracts are strict, and two of them shape this module:

- `generate(system, messages)` must return `{"text": str, "cost_units": int}`, with the text under
  64 KiB. A provider that returns anything else closes admission rather than being coerced.
- **The first `evaluate` must be the initial program**, and no model call is admitted until some
  evaluation has come back valid (`program_broker.py:271-278`). So a pilot cell evaluates the seed
  before it proposes anything, and a driver that skipped that would be refused rather than quietly
  starting.

Scoring here is **selection only** - train and validation. The endpoint is scored outside this
process by `pilot_cells`, from a holdout seed the search never receives.
"""
from __future__ import annotations

import importlib.util
import math
import tempfile
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import symbolic_regression_family as family  # noqa: E402

MAX_TEXT_BYTES = 64 * 1024


def model_callback(provider):
    """Wraps a provider callable into the broker's `generate` contract.

    `provider(system, messages) -> (text, cost_units)`. The wrapper is where the broker's shape is
    enforced, so a provider cannot close admission by returning a float cost or oversized text.
    """
    def generate(system, messages, model=None):
        text, cost_units = provider(system, messages) if model is None else provider(system, messages, model)
        if not isinstance(text, str):
            raise TypeError("the provider returned no text")
        encoded = text.encode()
        if len(encoded) > MAX_TEXT_BYTES:
            raise ValueError("the provider returned %d bytes, over the broker's 64 KiB limit" % len(encoded))
        if type(cost_units) is not int or cost_units < 0:
            raise TypeError("cost_units must be a non-negative int, not " + type(cost_units).__name__)
        return {"text": text, "cost_units": cost_units}

    return generate


def evaluate_callback(task, seed):
    """Scores candidate code on the selection rows, in the broker's `evaluate` shape.

    Invalid is a status, not an exception: a candidate that will not import, has no `predict`, or
    returns something unscorable is `invalid` with no quality, exactly as a broken program is for
    OpenEvolve's own examples. An exception here would close the broker and lose the whole cell.
    """
    if task not in family.TASKS:
        raise KeyError("Unknown task: " + task)
    rows = family.selection(task, seed)

    def evaluate(code):
        with tempfile.TemporaryDirectory(prefix="pilot-candidate-") as directory:
            path = Path(directory) / "candidate.py"
            path.write_text(code, encoding="utf-8")
            try:
                predict = _load(path)
                score = (family.nmse(predict, rows["train"]) + family.nmse(predict, rows["validation"])) / 2.0
            except Exception:  # noqa: BLE001 - a broken candidate is invalid, never a closed broker
                return {"status": "invalid", "work_units": 0.0}
        if not math.isfinite(score):
            return {"status": "invalid", "work_units": 0.0}
        # Minimising NMSE, and the broker's quality is higher-is-better.
        return {"status": "valid", "quality": 1.0 / (1.0 + score), "work_units": 0.0,
                "selection_nmse": score}

    return evaluate


def _load(path):
    spec = importlib.util.spec_from_file_location("pilot_broker_candidate", path)
    if spec is None or spec.loader is None:
        raise ImportError("cannot load candidate " + str(path))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    predict = getattr(module, "predict", None)
    if not callable(predict):
        raise AttributeError("the candidate defines no predict(*xs)")
    return predict
