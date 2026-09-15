"""SciPy comparison on the actual US-01 evaluator, not reimplemented objective functions.

This is a development/contract adapter. It does not acquire registered holdout custody.
Native-sized initialization is explicitly not SciPy's untouched native defaults.
"""
from __future__ import annotations

import math
import time

from scipy_baseline import Bridge, versions


class EvaluationCap(Exception):
    """Normal stop, raised before dispatching work beyond the declared cap."""


def run_one(dll, task, instance_seed, search_seed, budget, expected_hash, *,
            mode="controlled", optimizer=None):
    if (type(budget) is not int or not 8 <= budget <= 4096
            or type(instance_seed) is not int or not 0 <= instance_seed < 2**32
            or type(search_seed) is not int or not 0 <= search_seed < 2**64
            or mode not in ("controlled", "native-sized")):
        raise ValueError("Invalid bounded suite comparison configuration")
    if mode == "native-sized" and budget < 120:
        raise ValueError("Native-sized population requires at least 120 evaluations")
    environment = versions()
    import numpy as np
    from scipy.optimize import differential_evolution
    from scipy.stats import qmc

    optimizer = optimizer or differential_evolution
    bridge = None
    manifest = summary = None
    samples = []
    dispatched = 0
    best = None
    stop = "failed"
    error = None
    started = time.perf_counter()
    settings = dict(strategy="best1bin", mutation=[0.5, 1.0], recombination=0.7, tol=0.01,
                    atol=0, polish=False, updating="immediate", workers=1, vectorized=False,
                    initial_population=8 if mode == "controlled" else 120, tuning_trials=0,
                    constraint_handling="infeasible objectives rank as positive infinity; raw violations retained",
                    initialization="shared eight" if mode == "controlled" else
                    "120 Latin-hypercube points with first eight replaced by shared starts; not untouched defaults")
    try:
        bridge = Bridge(dll, task, search_seed, budget, instance_seed=instance_seed)
        manifest = bridge.receive()
        expected = dict(Kind="manifest", Protocol="suite-numeric-objective-service-v1", Task=task,
                        Seed=search_seed, InstanceSeed=instance_seed, Budget=budget,
                        InitialPopulationHash=expected_hash, Dimensions=8)
        if any(manifest.get(key) != value for key, value in expected.items()):
            raise ValueError("Shared suite evaluator manifest mismatch")
        work = manifest["WorkUnitsPerEvaluation"]
        if type(work) is not int or work < 1:
            raise ValueError("Invalid evaluator work units")
        initial = np.array(manifest["InitialUnits"], dtype=float)
        if initial.shape != (8, 8) or not np.isfinite(initial).all():
            raise ValueError("Invalid shared initialization")
        if mode == "native-sized":
            population = qmc.LatinHypercube(d=8, rng=np.random.default_rng(search_seed)).random(120)
            population[:8] = initial
        else:
            population = initial

        def objective(units):
            nonlocal dispatched, best
            if dispatched >= budget:
                raise EvaluationCap()
            dispatched += 1
            receipt = bridge.request(units.tolist())
            if (receipt.get("Kind") != "measurement" or receipt.get("EvaluationId") != len(samples)
                    or receipt.get("CostUnits") != work):
                raise ValueError("Invalid evaluator receipt")
            loss, violation = receipt["Loss"], receipt["Violation"]
            if any(type(value) not in (float, int) or not math.isfinite(value) for value in (loss, violation)) or violation < 0:
                raise ValueError("Invalid measured objective or constraint")
            if len(samples) < 8 and receipt["GenomeHash"] != manifest["InitialGenomeHashes"][len(samples)]:
                raise ValueError("Shared start changed in transport")
            if violation <= 0:
                best = loss if best is None else min(best, loss)
            samples.append(dict(EvaluationId=len(samples), Status="Completed", BestLoss=best, Attempts=1,
                                CostUnits=work, DiagnosticCodes=[], Quality=-loss, ConstraintViolations=[violation],
                                Descriptors={"coordinate-0": -5 + 10 * float(units[0]),
                                             "coordinate-1": -5 + 10 * float(units[1])}, GenomeId=receipt["GenomeHash"]))
            return loss if violation <= 0 else math.inf

        try:
            result = optimizer(objective, [(0, 1)] * 8, init=population, maxiter=budget,
                               rng=np.random.default_rng(search_seed), strategy="best1bin", mutation=(0.5, 1.0),
                               recombination=0.7, tol=0.01, atol=0, polish=False,
                               updating="immediate", workers=1, vectorized=False)
            if int(result.nfev) != dispatched or not result.success or result.fun != best:
                raise ValueError("Unreconciled optimizer convergence")
            stop = "evaluation-cap" if dispatched == budget else "converged"
        except EvaluationCap:
            stop = "evaluation-cap"
        summary = bridge.request(None)
        if (summary.get("Kind") != "summary" or bridge.process.wait(timeout=10) != 0
                or summary["EvaluatorCalls"] != dispatched or dispatched != len(samples)
                or summary["Samples"] != samples or summary["BestLoss"] != best):
            raise ValueError("Controller and independent evaluator evidence differ")
        resources = summary["Resources"]
        if (resources["Spent"]["cost_units"] != dispatched * work
                or resources["Spent"]["proposal_calls"] != max(0, dispatched - 8)
                or resources["Unknown"] or resources["MaximumViolated"]
                or any(resources["Reserved"].values())):
            raise ValueError("Unreconciled independent resources")
    except Exception as exception:
        if isinstance(exception, MemoryError):
            raise
        stop = "failed"
        error = type(exception).__name__ + ": " + str(exception)[:300]
        if bridge is not None and isinstance(bridge.last, dict) and bridge.last.get("Kind") in ("summary", "error"):
            summary = bridge.last
    finally:
        if bridge is not None:
            bridge.close()
    return dict(task=task, instance_seed=instance_seed, search_seed=search_seed, budget=budget, mode=mode,
                method="ScipyDifferentialEvolution", status="failed" if stop == "failed" else "completed",
                stop_reason=stop, error=error, controller_dispatches=dispatched,
                independent_evaluator_calls=None if summary is None else summary["EvaluatorCalls"],
                unknown_work=summary is None, best_loss=best, samples=samples, evaluator_summary=summary,
                evaluator_manifest=manifest, settings=settings, environment=environment,
                elapsed_seconds=time.perf_counter() - started, evidence_class="contract-only")
