"""E-4 (#347): the pilot's seams for AlphaEvolve problems - broker scoring, the endpoint, and the registration.

`pilot_broker`, `pilot_cells` and `pilot_campaign.spec` are written for the symbolic-regression family: a candidate
defines `predict`, selection rows score it, and a held-out split is the endpoint. An AlphaEvolve candidate defines
`construct()` instead, and its verifier is exact and deterministic, so there is nothing held out: the endpoint
rescores the stored heights from the search's own evaluation of the returned program. A fresh run can differ when
candidates read the clock. The searchers, per-cell broker, paired analysis and power analysis are reused.

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
import signal
import subprocess
import sys
import tempfile
import threading
import time

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "analysis"))

import alphaevolve_headroom as headroom  # noqa: E402
import alphaevolve_tasks as tasks  # noqa: E402
import pilot_broker  # noqa: E402
import pilot_campaign  # noqa: E402
import pilot_cells  # noqa: E402
from codex_transport import MAX_OUTPUT  # noqa: E402
from program_broker import ProgramBroker, candidate_hash  # noqa: E402

METRIC = "published_ratio"
# The construction limit the system message states. The engines' own evaluator timeout is set above it
# (`ENGINE_EVALUATOR_TIMEOUT`), so the broker's limit is the one that binds and an engine never abandons an
# evaluation the broker is still finishing.
CONSTRUCT_TIMEOUT = tasks.TIMEOUT_SECONDS
# The memory the statement grants a construction, for the child and everything it starts together.
CONSTRUCT_MEMORY = tasks.MEMORY_GIB * 1024 ** 3
ENGINE_EVALUATOR_TIMEOUT = tasks.TIMEOUT_SECONDS + 30
# What a child may inherit: enough for Python and numpy to start on Windows and POSIX, nothing that names a broker.
CHILD_ENVIRONMENT = ("PATH", "SYSTEMROOT", "SYSTEMDRIVE", "TEMP", "TMP", "HOME", "USERPROFILE", "LANG")
VERIFIER_SOURCES = ("alphaevolve_math.py", "alphaevolve_headroom.py", "alphaevolve_tasks.py")

def _release(process):
    """Allow candidate imports only after process containment is installed."""
    try:
        process.stdin.write(b"1")
        process.stdin.flush()
    finally:
        process.stdin.close()



@contextlib.contextmanager
def _construction_directory():
    directory = tempfile.TemporaryDirectory(prefix="alphaevolve-construct-")
    try:
        yield directory.name
    finally:
        # Windows may release redirected file handles just after the job reports zero active processes.
        deadline = time.monotonic() + 10
        while True:
            try:
                directory.cleanup()
                break
            except PermissionError as error:
                if getattr(error, "winerror", None) != 32 or time.monotonic() >= deadline:
                    raise
                time.sleep(0.01)



CONSTRUCT = r'''
import importlib.util, json, os, sys
# Parent grants admission only after job assignment succeeds. No candidate code runs before this gate.
if sys.stdin.buffer.read(1) != b"1":
    raise SystemExit("construction admission refused")
sys.stdin.close()
if os.name != "nt":
    # Soft and hard alike, so the candidate cannot raise it. RLIMIT_DATA rather than RLIMIT_AS: numpy reserves address
    # space at import that it never commits, and Linux counts mmap'd memory against RLIMIT_DATA since 4.7.
    import resource
    resource.setrlimit(resource.RLIMIT_DATA, (int(sys.argv[3]), int(sys.argv[3])))
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


class _Job:
    """A Windows job object holding a construction and everything it starts.

    It caps the committed memory of the whole tree at `memory` bytes, and closing it kills every process still in it
    (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`), whether the construction overran or finished normally.
    """

    _LIMIT_JOB_MEMORY, _LIMIT_KILL_ON_JOB_CLOSE, _EXTENDED_LIMIT_INFORMATION = 0x200, 0x2000, 9

    def __init__(self, memory):
        import ctypes
        from ctypes import wintypes

        class Basic(ctypes.Structure):
            _fields_ = [("PerProcessUserTimeLimit", ctypes.c_int64), ("PerJobUserTimeLimit", ctypes.c_int64),
                        ("LimitFlags", wintypes.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                        ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wintypes.DWORD),
                        ("Affinity", ctypes.c_size_t), ("PriorityClass", wintypes.DWORD),
                        ("SchedulingClass", wintypes.DWORD)]

        class Extended(ctypes.Structure):
            _fields_ = [("BasicLimitInformation", Basic), ("IoInfo", ctypes.c_uint64 * 6),
                        ("ProcessMemoryLimit", ctypes.c_size_t), ("JobMemoryLimit", ctypes.c_size_t),
                        ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]

        self._kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self._kernel.CreateJobObjectW.restype = wintypes.HANDLE
        self._kernel.CreateJobObjectW.argtypes = (ctypes.c_void_p, wintypes.LPCWSTR)
        self._kernel.SetInformationJobObject.argtypes = (wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD)
        self._kernel.AssignProcessToJobObject.argtypes = (wintypes.HANDLE, wintypes.HANDLE)
        self._kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
        self._handle = self._kernel.CreateJobObjectW(None, None)
        if not self._handle:
            raise ctypes.WinError(ctypes.get_last_error())
        limits = Extended()
        limits.BasicLimitInformation.LimitFlags = self._LIMIT_JOB_MEMORY | self._LIMIT_KILL_ON_JOB_CLOSE
        limits.JobMemoryLimit = memory
        if not self._kernel.SetInformationJobObject(self._handle, self._EXTENDED_LIMIT_INFORMATION,
                                                    ctypes.byref(limits), ctypes.sizeof(limits)):
            error = ctypes.WinError(ctypes.get_last_error())
            self.close()
            raise error
        self._error = ctypes.WinError
        self._last_error = ctypes.get_last_error

    def assign(self, process):
        # The child is assigned as soon as it exists, before its interpreter has started, so what it starts is inside.
        # A child that already exited cannot be assigned (access denied) and has nothing left to contain.
        if not self._kernel.AssignProcessToJobObject(self._handle, int(process._handle)) and process.poll() is None:
            raise self._error(self._last_error())

    def close(self):
        if not self._handle:
            return
        import ctypes
        from ctypes import wintypes

        class Accounting(ctypes.Structure):
            _fields_ = [("TotalUserTime", ctypes.c_int64), ("TotalKernelTime", ctypes.c_int64),
                        ("ThisPeriodTotalUserTime", ctypes.c_int64), ("ThisPeriodTotalKernelTime", ctypes.c_int64),
                        ("TotalPageFaultCount", wintypes.DWORD), ("TotalProcesses", wintypes.DWORD),
                        ("ActiveProcesses", wintypes.DWORD), ("TotalTerminatedProcesses", wintypes.DWORD)]

        self._kernel.TerminateJobObject.argtypes = (wintypes.HANDLE, wintypes.UINT)
        self._kernel.QueryInformationJobObject.argtypes = (
            wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p)
        handle, self._handle = self._handle, None
        try:
            if not self._kernel.TerminateJobObject(handle, 1):
                raise ctypes.WinError(ctypes.get_last_error())
            # Job termination is asynchronous. Reap the whole tree before deleting its redirected files.
            deadline = time.monotonic() + 10
            accounting = Accounting()
            while True:
                if not self._kernel.QueryInformationJobObject(
                        handle, 1, ctypes.byref(accounting), ctypes.sizeof(accounting), None):
                    raise ctypes.WinError(ctypes.get_last_error())
                if accounting.ActiveProcesses == 0:
                    break
                if time.monotonic() >= deadline:
                    raise TimeoutError("construction job did not terminate within 10 seconds")
                time.sleep(0.01)
        finally:
            self._kernel.CloseHandle(handle)


def _stop(process, job):
    """Kills the construction's whole tree, even when the construction itself already exited normally."""
    if job is not None:
        job.close()
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass  # the group is already empty
    if process.poll() is None:
        # Assignment can fail before the child belongs to the job; closing that job cannot reap it.
        process.kill()
        process.wait(timeout=10)
    if process.stdin is not None and not process.stdin.closed:
        process.stdin.close()


def _size(path):
    try:
        return path.stat().st_size
    except FileNotFoundError:
        return 0


def construction(program_path, python, *, timeout=CONSTRUCT_TIMEOUT):
    """Runs `construct()` in a child process. Returns `(heights, None)` or `(None, reason)`; never raises for the candidate.

    The child runs in its own process group (a new session on POSIX, a job object on Windows) with `CONSTRUCT_MEMORY`
    as its memory limit (`RLIMIT_DATA` on POSIX, the job's limit on Windows), and the whole group is killed when it
    overruns, writes more than `MAX_OUTPUT`, or finishes - so a candidate that spawns workers leaves none behind. Its
    output and its `heights.json` are bounded by `MAX_OUTPUT` while it runs and again after it exits.
    """
    environment = {name: os.environ[name] for name in CHILD_ENVIRONMENT if name in os.environ}
    with _construction_directory() as directory:
        directory = Path(directory)
        output, printed = directory / "heights.json", (directory / "stdout.txt", directory / "stderr.txt")
        process, job = None, (_Job(CONSTRUCT_MEMORY) if os.name == "nt" else None)
        started = time.monotonic()
        try:
            with printed[0].open("xb") as stdout, printed[1].open("xb") as stderr:
                process = subprocess.Popen([str(python), "-I", "-c", CONSTRUCT, str(program_path), str(output),
                                            str(CONSTRUCT_MEMORY)],
                                           stdin=subprocess.PIPE, stdout=stdout, stderr=stderr, cwd=directory,
                                           env=environment, start_new_session=os.name != "nt")
                if job is not None:
                    job.assign(process)
                _release(process)
                while process.poll() is None:
                    if time.monotonic() - started >= timeout:
                        return None, "construct() did not return within %d seconds" % timeout
                    if max(_size(path) for path in printed) > MAX_OUTPUT:
                        return None, "the construction printed more than %d bytes" % MAX_OUTPUT
                    if _size(output) > MAX_OUTPUT:
                        return None, "construct() returned more than %d bytes of heights" % MAX_OUTPUT
                    time.sleep(0.02)
                returncode = process.returncode
        finally:
            if process is not None:
                _stop(process, job)
            elif job is not None:
                job.close()
        # A child that printed past the bound and exited between two polls is refused just the same.
        if max(_size(path) for path in printed) > MAX_OUTPUT:
            return None, "the construction printed more than %d bytes" % MAX_OUTPUT
        if returncode != 0 or not output.is_file():
            with (directory / "stderr.txt").open("rb") as stderr:
                tail = stderr.read(MAX_OUTPUT).decode("utf-8", errors="replace")
            lines = tail.strip().splitlines()
            return None, (lines[-1] if lines else "the construction exited %d" % returncode)[:300]
        if output.stat().st_size > MAX_OUTPUT:
            return None, "construct() returned more than %d bytes of heights" % MAX_OUTPUT
        try:
            return json.loads(output.read_text(encoding="utf-8")), None
        except ValueError as error:
            return None, "construct() returned something that is not a list of reals: " + str(error)[:200]


def verifier_feedback(problem, scored):
    """What both arms are shown about a scored construction: the verifier's reading, or why it refused."""
    if "error" in scored:
        return "refused, scored 0.0: " + scored["error"]
    better = BETTER[headroom.IN_SCOPE[problem]["direction"]]
    return ("value %.10g (AlphaEvolve published %.10g, %s is better); combined_score %.6f; %d entries"
            % (scored["value"], scored["published"], better, scored["combined_score"], scored["length"]))


# The word the feedback uses for each direction. A lookup rather than string surgery, which turns "minimize" into
# "miner" (review caught it); a direction outside this table is a KeyError, not silently wrong feedback.
BETTER = {"minimize": "lower", "maximize": "higher"}


def evaluate_callback(problem, python, *, timeout=CONSTRUCT_TIMEOUT, record=None):
    """Scores candidate code with the problem's verifier, in the broker's `evaluate` shape.

    A candidate that does not run (no `construct`, an exception, the time limit) is `invalid`, as a broken program is
    in the symbolic pilot. A construction that runs but the verifier refuses (infeasible, malformed, too long) is
    `valid` at 0.0 with the reason in its artifacts, as the task's own evaluator scores it - so the model learns why.

    `record(code, heights, quality)`, when given, receives every scored construction (`EvaluatedHeights.recorder`).
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
        if record is not None:
            record(code, heights, reply["quality"])
        return reply

    return evaluate


def session(provider, *, initials, python, model_calls, evaluations, seconds, model_tokens, ledger,
            cost_metric=pilot_broker.COST_METRICS[1]):
    """Returns `open_for(cell)`: one broker for exactly one cell, scored by that cell's problem.

    Every construction the broker scores goes to `ledger` (an `EvaluatedHeights`) under the cell, for the endpoint.
    """
    @contextlib.contextmanager
    def open_for(cell):
        problem = str(cell["Family"])
        with ProgramBroker(pilot_broker.model_callback(provider, cost_metric=cost_metric),
                           evaluate_callback(problem, python, record=ledger.recorder(cell)),
                           model_calls=model_calls, evaluations=evaluations, seconds=seconds,
                           initial=initials[problem], model_tokens=model_tokens) as broker:
            yield broker

    return open_for


def cell_broker(frozen, provider, *, initials, python, ledger):
    """The per-cell broker with the frozen registration's budget as its ceiling, as `pilot_campaign.cell_broker`."""
    budget = frozen["Spec"]["Budget"]
    return session(provider, initials=initials, python=python, model_calls=budget["ModelCalls"],
                   evaluations=budget["ModelCalls"] + pilot_campaign.EVALUATION_HEADROOM,
                   seconds=budget["WallSeconds"], model_tokens=budget["ModelTokens"], ledger=ledger)


class EvaluatedHeights:
    """The constructions the broker scored, per cell and candidate, so the endpoint is the run the search acted on.

    Every program the first AlphaEvolve pilot returned reads the clock, so a fresh run of it is a different run: one
    c3 program scattered by 0.012 over three runs, and one c5 program scored 0.99668 in the search and 0.99410 in three
    faster runs. The endpoint therefore rescores the heights the search's own evaluation produced. One file per
    candidate hash, under `root/<family>__<arm>__<seed>/`; a candidate scored more than once keeps its best-scored
    construction, the copy a search that keeps its best would have returned.
    """

    def __init__(self, root):
        self.root = Path(root)
        self._lock = threading.Lock()

    def directory(self, cell):
        return self.root / ("%s__%s__%d" % (cell["Family"], cell["Arm"], int(cell["Seed"])))

    def recorder(self, cell):
        directory = self.directory(cell)

        def record(code, heights, quality):
            path = directory / (candidate_hash(code) + ".json")
            with self._lock:
                directory.mkdir(parents=True, exist_ok=True)
                if path.is_file() and json.loads(path.read_text(encoding="utf-8"))["quality"] >= quality:
                    return
                handle, temporary = tempfile.mkstemp(dir=directory, suffix=".tmp")
                with os.fdopen(handle, "w", encoding="utf-8") as out:
                    json.dump({"quality": quality, "heights": heights}, out)
                os.replace(temporary, path)

        return record

    def forget(self, cell):
        """Drops what an earlier, abandoned attempt at the cell recorded, so a fresh attempt starts empty."""
        with self._lock:
            directory = self.directory(cell)
            if directory.is_dir():
                for path in directory.iterdir():
                    path.unlink()
                directory.rmdir()

    def endpoint(self, cell, problem, program_path):
        """The verifier's combined_score for the construction the search's evaluation of the returned program produced.

        The program is matched by its text as written and with CRLF normalised (an engine writing in text mode on
        Windows translates newlines). A program the cell never evaluated, or recorded heights the verifier no longer
        scores as the search saw them, fails the cell rather than falling back to a fresh run.
        """
        raw = Path(program_path).read_bytes().decode("utf-8")
        for code in dict.fromkeys((raw, raw.replace("\r\n", "\n"))):
            path = self.directory(cell) / (candidate_hash(code) + ".json")
            if path.is_file():
                recorded = json.loads(path.read_text(encoding="utf-8"))
                value = float(tasks.score(problem, recorded["heights"])["combined_score"])
                if value != recorded["quality"]:
                    raise ValueError("the verifier scores the recorded construction %r, not the %r the search saw"
                                     % (value, recorded["quality"]))
                return value
        raise ValueError("the returned program has no construction recorded in this cell; it was never evaluated here")


def library_versions(python):
    """`{library: version}` for every library the task statement names, from `python`; raises if any is missing."""
    probe = ("import importlib, json, sys; "
             "print(json.dumps({n: importlib.import_module(n).__version__ for n in sys.argv[1:]}))")
    completed = subprocess.run([str(python), "-I", "-c", probe, *tasks.LIBRARIES], capture_output=True, text=True,
                               timeout=120, check=False)
    if completed.returncode != 0:
        lines = completed.stderr.strip().splitlines()
        raise RuntimeError("%s cannot import the libraries the statement names (%s): %s"
                           % (python, ", ".join(tasks.LIBRARIES), lines[-1] if lines else completed.returncode))
    return json.loads(completed.stdout)


def executor(searchers, *, ledger):
    """Returns an `execute_cell` dispatching on the cell's problem and arm, as `pilot_cells.executor` does for one family.

    `searchers` maps a problem to `{arm: search}`. The endpoint comes from `ledger` (`EvaluatedHeights.endpoint`); a
    fresh attempt at a cell first forgets what an abandoned one recorded.
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
            if not resume:
                ledger.forget(cell)
            outcome = searchers[problem][arm](cell, Path(directory), resume)
            value = ledger.endpoint(cell, problem, outcome["program"])
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
