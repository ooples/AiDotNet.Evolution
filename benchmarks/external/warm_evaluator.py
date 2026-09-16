"""Same isolated warm objective and host validation for every search controller."""
import hashlib
import math
import statistics
import time

from docker_sandbox import encode
from program_controls import candidate_hash
from warm_budget import container_observations


class WarmEvaluator:
    def __init__(self, sandbox, class_name, cases, validate, *, identity, samples=3, phase="search", budget=None, owner="shared", stage=None):
        if (type(samples) is not int or not 1 <= samples <= 9 or phase not in ("search", "confirmation")
                or not isinstance(cases, list) or not cases):
            raise ValueError("Invalid warm evaluator configuration")
        self.sandbox, self.class_name, self.cases, self.validate = sandbox, class_name, cases, validate
        self.samples, self.phase = samples, phase
        self.budget, self.owner, self.stage = budget, owner, stage or phase
        self.manifest = {**sandbox.identity, "identity": identity, "phase": phase, "samples": samples,
                         "input_sha256": hashlib.sha256(encode(cases)).hexdigest()}

    def __call__(self, code, *, owner=None):
        started = time.monotonic()
        rows, valid = [], False
        operation_ids = []
        owner = owner or self.owner
        for _ in range(self.samples):
            work = lambda: self.sandbox.run(code, {"class": self.class_name, "problems": self.cases}, phase=self.phase)
            row = (self.budget.run(self.stage, work, resource=self.phase + "_containers", owner=owner,
                                   measure=container_observations, receipt_ids=operation_ids) if self.budget else work())
            rows.append(row)
            validate = lambda: self.validate(row["output"])
            valid = row["status"] == "completed" and row["resource_status"] == "measured" and (
                self.budget.run("validation", validate, owner=owner) if self.budget else validate())
            if not valid:
                break
        times = [r["elapsed_seconds"] for r in rows if r["elapsed_seconds"] is not None]
        duration = statistics.median(times) if len(times) == len(rows) else None
        valid = valid and duration is not None and math.isfinite(duration) and duration > 0
        return dict(candidate_hash=candidate_hash(code), status="valid" if valid else "invalid",
                    quality=1 / duration if valid and duration and duration > 0 else -1e300,
                    duration_seconds=duration, work_units=time.monotonic() - started,
                    work_metric="supervisor_wall_seconds", unknown_work=any(r["unknown_work"] for r in rows),
                    samples=times, sample_ids=[r["id"] for r in rows], phase=self.phase,
                    input_sha256=self.manifest["input_sha256"], resources=[r["resources"] for r in rows],
                    evaluator_sha256=hashlib.sha256(encode(self.manifest)).hexdigest(),
                    budget_operation_ids=operation_ids,
                    startup_samples=[r["startup_seconds"] for r in rows],
                    throughput_cases_per_second=len(self.cases)/duration if valid and duration else None)
