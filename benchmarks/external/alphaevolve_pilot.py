"""E-4 (#347): the pilot's seams for AlphaEvolve problems - broker scoring, the endpoint, and the registration.

`pilot_broker`, `pilot_cells` and `pilot_campaign.spec` are written for the symbolic-regression family: a candidate
defines `predict`, selection rows score it, and a held-out split is the endpoint. An AlphaEvolve candidate defines
`construct()` instead, and its verifier is exact and deterministic, so there is nothing held out: the endpoint is the
same verifier run again, in a fresh process, on the program the search returned. Everything else - the searchers,
`campaign_runner`, the per-cell broker, the paired analysis and the power analysis - is reused unchanged.

**A construction runs in a child process with a timeout.** The symbolic pilot loads `predict` into the broker's own
process, which is safe for a formula. A construction may run an optimisation for the full limit, or never return, and
in-process that would hang the broker and with it the cell. The child gets a minimal environment, so it never sees
whatever the runner's process carries.
"""
from __future__ import annotations

import contextlib
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "analysis"))

import alphaevolve_headroom as headroom  # noqa: E402
import alphaevolve_tasks as tasks  # noqa: E402
import pilot_broker  # noqa: E402
import pilot_campaign  # noqa: E402
import pilot_cells  # noqa: E402
from program_broker import ProgramBroker, candidate_hash  # noqa: E402

METRIC = "published_ratio"
# The construction limit the system message states. The engines' own evaluator timeout is set above it
# (`ENGINE_EVALUATOR_TIMEOUT`), so the broker's limit is the one that binds and an engine never abandons an
# evaluation the broker is still finishing.
CONSTRUCT_TIMEOUT = tasks.TIMEOUT_SECONDS
ENGINE_EVALUATOR_TIMEOUT = tasks.TIMEOUT_SECONDS + 30
# What a child may inherit: enough for Python and numpy to start on Windows and POSIX, nothing that names a broker.
CHILD_ENVIRONMENT = ("PATH", "SYSTEMROOT", "SYSTEMDRIVE", "TEMP", "TMP", "HOME", "USERPROFILE", "LANG")
VERIFIER_SOURCES = ("alphaevolve_math.py", "alphaevolve_headroom.py", "alphaevolve_tasks.py")

CONSTRUCT = r'''
import importlib.util, json, sys
import numpy as np
spec = importlib.util.spec_from_file_location("alphaevolve_candidate", sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
construct = getattr(module, "construct", None)
if not callable(construct):
    raise AttributeError("the candidate defines no construct()")
heights = np.asarray(construct(), dtype=np.float64)
with open(sys.argv[2], "w", encoding="utf-8") as out:
    json.dump(heights.tolist(), out)
'''


def construction(program_path, python, *, timeout=CONSTRUCT_TIMEOUT):
    """Runs `construct()` in a child process. Returns `(heights, None)` or `(None, reason)`; never raises for the candidate."""
    environment = {name: os.environ[name] for name in CHILD_ENVIRONMENT if name in os.environ}
    with tempfile.TemporaryDirectory(prefix="alphaevolve-construct-") as directory:
        output = Path(directory) / "heights.json"
        try:
            completed = subprocess.run([str(python), "-I", "-c", CONSTRUCT, str(program_path), str(output)],
                                       capture_output=True, text=True, timeout=timeout, cwd=directory,
                                       env=environment, check=False)
        except subprocess.TimeoutExpired:
            return None, "construct() did not return within %d seconds" % timeout
        if completed.returncode != 0 or not output.is_file():
            lines = (completed.stderr or "").strip().splitlines()
            return None, (lines[-1] if lines else "the construction exited %d" % completed.returncode)[:300]
        try:
            return json.loads(output.read_text(encoding="utf-8")), None
        except ValueError as error:
            return None, "construct() returned something that is not a list of reals: " + str(error)[:200]


def verifier_feedback(problem, scored):
    """What both arms are shown about a scored construction: the verifier's reading, or why it refused."""
    if "error" in scored:
        return "refused, scored 0.0: " + scored["error"]
    return ("value %.10g (AlphaEvolve published %.10g, %s is better); combined_score %.6f; %d entries"
            % (scored["value"], scored["published"], headroom.IN_SCOPE[problem]["direction"].replace("imize", "er"),
               scored["combined_score"], scored["length"]))


def evaluate_callback(problem, python, *, timeout=CONSTRUCT_TIMEOUT):
    """Scores candidate code with the problem's verifier, in the broker's `evaluate` shape.

    A candidate that does not run (no `construct`, an exception, the time limit) is `invalid`, as a broken program is
    in the symbolic pilot. A construction that runs but the verifier refuses (infeasible, malformed, too long) is
    `valid` at 0.0 with the reason in its artifacts, as the task's own evaluator scores it - so the model learns why.
    """
    if problem not in tasks.TASKS:
        raise KeyError("not an AlphaEvolve task: " + str(problem))

    def evaluate(code):
        reply = {"candidate_hash": candidate_hash(code), "unknown_work": False}
        started = time.perf_counter()
        with tempfile.TemporaryDirectory(prefix="alphaevolve-candidate-") as directory:
            path = Path(directory) / "candidate.py"
            path.write_text(code, encoding="utf-8")
            heights, _ = construction(path, python, timeout=timeout)
        scored = tasks.score(problem, heights) if heights is not None else None
        reply["work_units"] = time.perf_counter() - started
        if scored is None:
            reply["status"] = "invalid"
            return reply
        reply.update(status="valid", quality=float(scored["combined_score"]),
                     artifacts={"verifier": verifier_feedback(problem, scored)})
        return reply

    return evaluate


def session(provider, *, initials, python, model_calls, evaluations, seconds, model_tokens,
            cost_metric=pilot_broker.COST_METRICS[1]):
    """Returns `open_for(cell)`: one broker for exactly one cell, scored by that cell's problem."""
    @contextlib.contextmanager
    def open_for(cell):
        problem = str(cell["Family"])
        with ProgramBroker(pilot_broker.model_callback(provider, cost_metric=cost_metric),
                           evaluate_callback(problem, python),
                           model_calls=model_calls, evaluations=evaluations, seconds=seconds,
                           initial=initials[problem], model_tokens=model_tokens) as broker:
            yield broker

    return open_for


def cell_broker(frozen, provider, *, initials, python):
    """The per-cell broker with the frozen registration's budget as its ceiling, as `pilot_campaign.cell_broker`."""
    budget = frozen["Spec"]["Budget"]
    return session(provider, initials=initials, python=python, model_calls=budget["ModelCalls"],
                   evaluations=budget["ModelCalls"] + pilot_campaign.EVALUATION_HEADROOM,
                   seconds=budget["WallSeconds"], model_tokens=budget["ModelTokens"])


def score_endpoint(problem, program_path, python):
    """The endpoint: the verifier's combined_score for the returned program, from a fresh run of its construction."""
    heights, reason = construction(program_path, python)
    if heights is None:
        raise ValueError("the returned program did not construct: " + reason)
    return float(tasks.score(problem, heights)["combined_score"])


def executor(searchers, *, python):
    """Returns an `execute_cell` dispatching on the cell's problem and arm, as `pilot_cells.executor` does for one family.

    `searchers` maps a problem to `{arm: search}`.
    """
    if not searchers or not all(searchers.values()):
        raise ValueError("A pilot needs at least one arm's searcher for every problem")
    unknown = [problem for problem in searchers if problem not in tasks.TASKS]
    if unknown:
        raise KeyError("not an AlphaEvolve task: " + ", ".join(unknown))

    def execute_cell(cell, directory, resume):
        problem, arm = str(cell["Family"]), cell["Arm"]
        if problem not in searchers or arm not in searchers[problem]:
            raise KeyError("No searcher declared for %s / %s" % (problem, arm))
        try:
            outcome = searchers[problem][arm](cell, Path(directory), resume)
            value = score_endpoint(problem, outcome["program"], python)
            if not math.isfinite(value):
                return pilot_cells._failed(outcome, "non-finite endpoint score")
        except Exception as error:  # noqa: BLE001 - one cell's failure is that cell's outcome
            return pilot_cells._failed(locals().get("outcome") or getattr(error, "accounting", None),
                                       pilot_cells._reason(error))
        return {
            "Status": "completed",
            "Value": value,
            "ModelCalls": outcome.get("model_calls"),
            "ModelTokens": outcome.get("model_tokens"),
            "ThrottleSeconds": float(outcome.get("throttle_seconds", 0.0)),
            "LostCalls": int(outcome.get("lost_calls", 0)),
        }

    return execute_cell


def verifier_seal(problem):
    """SHA-256 over the problem's name and the verifier sources the endpoint is scored by.

    There is no held-out split to seal: the verifier is the test. What the registration commits to is therefore the
    scoring code, so a verifier changed after the seeds were drawn no longer matches what was registered.
    """
    digest = hashlib.sha256(problem.encode("utf-8") + b"\0")
    for name in VERIFIER_SOURCES:
        digest.update((HERE / name).read_bytes().replace(b"\r\n", b"\n"))
    return digest.hexdigest()


def spec(problems, *, seed_count, model, model_calls, model_tokens, wall_seconds,
         bootstrap_samples=1000, bootstrap_seed=11, familywise_alpha=0.05):
    """The campaign as declared: one family per problem, each endpoint the published ratio, higher is better."""
    problems = list(problems)
    unknown = [problem for problem in problems if problem not in tasks.TASKS]
    if unknown or not problems or len(set(problems)) != len(problems):
        raise ValueError("Declare each AlphaEvolve task at most once: " + ", ".join(map(str, problems)))
    return {
        "Campaign": "e4-alphaevolve-pilot",
        "Budget": {"ModelTokens": model_tokens, "ModelCalls": model_calls, "WallSeconds": wall_seconds},
        "Models": [model],
        "SeedCount": seed_count,
        "BootstrapSamples": bootstrap_samples,
        "BootstrapSeed": bootstrap_seed,
        "FamilywiseAlpha": familywise_alpha,
        "Families": [{"Family": problem, "Configs": ["recommended", "matched"],
                      "Endpoint": {"Metric": METRIC, "Direction": "maximize", "FailureValue": 0.0}}
                     for problem in problems],
        "TestPartitionHashes": {problem: verifier_seal(problem) for problem in problems},
        "Published": None,
    }


def stub_provider(calls, answers):
    """A loopback model for rehearsals: answers each prompt with the given problem's whole-program construction.

    The problem is recognised from its statement in the system message, which every arm's prompt carries. `answers`
    maps a problem to the program text to return.
    """
    def provider(system, messages, model=None):
        calls.append(model)
        text = " ".join(((system or "") + " " + " ".join(str(message.get("content", "")) for message in messages)).split())
        # The whole statement: c3's and c3prime's agree for their first dozen words.
        for problem, statement in tasks.STATEMENTS.items():
            if " ".join(statement.split()) in text and problem in answers:
                return answers[problem], 30
        raise ValueError("the stub could not tell which problem this prompt is for")

    return provider
