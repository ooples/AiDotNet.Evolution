# Runs an unmodified OpenEvolve 0.3.2 evaluator inside the aidotnet-evolve sandbox.
# The CLI prepends a CONFIG dict. The candidate arrives on standard input, and one JSON object goes to standard output:
#   {"quality": <fitness>, "metrics": {name: number}, "artifacts": {name: text}}
# The semantics are copied from openevolve/evaluator.py and openevolve/utils/metrics_utils.py (0.3.2, 411fb59):
# evaluate(program_path), or evaluate_stage1..3 with cascade_thresholds; the threshold uses combined_score, or else the
# mean of numeric non-"error" metrics; fitness uses combined_score, or else the mean of numeric non-bool, non-NaN
# metrics outside feature_dimensions.
import importlib.util
import json
import math
import os
import sys
import tempfile
import threading
import traceback


def _load_module(path):
    directory = os.path.dirname(os.path.abspath(path))
    if directory not in sys.path:
        sys.path.insert(0, directory)  # OpenEvolve evaluators import helpers that sit beside them
    spec = importlib.util.spec_from_file_location("openevolve_evaluator", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _as_result(value):
    # openevolve.evaluation_result.EvaluationResult has .metrics and .artifacts; a dict is metrics alone.
    if isinstance(value, dict):
        return dict(value), {}
    metrics = getattr(value, "metrics", None)
    if isinstance(metrics, dict):
        artifacts = getattr(value, "artifacts", None)
        return dict(metrics), dict(artifacts) if isinstance(artifacts, dict) else {}
    return {"error": 0.0}, {}


def _call(function, program_path, timeout):
    # OpenEvolve waits with asyncio.wait_for on a worker thread; the thread is left running, and so is this one (daemon).
    box = {}

    def run():
        try:
            box["value"] = function(program_path)
        except BaseException as error:  # noqa: BLE001 - mirrored as OpenEvolve's stage failure, not raised
            box["error"] = error
            box["traceback"] = traceback.format_exc()

    worker = threading.Thread(target=run, daemon=True)
    worker.start()
    worker.join(timeout)
    if worker.is_alive():
        raise TimeoutError()
    if "error" in box:
        raise _StageError(box["error"], box["traceback"])
    return box["value"]


class _StageError(Exception):
    def __init__(self, error, trace):
        super().__init__(str(error))
        self.trace = trace


def _passes_threshold(metrics, threshold):
    if not metrics:
        return False
    if "combined_score" in metrics:
        score = metrics.get("combined_score")
        if isinstance(score, (int, float)):
            return float(score) >= threshold
    valid = [float(v) for k, v in metrics.items() if k != "error" and isinstance(v, (int, float))]
    return bool(valid) and sum(valid) / len(valid) >= threshold


def _fitness(metrics, feature_dimensions):
    if not metrics:
        return 0.0
    if "combined_score" in metrics:
        try:
            return float(metrics["combined_score"])
        except (ValueError, TypeError):
            pass
    values = []
    for key, value in metrics.items():
        if key in feature_dimensions or isinstance(value, bool) or not isinstance(value, (int, float)):
            continue
        number = float(value)
        if number == number:
            values.append(number)
    return sum(values) / len(values) if values else 0.0


def _merge(first, second):
    merged = {k: float(v) for k, v in first.items() if isinstance(v, (int, float)) and k != "error"}
    merged.update({k: float(v) for k, v in second.items() if isinstance(v, (int, float)) and k != "error"})
    return merged


def _cascade(module, path, config):
    thresholds, timeout = config["cascade_thresholds"], config["timeout"]
    try:
        metrics, artifacts = _as_result(_call(module.evaluate_stage1, path, timeout))
    except TimeoutError:
        return {"stage1_passed": 0.0, "error": 0.0, "timeout": True}, {"failure_stage": "stage1", "timeout": True}
    except _StageError as error:
        return {"stage1_passed": 0.0, "error": 0.0}, {"failure_stage": "stage1", "stderr": str(error), "traceback": error.trace}
    if not thresholds or not _passes_threshold(metrics, thresholds[0]) or not hasattr(module, "evaluate_stage2"):
        return metrics, artifacts
    for stage, index in (("stage2", 1), ("stage3", 2)):
        if stage == "stage3" and (len(thresholds) < 2 or not _passes_threshold(metrics, thresholds[1])
                                  or not hasattr(module, "evaluate_stage3")):
            return metrics, artifacts
        try:
            later, later_artifacts = _as_result(_call(getattr(module, "evaluate_" + stage), path, timeout))
        except TimeoutError:
            artifacts.update({stage + "_timeout": True, "failure_stage": stage})
            metrics[stage + "_passed"] = 0.0
            metrics["timeout"] = True
            return metrics, artifacts
        except _StageError as error:
            artifacts.update({stage + "_stderr": str(error), stage + "_traceback": error.trace, "failure_stage": stage})
            metrics[stage + "_passed"] = 0.0
            return metrics, artifacts
        metrics = _merge(metrics, later)
        artifacts = {**artifacts, **later_artifacts}
    return metrics, artifacts


def _verify(config):
    import hashlib
    with open(config["evaluator_path"], "rb") as evaluator:
        digest = hashlib.sha256(evaluator.read()).hexdigest()
    if digest != config["evaluator_sha256"]:
        raise SystemExit("the evaluator changed after the run started: " + config["evaluator_path"])


def main():
    config = CONFIG  # noqa: F821 - prepended by the CLI
    _verify(config)
    source = sys.stdin.read()
    # OpenEvolve evaluators print progress to stdout, which is where the result goes. Point stdout at stderr while the
    # evaluator runs, at the descriptor level so processes it starts are redirected too, and keep the original for the
    # result.
    sys.stdout.flush()
    result_stream = os.fdopen(os.dup(1), "w", encoding="utf-8")
    os.dup2(2, 1)
    sys.stdout = sys.stderr
    handle, path = tempfile.mkstemp(suffix=config["file_suffix"], text=True)
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as candidate:
            candidate.write(source)
        module = _load_module(config["evaluator_path"])
        if config["cascade_evaluation"] and hasattr(module, "evaluate_stage1"):
            metrics, artifacts = _cascade(module, path, config)
        else:
            try:
                metrics, artifacts = _as_result(_call(module.evaluate, path, config["timeout"]))
            except TimeoutError:
                metrics, artifacts = {"error": 0.0, "timeout": True}, {"timeout": True}
            except _StageError as error:
                metrics, artifacts = {"error": 0.0}, {"stderr": str(error), "traceback": error.trace}
    finally:
        try:
            os.remove(path)
        except OSError:
            pass
    numeric = {k: float(v) for k, v in metrics.items()
               if isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(float(v))}
    text = {k: (v if isinstance(v, str) else json.dumps(v, default=str)) for k, v in artifacts.items()}
    quality = _fitness(metrics, config["feature_dimensions"])
    result_stream.write(json.dumps({"quality": quality if math.isfinite(quality) else 0.0, "metrics": numeric, "artifacts": text}))
    result_stream.write("\n")
    result_stream.flush()


# The CLI checks for this marker: the script's entry point is evaluate(program_path) in the user's file.
if __name__ == "__main__":
    main()
