"""E-4 (#347) scope 3: AlphaEvolve's analytic problems as tasks both engines can run.

    python alphaevolve_tasks.py write --output DIR [--problem ID ...]
    python alphaevolve_tasks.py score --problem ID PROGRAM

`alphaevolve_headroom.py` measured how far a model-free floor gets on the five vector problems with
room left in them. What it could not do is hand those problems to a searcher: there was no seed
program, no evaluator and no config, so neither our engine nor OpenEvolve could be pointed at them.
`write` produces one ordinary OpenEvolve task directory per problem - `config.yaml`,
`initial_program.py`, `evaluator.py` - which `aidotnet-evolve run --openevolve-config` and
`openevolve-run.py` both take unchanged. The directory is self-contained: `support/` holds copies
of this module, the headroom module and the verifiers, plus the floors, so it also runs where the
checkout is absent, such as a container that mounts only the evaluator's directory.

**The contract.** A candidate defines `construct()` and returns a sequence of reals. It is scored by
the problem's own independent verifier in `alphaevolve_math` (the one whose self-test reproduces the
published value), as given: nothing is repaired, because a repair would score a construction the
candidate did not make. An infeasible, non-finite, over-long or crashing candidate scores 0.0 with
the reason in `error`, which is what the next prompt sees.

**The score.** `combined_score` is the candidate's value as a fraction of AlphaEvolve's published
one, oriented so higher is better whatever the problem's direction: 1.0 matches the published
construction and above 1.0 beats it. `gap_fraction` uses `alphaevolve_headroom`'s orientation
(positive means behind the published value) so a result reads against that bundle directly, and
`beats_floor` says whether it improves on the floor that bundle measured.

**Scope.** c1, c2, c3, c3' and c5. c4-uncertainty-hermite is left out on purpose: the floor is 0.27%
from its published value, so a search has nothing to show there (#347).

**Residual risk, declared.** As in the symbolic-regression task, a candidate runs in the evaluator's
own process when it is imported, so it could replace the verifier. Only a restricted process closes
that; run the search behind the container engine when the candidate is not trusted.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import math
import pathlib
import sys

import numpy as np

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import alphaevolve_headroom as headroom  # noqa: E402

# The floor each problem is read against: the committed 20,000-iteration headroom bundle. A generated
# task carries its own copy of these modules and the floors (FLOOR_COPY) instead of the checkout.
FLOOR_BUNDLE = HERE.parent / "evidence/alphaevolve-headroom/77756eb/headroom.json"
FLOOR_COPY = HERE / "floors.json"
# What `write` copies into each task's support directory, so the task runs where the checkout is not
# (a container mounts only the evaluator's own directory).
SUPPORT = ("alphaevolve_tasks.py", "alphaevolve_headroom.py", "alphaevolve_math.py")

# np.convolve is quadratic in the length. 20,000 entries is 4e8 multiply-adds, well inside the
# evaluator timeout, and far beyond the lengths AlphaEvolve published; the bound keeps a candidate
# from turning one evaluation into a denial of the whole budget.
MAX_LENGTH = 20_000

# What a model is told. The formula is the verifier's, stated in the verifier's terms, so the prompt
# cannot describe a different problem from the one that is scored.
STATEMENTS = {
    "c1-autocorrelation": (
        "Find non-negative step heights h_0..h_{n-1} (n >= 2) of a step function f on [-1/4, 1/4] that "
        "MINIMIZE 2 * n * max(convolve(h, h)) / sum(h)**2, the autoconvolution ratio "
        "max(f*f) / (integral f)**2. Every height must be >= 0 and the sum positive."),
    "c2-autocorrelation": (
        "Find non-negative step heights h_0..h_{n-1} (n >= 2) that MAXIMIZE "
        "||g||_2^2 / (||g||_1 * ||g||_inf), where g = convolve(h, h) is taken as the piecewise-linear "
        "function through its knots, with a zero knot added at each end. Every height must be >= 0 and "
        "the sum positive."),
    "c3-autocorrelation": (
        "Find real step heights h_0..h_{n-1} (n >= 2; they MAY be negative) that MINIMIZE "
        "2 * n * |max(convolve(h, h))| / sum(h)**2. The sum must be non-zero."),
    "c3prime-autocorrelation": (
        "Find real step heights h_0..h_{n-1} (n >= 2; they MAY be negative) that MINIMIZE "
        "2 * n * max(|convolve(h, h)|) / sum(h)**2. The sum must be non-zero."),
    "c5-erdos-overlap": (
        "Erdos minimum overlap: find h_0..h_{n-1} (n >= 2) with 0 <= h_i <= 1 and sum(h) = n / 2 "
        "EXACTLY (to 1e-9 * n), that MINIMIZE 2 * max(correlate(h, 1 - h, mode='full')) / n."),
}

# The seed: a constant, which every problem accepts and every floor already beats.
SEEDS = {
    "c1-autocorrelation": "[1.0] * 16",
    "c2-autocorrelation": "[1.0] * 16",
    "c3-autocorrelation": "[1.0] * 16",
    "c3prime-autocorrelation": "[1.0] * 16",
    "c5-erdos-overlap": "[0.5] * 16",
}

TASKS = tuple(STATEMENTS)


def _floors():
    # A generated task's support copy has the floors beside it; the checkout reads the bundle itself.
    if FLOOR_COPY.is_file():
        return json.loads(FLOOR_COPY.read_text(encoding="utf-8"))
    report = json.loads(FLOOR_BUNDLE.read_text(encoding="utf-8"))
    return {row["Problem"]: row["FloorBest"] for row in report["Rows"]}


FLOORS = _floors()


def distance(problem, value):
    """Where `value` stands against the published construction and the floor, in both orientations."""
    spec = headroom.IN_SCOPE[problem]
    published, floor = spec["published"], FLOORS[problem]
    minimize = spec["direction"] == "minimize"
    return {
        "value": value,
        "published": published,
        "floor": floor,
        "combined_score": published / value if minimize else value / published,
        "gap_fraction": (value - published) / abs(published) if minimize else (published - value) / abs(published),
        "beats_floor": value < floor if minimize else value > floor,
    }


def score(problem, construction):
    """Scores one construction with the problem's verifier. Never raises for a bad construction."""
    if problem not in STATEMENTS:
        raise KeyError("not an AlphaEvolve task: " + str(problem))
    try:
        heights = np.asarray(construction, dtype=np.float64)
    except (TypeError, ValueError, OverflowError, MemoryError) as error:
        return {"combined_score": 0.0, "error": "construct() did not return a sequence of reals: " + str(error)}
    if heights.ndim != 1:
        return {"combined_score": 0.0, "error": "construct() must return a flat sequence, got shape %s" % (heights.shape,)}
    if heights.size > MAX_LENGTH:
        return {"combined_score": 0.0, "error": "%d entries exceeds the %d-entry limit" % (heights.size, MAX_LENGTH)}
    value = headroom.IN_SCOPE[problem]["objective"](heights)
    if value is None:
        return {"combined_score": 0.0, "error": "infeasible: " + STATEMENTS[problem]}
    value = float(value)
    if not math.isfinite(value) or value <= 0:
        return {"combined_score": 0.0, "error": "the verifier returned %r, which cannot be ranked" % value}
    result = distance(problem, value)
    result["length"] = int(heights.size)
    return result


def _load(program_path):
    spec = importlib.util.spec_from_file_location("alphaevolve_candidate", program_path)
    if spec is None or spec.loader is None:
        raise ImportError("cannot load candidate " + str(program_path))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    construct = getattr(module, "construct", None)
    if not callable(construct):
        raise AttributeError("the candidate defines no construct()")
    return construct


def evaluator(problem):
    """Returns `evaluate(program_path)` under OpenEvolve's contract for one problem."""
    if problem not in STATEMENTS:
        raise KeyError("not an AlphaEvolve task: " + str(problem))

    def evaluate(program_path):
        # SystemExit too: a candidate that calls sys.exit() would otherwise end the evaluator, and with it
        # OpenEvolve's whole run, rather than score 0.0.
        try:
            construction = _load(program_path)()
        except (Exception, SystemExit) as error:  # noqa: BLE001 - a broken candidate scores worst, as upstream's examples do
            return {"combined_score": 0.0, "error": "%s: %s" % (type(error).__name__, str(error)[:200])}
        return score(problem, construction)

    return evaluate


CONFIG = """# OpenEvolve config for AlphaEvolve problem {problem} (generated by alphaevolve_tasks.py, #347).
# Both arms read this file: ours through `aidotnet-evolve run --openevolve-config`, OpenEvolve directly.
max_iterations: 100
checkpoint_interval: 10
log_level: INFO

llm:
  models:
    - name: "sonnet"
      weight: 1.0
  temperature: 0.7
  max_tokens: 8192
  timeout: 300
  retries: 0

prompt:
  system_message: |
{statement}
    Write `construct()`, returning the heights as a list of floats. You may compute them however you
    like within the evaluator's {timeout}-second limit, and any n up to {max_length}. AlphaEvolve's
    published construction scores {published}; combined_score is your value as a fraction of it,
    oriented so higher is better, and above 1.0 beats it. Return the complete program in a python
    code fence.

database:
  population_size: 40
  num_islands: 2

evaluator:
  timeout: {timeout}
  cascade_evaluation: false
"""

INITIAL = '''"""Seed for AlphaEvolve problem {problem}: a constant, which is feasible and weak."""


def construct():
    return {seed}
'''

EVALUATOR = '''"""Generated by alphaevolve_tasks.py for {problem}: OpenEvolve's evaluate(program_path) contract."""
import sys
from pathlib import Path

# The verifier and the floors travel with the task, so it runs wherever this directory is mounted.
sys.path.insert(0, str(Path(__file__).resolve().parent / "support"))
import alphaevolve_tasks  # noqa: E402

evaluate = alphaevolve_tasks.evaluator({problem!r})
'''

TIMEOUT_SECONDS = 60


def write(output, problems=TASKS):
    """Writes one task directory per problem under `output`.

    Everything is checked before anything is written, so a refusal (an unknown or repeated problem, or a
    directory that already exists) leaves no partial output behind.
    """
    root = pathlib.Path(output)
    problems = list(problems)
    unknown = [problem for problem in problems if problem not in STATEMENTS]
    if unknown:
        raise KeyError("not an AlphaEvolve task: " + ", ".join(map(str, unknown)))
    if len(set(problems)) != len(problems):
        raise ValueError("a problem is listed more than once: " + ", ".join(problems))
    existing = [str(root / problem) for problem in problems if (root / problem).exists()]
    if existing:
        raise FileExistsError("refusing to overwrite: " + ", ".join(existing))

    floors = json.dumps({problem: FLOORS[problem] for problem in TASKS}, indent=1) + "\n"
    written = []
    for problem in problems:
        directory = root / problem
        (directory / "support").mkdir(parents=True)
        statement = "\n".join("    " + line for line in _wrap(STATEMENTS[problem]))
        files = {
            "config.yaml": CONFIG.format(problem=problem, statement=statement, timeout=TIMEOUT_SECONDS,
                                         max_length=MAX_LENGTH, published=headroom.IN_SCOPE[problem]["published"]),
            "initial_program.py": INITIAL.format(problem=problem, seed=SEEDS[problem]),
            "evaluator.py": EVALUATOR.format(problem=problem),
            "support/floors.json": floors,
        }
        for name in SUPPORT:
            files["support/" + name] = (HERE / name).read_text(encoding="utf-8")
        for name, text in files.items():
            (directory / name).write_text(text, encoding="utf-8", newline="\n")
        written.append(directory)
    return written


def _wrap(text, width=96):
    lines, current = [], ""
    for word in text.split():
        if current and len(current) + 1 + len(word) > width:
            lines.append(current)
            current = word
        else:
            current = word if not current else current + " " + word
    if current:
        lines.append(current)
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)
    write_command = commands.add_parser("write", help="write one OpenEvolve task directory per problem")
    write_command.add_argument("--output", required=True)
    write_command.add_argument("--problem", action="append", choices=TASKS)
    score_command = commands.add_parser("score", help="score one program file")
    score_command.add_argument("--problem", required=True, choices=TASKS)
    score_command.add_argument("program")
    arguments = parser.parse_args(argv)
    if arguments.command == "write":
        for directory in write(arguments.output, arguments.problem or TASKS):
            print(directory)
        return 0
    print(json.dumps(evaluator(arguments.problem)(arguments.program), indent=1))
    return 0


if __name__ == "__main__":
    sys.exit(main())
