"""Shared warm-study admission journal. Counts are caps; tokens/time are observations.

Reservations are flushed before side effects. This is evidence, not a resume service:
an interrupted registration stays consumed and may not be replayed.
"""
import copy
import math
import os
from pathlib import Path
import threading
import time

from docker_sandbox import encode
from warm_study_design import call_cap, digest

SCHEMA = "warm-budget-v1"
RESOURCES = {"model_calls", "search_containers", "confirmation_containers"}


def requirements(grid, iterations, samples, confirmation_pairs=None, screening=None):
    tracks = sum(len(row["tracks"]) for row in grid)
    screened = bool(screening and screening["enabled"])
    rejection_audits = (sum(min(iterations*len(row["tracks"]),screening["audit_candidates"]) for row in grid)
                       * 2 * screening["audit_pairs"] if screened else 0)
    return dict(model_calls=call_cap(grid, iterations),
                search_containers=tracks * ((iterations + 1) * (samples + int(screened))
                                            + (screening["baseline_samples"]-1 if screened else 0)),
                confirmation_containers=tracks * (2 * (samples if confirmation_pairs is None else confirmation_pairs) + 8)
                                        + rejection_audits)


def integer(value):
    return type(value) is int and 0 <= value <= 100_000_000


def validate_limits(limits):
    if not isinstance(limits, dict) or set(limits) != RESOURCES or not all(integer(v) for v in limits.values()):
        raise ValueError("Explicit bounded integer campaign limits required")


class CampaignBudget:
    def __init__(self, path, limits, plan_sha256):
        validate_limits(limits)
        self.limits = dict(limits)
        self.spent = dict.fromkeys(limits, 0)
        self.pending = dict.fromkeys(limits, 0)
        self.rows = []
        self.closed = False
        self.denied = 0
        self.plan_sha256 = plan_sha256
        self.path = Path(path)
        self._lock = threading.RLock()
        with self.path.open("xb") as stream:
            stream.write(encode(dict(schema=SCHEMA, limits=limits, plan_sha256=plan_sha256)) + b"\n")
            stream.flush()
            os.fsync(stream.fileno())

    def _append(self, event):
        try:
            with self.path.open("ab") as stream:
                stream.write(encode(event) + b"\n")
                stream.flush()
                os.fsync(stream.fileno())
        except BaseException:
            self.closed = True
            raise

    def run(self, stage, work, *, resource=None, owner="shared", measure=None, receipt_ids=None):
        if not isinstance(stage, str) or not stage or not isinstance(owner, str) or not owner:
            raise ValueError("Stage and owner required")
        with self._lock:
            if resource is not None and resource not in self.limits:
                raise ValueError("Undeclared resource")
            if self.closed or len(self.rows) >= 1_000_000 or (resource and self.spent[resource] + self.pending[resource] >= self.limits[resource]):
                self.denied += 1
                raise ValueError("Campaign budget refused dispatch")
            row = dict(id=len(self.rows), stage=stage, owner=owner, resource=resource,
                       status="pending", observations={})
            self._append(dict(event="reserve", **row))
            self.rows.append(row)
            if resource:
                self.pending[resource] += 1
        started = time.monotonic()
        try:
            value = work()
            observations = measure(value) if measure else {}
            if (not isinstance(observations, dict) or any(type(v) not in (int, float) or not math.isfinite(v) or v < 0
                                                       for v in observations.values())):
                raise ValueError("Invalid resource observations")
            row.update(status="completed", observations=observations)
            return value
        except BaseException:
            row["status"] = "unknown"
            self.closed = True
            raise
        finally:
            with self._lock:
                row["wall_seconds"] = time.monotonic() - started
                if resource:
                    self.pending[resource] -= 1
                    self.spent[resource] += 1
                self._append(dict(event="settle", **row))
                if receipt_ids is not None:
                    receipt_ids.append(row["id"])

    def snapshot(self):
        with self._lock:
            value = dict(schema=SCHEMA, plan_sha256=self.plan_sha256, limits=self.limits,
                         spent=self.spent, pending=self.pending, rows=self.rows,
                         closed=self.closed, denied=self.denied)
            value = copy.deepcopy(value)
            value["sha256"] = digest(value)
            return value


def model_observations(value):
    if (not isinstance(value, dict) or type(value.get("cost_units")) is not int or value["cost_units"] < 0
            or value.get("cost_metric") != "reported_input_plus_output_tokens"):
        raise ValueError("Model usage is unknown")
    return dict(reported_tokens=value["cost_units"])


def container_observations(value):
    if value.get("unknown_work") is not False or value.get("resource_status") != "measured":
        raise ValueError("Container usage is unknown")
    # Counters end at the response probe, not at host cleanup; keep that scope explicit.
    resources = value.get("resources") or {}
    return {key: resources[key] for key in ("cpu_seconds_through_response", "peak_bytes_through_response")}


def validate_accounting(report):
    """Check the full receipt set, expected stage counts and independent totals."""
    plan = report["plan"]
    accounting = report["accounting"]
    unsigned = {k: v for k, v in accounting.items() if k != "sha256"}
    if (accounting["schema"] != SCHEMA or digest(unsigned) != accounting["sha256"]
            or accounting["plan_sha256"] != report["plan_sha256"] or accounting["limits"] != plan["resource_limits"]
            or accounting["closed"] or any(accounting["pending"].values())):
        raise ValueError("Unreconciled campaign accounting")
    validate_limits(accounting["limits"])
    if (accounting["limits"] != requirements(plan["grid"],plan["iterations"],plan["samples"],plan.get("noise_policy",{}).get("pairs"),plan.get("screening_policy"))
            or report["cumulative_model_calls"] != plan["cumulative_calls_before"] + report["model_calls"]
            or report["cumulative_container_attempts"] != plan["cumulative_containers_before"] + report["evaluator_attempts"]
            or report["cumulative_model_calls"] > plan["call_limit"]
            or report["cumulative_container_attempts"] > plan["container_limit"]):
        raise ValueError("Campaign cumulative spending differs or exceeds authorization")
    rows = accounting["rows"]
    if [r["id"] for r in rows] != list(range(len(rows))) or any(r["status"] != "completed" for r in rows):
        raise ValueError("Missing, duplicate or unknown operation receipts")
    if any(type(r.get("wall_seconds")) not in (int, float) or not math.isfinite(r["wall_seconds"]) or r["wall_seconds"] < 0
           or not isinstance(r.get("observations"), dict)
           or any(type(v) not in (int, float) or not math.isfinite(v) or v < 0 for v in r["observations"].values()) for r in rows):
        raise ValueError("Invalid accounting observations")
    expected_stages = {"sandbox-setup": 1, "verification": 2 * len(plan["grid"]),
                       "search-setup": len(plan["grid"]), "transport-setup": len(plan["grid"]),
                       "diagnostic-setup": len(plan["grid"]), "audit-setup": 4 * len(plan["grid"]),
                       "controller": sum(len(cell["tracks"]) for cell in plan["grid"])}
    screened = plan.get("screening_policy",{}).get("enabled",False)
    if screened:
        expected_stages["screen-setup"] = len(plan["grid"])
    if any(sum(r["stage"] == stage for r in rows) != count for stage, count in expected_stages.items()):
        raise ValueError("Missing setup or verification receipts")
    actual = {key: sum(r["resource"] == key for r in rows) for key in RESOURCES}
    allowed = {"model_calls": {"model"}, "search_containers": {"search"},
               "confirmation_containers": {"diagnostic", "audit"}, None: set(expected_stages) | {"validation"}}
    if screened:
        allowed["search_containers"].add("screen")
        allowed["confirmation_containers"].add("rejection-audit")
    if any(r["resource"] not in allowed or r["stage"] not in allowed[r["resource"]] for r in rows):
        raise ValueError("Resource stage differs from registered protocol")
    if actual != accounting["spent"] or any(actual[k] > accounting["limits"][k] for k in RESOURCES):
        raise ValueError("Campaign totals differ from receipts or limits")
    if actual["model_calls"] != report["model_calls"] or sum(actual[k] for k in ("search_containers", "confirmation_containers")) != report["evaluator_attempts"]:
        raise ValueError("Independent producer counts differ")
    ids, search_ids = [], []
    for index, cell in enumerate(report["rows"]):
        from warm_screening import audit_receipts
        for receipt in audit_receipts(cell):
            if len(receipt["budget_operation_ids"]) != len(receipt["sample_ids"]):
                raise ValueError("Missing rejection audit charges")
            ids.extend(receipt["budget_operation_ids"])
        if len(cell["pairs"]) != len(cell["tracks"]):
            raise ValueError("Missing final acceptance work")
        for pair in cell["pairs"]:
            owner = f"cell-{index:04d}/{pair['mode']}/{pair['method']}"
            owned = [r for r in rows if r["owner"] == owner]
            models = [r for r in owned if r["resource"] == "model_calls"]
            if len(models) != pair["model_calls"] or sum(r["observations"]["reported_tokens"] for r in models) != pair["model_tokens"]:
                raise ValueError("Controller model accounting differs")
            audits = pair["audits"] + pair["original_audits"]
            if len(pair["audits"]) != 4 or len(pair["original_audits"]) != 4:
                raise ValueError("Missing both-executable audits")
            for receipt in [pair["original"], pair["selected"], *audits]:
                if not receipt["budget_operation_ids"] or len(receipt["budget_operation_ids"]) != len(receipt["sample_ids"]):
                    raise ValueError("Missing final sample charges")
                if any(not integer(i) or i >= len(rows) or rows[i]["owner"] != owner for i in receipt["budget_operation_ids"]):
                    raise ValueError("Final sample ownership differs")
                ids.extend(receipt["budget_operation_ids"])
        for track in cell["search_runs"]:
            for receipt in track["receipts"]:
                if receipt["operation"] == "evaluate" and receipt["status"] == "completed":
                    result = receipt["result"]
                    if len(result["budget_operation_ids"]) != len(result["sample_ids"]) or not result["sample_ids"]:
                        raise ValueError("Missing search sample charges")
                    search_ids.extend(result["budget_operation_ids"])
    expected = [r["id"] for r in rows if r["resource"] == "confirmation_containers"]
    if len(ids) != len(set(ids)) or sorted(ids) != expected:
        raise ValueError("Acceptance receipts do not bind all confirmation work")
    if len(search_ids) != len(set(search_ids)) or sorted(search_ids) != [r["id"] for r in rows if r["resource"] == "search_containers"]:
        raise ValueError("Search receipts do not bind all evaluation work")
    return accounting
