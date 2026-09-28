"""Runs the V1-83 defect scenarios against an installed OpenEvolve and records what happens.

Each scenario drives OpenEvolve's own classes (ProgramDatabase, Evaluator) the way its controller does. D8
is a static scan: an option counts as unimplemented when nothing outside config.py reads it. The outcomes
are evidence, not gates; the script exits 0 whether or not a defect reproduces, and 1 only if it cannot
run a scenario at all.

    python openevolve_defect_probe.py --output openevolve-defects.json
"""

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import math
import os
import pathlib
import re
import sys
import tempfile
import time
import uuid
from importlib import metadata

logging.disable(logging.CRITICAL)

from openevolve.config import DatabaseConfig, EvaluatorConfig  # noqa: E402
from openevolve.database import Program, ProgramDatabase  # noqa: E402
from openevolve.evaluator import Evaluator  # noqa: E402

# Our test that shows the correct behaviour for each class, so the evidence reads side by side.
OURS = {
    "D1": "ArchiveDefectClassTests.D1_a_non_finite_quality_cannot_complete_so_it_can_never_hold_a_cell",
    "D2": "EngineDefectClassTests.D2_a_program_rejected_as_not_novel_leaves_nothing_behind",
    "D3": "ArchiveDefectClassTests.D3_a_program_that_loses_its_cell_is_not_an_archive_member",
    "D4": "ArchiveDefectClassTests.D4_an_occupant_keeps_its_cell_when_later_values_arrive_outside_the_range",
    "D5": "EngineDefectClassTests.D5_the_result_does_not_depend_on_which_worker_finishes_first",
    "D6": "HungCandidateDefectClassTests.D6_a_hung_candidate_and_the_process_it_detached_are_gone_within_the_timeout_plus_one_second",
    "D7": "ArchiveDefectClassTests.D7_the_grid_has_exactly_the_requested_bins",
    "D8": "OptionCoverageTests.D8_every_public_option_is_exercised_by_a_test",
}


def database(**overrides) -> ProgramDatabase:
    settings = {"num_islands": 1, "feature_dimensions": ["x"], "feature_bins": 10}
    settings.update(overrides)
    return ProgramDatabase(DatabaseConfig(**settings))


def program(score: float, x: float) -> Program:
    tag = uuid.uuid4().hex
    return Program(id=tag, code="pass  # " + tag, metrics={"combined_score": score, "x": x})


def occupant(db: ProgramDatabase, member: Program):
    return next((cell for cell, pid in db.island_feature_maps[0].items() if pid == member.id), None)


def d1_nan_freezes_cell() -> dict:
    db = database()
    db.add(program(0.0, 0.0))
    db.add(program(0.0, 1.0))
    nan = program(float("nan"), 0.3)
    db.add(nan)
    cell = occupant(db, nan)
    challengers = (1.0, 100.0, 1e9)
    for score in challengers:
        db.add(program(score, 0.3))
    kept = cell is not None and db.island_feature_maps[0].get(cell) == nan.id

    # Control: the same challengers do displace a finite occupant, so they land in the NaN program's cell.
    control = database()
    control.add(program(0.0, 0.0))
    control.add(program(0.0, 1.0))
    finite = program(0.5, 0.3)
    control.add(finite)
    for score in challengers:
        control.add(program(score, 0.3))
    displaced = occupant(control, finite) is None
    return {
        "reproduced": kept and displaced,
        "observation": f"NaN occupant of cell {cell} kept it against challengers {list(challengers)}: {kept}; "
        f"a finite 0.5 occupant of the same cell was displaced by them: {displaced}",
    }


def d2_rejected_program_leaks() -> dict:
    db = database()
    db.add(program(0.0, 0.0))
    # Force the novelty verdict the way an embedding judge would return it; add() stores first, checks second.
    db._is_novel = lambda program_id, island: False
    rejected = program(1.0, 0.3)
    db.add(rejected)
    stored = rejected.id in db.programs
    in_island = any(rejected.id in island for island in db.islands)
    return {
        "reproduced": stored and not in_island,
        "observation": f"rejected-as-not-novel program in programs: {stored}; in any island: {in_island}",
    }


def d3_loser_joins_island() -> dict:
    db = database()
    db.add(program(0.0, 0.0))
    db.add(program(0.0, 1.0))
    db.add(program(1.0, 0.5))
    loser = program(0.5, 0.5)
    db.add(loser)
    member = loser.id in db.islands[0]
    owns = loser.id in db.island_feature_maps[0].values()
    return {
        "reproduced": member and not owns,
        "observation": f"cell loser is an island member: {member}; owns a cell: {owns}",
    }


def d4_cells_drift() -> dict:
    db = database()
    db.add(program(0.0, 0.0))
    db.add(program(0.0, 10.0))
    stored = program(1.0, 5.0)
    db.add(stored)
    before = db._calculate_feature_coords(stored)
    db.add(program(0.0, 100.0))
    after = db._calculate_feature_coords(stored)
    return {
        "reproduced": before != after,
        "observation": f"same stored program's cell before/after one wider value arrived: {before} -> {after}",
    }


def d5_completion_order_decides() -> dict:
    # The controller applies whichever future it finds done first (process_parallel.py), and the database
    # keeps the first arrival on a tie, so the archive depends on which worker finished first.
    def winner(order: list[float]) -> str:
        db = database()
        db.add(program(0.0, 0.0))
        db.add(program(0.0, 1.0))
        arrivals = [program(0.7, 0.4) for _ in order]
        for member in arrivals:
            db.add(member)
        cell = occupant(db, arrivals[0]) or occupant(db, arrivals[1])
        owner = db.island_feature_maps[0].get(cell)
        return "first" if owner == arrivals[0].id else "second" if owner == arrivals[1].id else "neither"

    a_then_b = winner([0.7, 0.7])
    return {
        "reproduced": a_then_b == "first",
        "observation": "two equal-fitness results for one cell: the cell keeps whichever the controller applied "
        f"first ({a_then_b}); process_parallel.py applies futures in the order it finds them done",
    }


def d6_timeout_keeps_running() -> dict:
    work = pathlib.Path(tempfile.mkdtemp(prefix="oe-d6-"))
    heartbeat = work / "heartbeat"
    evaluation = work / "evaluator.py"
    evaluation.write_text(
        "import time\n"
        "def evaluate(program_path):\n"
        "    deadline = time.time() + 6\n"
        "    count = 0\n"
        "    while time.time() < deadline:\n"
        "        count += 1\n"
        f"        open({str(heartbeat)!r}, 'w').write(str(count))\n"
        "        time.sleep(0.05)\n"
        "    return {'combined_score': 1.0}\n",
        encoding="utf-8",
    )
    config = EvaluatorConfig(timeout=1, max_retries=0, cascade_evaluation=False, parallel_evaluations=1)
    evaluator = Evaluator(config, str(evaluation))

    async def run() -> tuple[dict, int, int, float]:
        started = time.monotonic()
        metrics = await evaluator.evaluate_program("def f():\n    return 1\n", "hung")
        returned = time.monotonic() - started
        at_return = int(heartbeat.read_text() or 0)
        await asyncio.sleep(1.0)
        a_second_later = int(heartbeat.read_text() or 0)
        return metrics, at_return, a_second_later, returned

    metrics, at_return, later, returned = asyncio.run(run())
    still_running = later > at_return
    return {
        "reproduced": still_running,
        "observation": f"evaluate_program returned after {returned:.2f}s with {sorted(metrics)}; the hung "
        f"evaluation's heartbeat went {at_return} -> {later} over the following second",
    }


def d7_bins_overridden() -> dict:
    requested = 10
    db = database(feature_bins=requested, archive_size=10000, feature_dimensions=["x", "y"])
    actual = db.feature_bins
    return {
        "reproduced": actual != requested,
        "observation": f"requested {requested} bins per dimension with archive_size=10000 over 2 dimensions; "
        f"the database uses {actual}",
    }


def d8_options_do_nothing() -> dict:
    import openevolve

    root = pathlib.Path(openevolve.__file__).parent
    sources = [
        path.read_text(encoding="utf-8")
        for path in root.rglob("*.py")
        if path.name != "config.py"
    ]
    probes = {
        "prompt.use_meta_prompting": r"\.use_meta_prompting\b",
        "evaluator.memory_limit_mb": r"\.memory_limit_mb\b",
        "evaluator.cpu_limit": r"\.cpu_limit\b",
        "evaluator.distributed": r"\.distributed\b",
        "database.diversity_metric=feature_based": r"feature_based",
    }
    unread = {}
    for option, pattern in probes.items():
        expression = re.compile(pattern)
        unread[option] = sum(len(expression.findall(text)) for text in sources)
    dead = sorted(option for option, count in unread.items() if count == 0)
    return {
        "reproduced": len(dead) > 0,
        "observation": f"{len(dead)} of {len(probes)} declared options are read nowhere outside config.py: {dead}",
        "reads": unread,
    }


SCENARIOS = {
    "D1": ("A NaN score freezes its cell", "probe", d1_nan_freezes_cell),
    "D2": ("Programs rejected as not novel leak", "probe", d2_rejected_program_leaks),
    "D3": ("Cell losers join the island", "probe", d3_loser_joins_island),
    "D4": ("Cells drift as bin ranges adapt", "probe", d4_cells_drift),
    "D5": ("Results depend on worker completion order", "probe", d5_completion_order_decides),
    "D6": ("Timeouts do not stop the evaluation", "probe", d6_timeout_keeps_running),
    "D7": ("Requested bins are overridden", "probe", d7_bins_overridden),
    "D8": ("Declared options that do nothing", "static", d8_options_do_nothing),
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    results = []
    failed = False
    for key, (title, method, scenario) in SCENARIOS.items():
        try:
            outcome = scenario()
        except Exception as error:  # a scenario that cannot run is a probe failure, not a finding
            outcome = {"reproduced": None, "observation": f"scenario could not run: {type(error).__name__}: {error}"}
            failed = True
        results.append({"id": key, "title": title, "method": method, "ours": OURS[key], **outcome})
        print(f"{key} reproduced={outcome['reproduced']}: {outcome['observation']}")

    document = {
        "openevolve_version": metadata.version("openevolve"),
        "python": sys.version.split()[0],
        "platform": sys.platform,
        "reproduced": sum(1 for result in results if result["reproduced"]),
        "scenarios": len(results),
        "results": results,
    }
    pathlib.Path(args.output).write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
