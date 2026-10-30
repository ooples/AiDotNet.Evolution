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

import contextlib
import importlib.util
import math
import tempfile
import time
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import symbolic_regression_family as family  # noqa: E402
from program_broker import COST_METRICS, ProgramBroker  # noqa: E402
from program_controls import candidate_hash  # noqa: E402

MAX_TEXT_BYTES = 64 * 1024
# Training rows shown to the model per evaluation, with the candidate's prediction and residual.
FEEDBACK_ROWS = 8


def model_callback(provider, *, cost_metric=COST_METRICS[1]):
    """Wraps a provider callable into the broker's `generate` contract.

    `provider(system, messages) -> (text, cost_units)`. The wrapper is where the broker's shape is
    enforced, so a provider cannot close admission by returning a float cost or oversized text.

    `cost_metric` names what `cost_units` counted, and the broker refuses a reply without it
    (`program_broker.py:299-302`) rather than assuming one - two providers that both say "tokens"
    can differ by whether cache reads are in the number. The default is the subscription CLI's
    metric, which includes cache reads; a provider that reports input+output only must say so.

    **A provider that already answers in the broker's shape is passed through, not rebuilt.**
    `claude_transport.ClaudeTransport.generate_metered` returns its own `cost_metric` and also
    `throttle_seconds` and `rate_limit_events`, which the broker records apart from search work;
    re-deriving a reply from its text and cost would silently drop the backoff accounting and
    substitute this module's metric for the transport's own. So a mapping reply is validated
    against the same rules and handed on intact.
    """
    if cost_metric not in COST_METRICS:
        raise ValueError("cost_metric must be one of %s, not %r" % (COST_METRICS, cost_metric))

    def generate(system, messages, model=None):
        measured = provider(system, messages) if model is None else provider(system, messages, model)
        if isinstance(measured, dict):
            reply = dict(measured)
            if reply.get("cost_metric") not in COST_METRICS:
                raise ValueError("the provider's cost_metric is not one the broker accepts: "
                                 + repr(reply.get("cost_metric")))
        else:
            text, cost_units = measured
            reply = {"text": text, "cost_units": cost_units, "cost_metric": cost_metric}
        if not isinstance(reply.get("text"), str):
            raise TypeError("the provider returned no text")
        encoded = reply["text"].encode()
        if len(encoded) > MAX_TEXT_BYTES:
            raise ValueError("the provider returned %d bytes, over the broker's 64 KiB limit" % len(encoded))
        if type(reply.get("cost_units")) is not int or reply["cost_units"] < 0:
            raise TypeError("cost_units must be a non-negative int, not "
                            + type(reply.get("cost_units")).__name__)
        return reply

    return generate


def evaluate_callback(task, seed, *, train=60, validation=20, feedback_rows=FEEDBACK_ROWS):
    """Scores candidate code on the selection rows, in the broker's `evaluate` shape.

    Invalid is a status, not an exception: a candidate that will not import, has no `predict`, or
    returns something unscorable is `invalid` with no quality, exactly as a broken program is for
    OpenEvolve's own examples. An exception here would close the broker and lose the whole cell.

    **Every reply carries `candidate_hash`, `unknown_work` and a measured `work_units`.** The broker
    does not treat those as optional: a reply whose hash does not reconcile with the program it
    dispatched, or that leaves work unaccounted, raises "Evaluator did not reconcile the dispatched
    program" and closes admission (`program_broker.py:318-323`). An earlier version of this callback
    omitted them and would have closed the broker on the first candidate of the first cell.
    `work_units` is the evaluation's own wall time, which is what the broker's evaluation-seconds
    budget is denominated in.

    **A valid reply also carries `artifacts`**: the task's inputs and ranges, and `feedback_rows` training rows
    with the candidate's prediction and residual (`feedback`). E-4's pilots gave the model a scalar score alone,
    and every arm's candidates stayed within 0.21 standard errors of the seed. Both arms read the same reply,
    so both see the same rows; the rows are training rows only, never validation or held-out.
    """
    if task not in family.TASKS:
        raise KeyError("Unknown task: " + task)
    rows = family.selection(task, seed, train=train, validation=validation)

    def evaluate(code):
        reply = {"candidate_hash": candidate_hash(code), "unknown_work": False}
        started = time.perf_counter()
        shown = None
        with tempfile.TemporaryDirectory(prefix="pilot-candidate-") as directory:
            path = Path(directory) / "candidate.py"
            path.write_text(code, encoding="utf-8")
            try:
                predict = _load(path)
                train_score, train_error = family.nmse_and_error(predict, rows["train"])
                validation_score, validation_error = family.nmse_and_error(predict, rows["validation"])
                score = (train_score + validation_score) / 2.0
                score_error = math.sqrt(train_error * train_error + validation_error * validation_error) / 2.0
                shown = feedback(predict, task, rows["train"], feedback_rows) if math.isfinite(score) else None
            except Exception:  # noqa: BLE001 - a broken candidate is invalid, never a closed broker
                score = math.nan
        reply["work_units"] = time.perf_counter() - started
        if not math.isfinite(score):
            reply["status"] = "invalid"
            return reply
        # Minimising NMSE, and the broker's quality is higher-is-better.
        reply.update(status="valid", quality=1.0 / (1.0 + score), selection_nmse=score)
        # The quality's standard error, by the delta method through 1 / (1 + score). Every arm receives it; ours
        # can require a champion to beat the seed by more than it (--champion-standard-errors).
        if math.isfinite(score_error):
            reply["quality_standard_error"] = score_error / ((1.0 + score) * (1.0 + score))
        if shown is not None:
            reply["artifacts"] = {"training_rows": shown}
        return reply

    return evaluate


def feedback(predict, task, rows, count):
    """The task's inputs and ranges, then the first `count` of `rows` sorted by input: inputs, target, prediction, residual."""
    specification = family.TASKS[task]
    names = list(specification["variables"])
    lines = ["inputs: " + ", ".join("%s = xs[%d] in [%g, %g]" % (name, index, low, high)
                                    for index, (name, (low, high)) in enumerate(zip(names, specification["ranges"]))),
             "training rows (your prediction and residual = prediction - target):",
             " ".join(names + ["target", "prediction", "residual"])]
    for xs, y in sorted(rows[:count]):
        prediction = float(predict(*xs))
        lines.append(" ".join(["%.4f" % x for x in xs] + ["%.4f" % y, "%.4f" % prediction, "%+.4f" % (prediction - y)]))
    return "\n".join(lines)


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


def session(provider, task, *, model_calls, evaluations, seconds, initial, model_tokens,
            cost_metric=COST_METRICS[1], selection_rows=(60, 20)):
    """Returns `open_for(cell)`: a context manager holding one broker for exactly one cell.

    **A cell is the largest thing a broker can be.** `ProgramBroker` refuses a budget above 64 model
    calls, 65 evaluations or 3600 seconds (`program_broker.py:140-144`), so a campaign-wide broker is
    not merely untidy, it cannot be constructed. The selection rows say the same thing from the other
    side: `evaluate_callback` is bound to one seed, and cells differ by seed, so one broker across
    cells would score every cell's candidates on the first cell's data.

    Per cell is also what the broker's own rules were written for: the first evaluation must be the
    initial program, and a cell does start from the initial program. Spend is then the broker's
    total - no cross-cell delta to get wrong - and a cell whose broker closed is a failed cell rather
    than a cheap-looking one.
    """
    @contextlib.contextmanager
    def open_for(cell):
        with ProgramBroker(model_callback(provider, cost_metric=cost_metric),
                           evaluate_callback(str(cell["Family"]), int(cell["Seed"]),
                                             train=selection_rows[0], validation=selection_rows[1]),
                           model_calls=model_calls, evaluations=evaluations, seconds=seconds,
                           initial=initial, model_tokens=model_tokens) as broker:
            yield broker

    return open_for
