"""A reference durable worker that serves an unmodified OpenEvolve evaluator.

OpenEvolve evaluators define ``evaluate(program_path)`` and return either a metrics ``dict`` or an
``EvaluationResult`` (an object with ``metrics`` and ``artifacts``). :func:`load_openevolve_evaluator`
loads such a file as OpenEvolve does, and :func:`serve` claims leases, evaluates each leased program and
commits one receipt per lease. The evaluator file is never edited or wrapped in source form.
"""
from __future__ import annotations

import importlib.util
import json
import math
import sys
import tempfile
from pathlib import Path
from typing import Any, Callable, Mapping

from .durable import DurableWorkClient

Evaluate = Callable[[str], dict[str, Any]]

_SCALARS = (bool, int, float, str)


def _json_value(value: Any) -> Any:
    """Metrics may hold numpy scalars; keep numbers and text exactly, stringify anything else."""
    if value is None or isinstance(value, _SCALARS):
        if isinstance(value, float) and not math.isfinite(value):
            return str(value)
        return value
    item = getattr(value, "item", None)  # numpy scalar -> Python scalar
    if callable(item):
        return _json_value(item())
    return str(value)


def _normalize(result: Any) -> dict[str, Any]:
    if isinstance(result, Mapping):
        metrics, artifacts = result, {}
    elif hasattr(result, "metrics"):
        metrics, artifacts = result.metrics, getattr(result, "artifacts", None) or {}
    else:
        raise TypeError("evaluate() must return a dict or an object with a 'metrics' mapping, not " + type(result).__name__)
    if not isinstance(metrics, Mapping):
        raise TypeError("the evaluator's metrics must be a mapping")
    return {
        "metrics": {str(key): _json_value(value) for key, value in metrics.items()},
        "artifacts": {str(key): _json_value(value) for key, value in dict(artifacts).items()},
    }


def load_openevolve_evaluator(path: str | Path, suffix: str = ".py") -> Evaluate:
    """Loads ``evaluate`` from an OpenEvolve evaluator file.

    As in OpenEvolve, the evaluator's directory goes on ``sys.path`` so its sibling imports resolve, and
    each candidate is written to its own temporary file whose path is passed to ``evaluate``.
    """
    file = Path(path).resolve()
    directory = str(file.parent)
    if directory not in sys.path:
        sys.path.insert(0, directory)
    spec = importlib.util.spec_from_file_location("aidotnet_openevolve_evaluator_" + str(abs(hash(str(file)))), file)
    if spec is None or spec.loader is None:
        raise ImportError("cannot load evaluator " + str(file))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    evaluate = getattr(module, "evaluate", None)
    if not callable(evaluate):
        raise AttributeError(str(file) + " defines no evaluate(program_path) function")

    def run(source: str) -> dict[str, Any]:
        with tempfile.TemporaryDirectory(prefix="aidotnet-candidate-") as scratch:
            program = Path(scratch) / ("program" + suffix)
            program.write_text(source, encoding="utf-8")
            return _normalize(evaluate(str(program)))

    return run


def serve(client: DurableWorkClient, worker: Mapping[str, Any], evaluate: Evaluate, *, provenance: str,
          actual: Mapping[str, str], max_leases: int | None = None) -> list[str]:
    """Claims and evaluates leases until none is available (or ``max_leases``), returning each commit disposition.

    A null claim means no work is available now, not that the search is complete; the caller decides whether to
    poll again. An evaluator exception commits a ``failed`` receipt carrying only the exception type. A commit that
    raises is propagated, never retried by re-running the evaluator: the durable host settles lost replies.
    """
    if max_leases is not None and max_leases < 1:
        raise ValueError("max_leases must be at least 1")
    dispositions: list[str] = []
    while max_leases is None or len(dispositions) < max_leases:
        lease = client.claim(worker)
        if lease is None:
            break
        try:
            payload, outcome = json.dumps(evaluate(lease["payload"]), sort_keys=True, allow_nan=False), "completed"
        except Exception as error:  # the evaluator is caller code; any failure is that lease's outcome
            payload, outcome = json.dumps({"error": type(error).__name__}), "failed"
        dispositions.append(client.commit({
            "identity": lease["identity"], "workerId": lease["workerId"], "payload": payload,
            "provenance": provenance, "actual": dict(actual), "outcome": outcome,
        }))
    return dispositions
