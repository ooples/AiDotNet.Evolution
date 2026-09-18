"""Pinned upstream CMA-ME using the shared US-01 service and receipt validator."""
import hashlib
import inspect
from pathlib import Path
from types import SimpleNamespace

from suite_baseline import run_one as run_shared


def run_one(dll, task, instance_seed, search_seed, budget, expected_hash):
    import numpy as np
    import ribs
    from ribs.archives import GridArchive
    from ribs.emitters import EvolutionStrategyEmitter
    if ribs.__version__ != "0.12.0":
        raise ValueError("Install the pinned pyribs version")
    state = dict(full_batch_updates=0, partial_batch_measurements=0, occupied_cells=0)

    def optimize(objective, bounds, **kwargs):
        initial = kwargs["init"]
        cap = kwargs["maxiter"]  # Shared runner passes the declared evaluation cap here.
        archive = GridArchive(solution_dim=8, dims=(10, 10), ranges=[(-5, 5), (-5, 5)], seed=search_seed)
        losses = np.array([objective(point) for point in initial])
        calls = len(losses)
        best = float(np.min(losses))

        def add(points, values):
            feasible = np.isfinite(values)
            status = np.zeros(len(points), dtype=np.int32)
            improvements = np.full(len(points), -1e300, dtype=float)
            if feasible.any():
                added = archive.add(solution=points[feasible], objective=-values[feasible],
                                    measures=-5 + 10 * points[feasible, :2])
                status[feasible] = added["status"]
                improvements[feasible] = added["value"]
            return {"status": status, "value": improvements}

        add(initial, losses)
        emitter = EvolutionStrategyEmitter(archive, x0=initial[int(np.argmin(losses))], sigma0=0.1,
                                            ranker="2imp", es="cma_es", selection_rule="filter",
                                            restart_rule="no_improvement", bounds=bounds, batch_size=8, seed=search_seed)
        while calls < cap:
            batch = emitter.ask()
            count = min(len(batch), cap - calls)
            points = batch[:count]
            measured = np.array([objective(point) for point in points])
            calls += count
            best = min(best, float(np.min(measured)))
            added = add(points, measured)
            if count == len(batch):
                emitter.tell(solution=points, objective=np.where(np.isfinite(measured), -measured, -1e300),
                             measures=-5 + 10 * points[:, :2], add_info=added)
                state["full_batch_updates"] += 1
            else:
                # Final measurements count in performance/work, but cannot form a full CMA update.
                state["partial_batch_measurements"] += count
        state["occupied_cells"] = len(archive)
        return SimpleNamespace(nfev=calls, fun=best, success=True)

    row = run_shared(dll, task, instance_seed, search_seed, budget, expected_hash, optimizer=optimize)
    row.update(method="PyribsCmaMe", state=state,
               settings=dict(initial_population=8, batch_size=8, sigma0=0.1, archive_dims=[10, 10],
                             archive_ranges=[[-5, 5], [-5, 5]], ranker="2imp", es="cma_es", selection_rule="filter",
                             restart_rule="no_improvement", tuning_trials=0,
                             constraint_handling="infeasible samples never inserted; rank as non-improvements",
                             partial_batch="measured but not passed to CMA tell"))
    row["environment"]["pyribs"] = ribs.__version__
    row["upstream_revision"] = "105cf16f27288c189dbf12c7bacdc40448ab1928"
    row["implementation_hashes"] = {kind.__name__: hashlib.sha256(Path(inspect.getfile(kind)).read_bytes()).hexdigest()
                                     for kind in (GridArchive, EvolutionStrategyEmitter)}
    return row
