"""Shared evaluator: output checking on the host, wall timing from the Docker daemon."""
import hashlib
import statistics
import time

from docker_sandbox import encode
from program_controls import candidate_hash
from program_tasks import TASKS, expected, problems


class IsolatedProgramEvaluator:
    def __init__(self, sandbox, task_id, seed, *, samples=3, phase="search"):
        if task_id not in TASKS or type(samples) is not int or not 1 <= samples <= 9 or phase not in ("search", "confirmation"):
            raise ValueError("Invalid evaluator configuration")
        self.sandbox, self.task_id, self.samples, self.phase = sandbox, task_id, samples, phase
        self.cases = problems(task_id, seed)
        started = time.monotonic()
        self.answer = encode(expected(task_id, self.cases))
        self.oracle_setup_seconds = time.monotonic() - started
        self.manifest = {**sandbox.identity, "identity": "isolated-program-v1", "task": task_id,
                         "phase": phase, "samples": samples, "input_sha256": hashlib.sha256(encode(self.cases)).hexdigest(),
                         "oracle_sha256": hashlib.sha256(self.answer).hexdigest(), "oracle_setup_seconds": self.oracle_setup_seconds,
                         "isolation": "Inspected nonroot Linux container; readonly root/input, network none, caps dropped, no-new-privileges, memory/pid/time caps"}

    def __call__(self, code):
        identity = candidate_hash(code)
        started = time.monotonic()
        rows = []
        valid = True
        for _ in range(self.samples):
            row = self.sandbox.run(code, {"class": TASKS[self.task_id]["class"], "problems": self.cases}, phase=self.phase)
            rows.append(row)
            valid = row["status"] == "completed" and encode(row.get("output")) == self.answer
            # Early rejection is explicit and retains the actual work, not an invented full-sample cost.
            if not valid:
                break
        duration = statistics.median(r["elapsed_seconds"] for r in rows)
        return {"candidate_hash": identity, "status": "valid" if valid else "invalid",
                "quality": 1 / duration if valid else -1e300, "duration_seconds": duration,
                "work_units": time.monotonic() - started, "work_metric": "supervisor_wall_seconds",
                "unknown_work": any(r["unknown_work"] for r in rows), "sample_ids": [r["id"] for r in rows],
                "samples": [r["elapsed_seconds"] for r in rows], "input_sha256": self.manifest["input_sha256"],
                "oracle_sha256": self.manifest["oracle_sha256"], "phase": self.phase}
