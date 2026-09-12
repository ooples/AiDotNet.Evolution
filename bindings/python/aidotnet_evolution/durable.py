"""Version-1 local worker protocol. IDs and resource amounts stay exact decimal strings.

The host directory and pipe are trusted. A remote supervisor must supply authentication,
hardware isolation and durable physical-operation receipts. Reconciliation is not permission
to execute a lost operation again. Closing this client never refunds unknown reservations.
"""

from __future__ import annotations

import json
import math
import re
import subprocess
import threading
from datetime import datetime
from typing import Any, Mapping, Sequence

MAX_FRAME_BYTES = 16 * 1024 * 1024
_INT64_MAX = (1 << 63) - 1
_UINT64_MAX = (1 << 64) - 1
_OUTCOMES = {"completed", "failed", "rejected", "canceled"}
_DISPOSITIONS = {"accepted", "duplicate", "stale", "duplicate-stale", "unknown-lease", "budget-violation"}
_HEARTBEATS = {"renewed", "expired", "canceled", "completed", "unknown-lease"}


class DurableWorkError(RuntimeError):
    """A refused operation or an uncertain transport failure requiring reconciliation."""


def _unsigned(value: Any, maximum: int = _INT64_MAX) -> bool:
    return isinstance(value, str) and bool(re.fullmatch(r"0|[1-9][0-9]{0,19}", value)) and int(value) <= maximum


def _integer(value: Any, maximum: int = 2_147_483_647) -> bool:
    return type(value) is int and 1 <= value <= maximum


def _amounts(value: Any) -> bool:
    return isinstance(value, dict) and len(value) <= 32 and all(
        isinstance(key, str) and 0 < len(key) <= 64 and key.strip()
        and not any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in key)
        and isinstance(amount, str) and re.fullmatch(r"(0|[1-9][0-9]{0,28})(\.[0-9]{0,27}[1-9])?", amount)
        for key, amount in value.items()
    )


def _identity(value: Any) -> bool:
    return isinstance(value, dict) and isinstance(value.get("runId"), str) and 0 < len(value["runId"]) <= 256 \
        and _unsigned(value.get("evaluationId")) and _integer(value.get("attempt")) \
        and isinstance(value.get("leaseId"), str) and bool(re.fullmatch(r"[0-9a-f]{32}", value["leaseId"]))


def _lease(value: Any) -> bool:
    if not isinstance(value, dict) or not _identity(value.get("identity")) or not isinstance(value.get("workerId"), str) \
            or not value["workerId"] or not isinstance(value.get("canonicalGenomeId"), str) \
            or not isinstance(value.get("payload"), str) or not _integer(value.get("deliveryNumber"), 16) \
            or not isinstance(value.get("expiresAt"), str):
        return False
    try:
        return datetime.fromisoformat(value["expiresAt"].replace("Z", "+00:00")).utcoffset() is not None
    except ValueError:
        return False


def _result(value: Any) -> bool:
    return value is None or (isinstance(value, dict) and _identity(value.get("identity"))
        and isinstance(value.get("payload"), str) and isinstance(value.get("provenance"), str)
        and _amounts(value.get("actual")) and isinstance(value.get("outcome"), str)
        and value["outcome"] in _OUTCOMES and type(value.get("accepted")) is bool)


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise DurableWorkError(message)


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise DurableWorkError("Duplicate response field: " + key)
        result[key] = value
    return result


def _json(text: str) -> Any:
    def invalid_constant(value: str) -> None:
        raise DurableWorkError("Non-JSON numeric constant: " + value)
    return json.loads(text, object_pairs_hook=_unique_object, parse_constant=invalid_constant)


def parse_evaluation_payload(text: str) -> dict[str, Any]:
    """Read the engine bridge envelope without coercing Int64 IDs or UInt64 seeds to floats."""
    _require(len(text.encode("utf-8")) <= 1024 * 1024, "Evaluation envelope exceeds one MiB.")
    value = _json(text)
    _require(isinstance(value, dict) and type(value.get("schema")) is int and value["schema"] == 1
        and isinstance(value.get("genomePayload"), str) and isinstance(value.get("canonicalGenomeId"), str)
        and bool(value["canonicalGenomeId"].strip()) and _unsigned(value.get("evaluationId"))
        and _integer(value.get("attempt")) and _unsigned(value.get("rootSeed"), _UINT64_MAX)
        and _unsigned(value.get("seedStream"), _UINT64_MAX), "Invalid versioned evaluation envelope.")
    return value


class DurableWorkClient:
    """Serialized, bounded requests to one owned host process. Use as a context manager.

    Supply the native executable as host_path, or host_path='dotnet' and the managed DLL
    in host_args. --durable is appended. On timeout the exact owned process is killed and
    the endpoint stays failed; callers must reopen the same journal and reconcile explicitly.
    """

    def __init__(self, config: Mapping[str, Any], *, host_path: str,
                 host_args: Sequence[str] = (), request_timeout: float = 120) -> None:
        _require(type(request_timeout) in (int, float) and math.isfinite(request_timeout)
            and 0 < request_timeout <= 2_147_483.647, "request_timeout must be finite and positive.")
        _require(_amounts(config.get("limits")), "Resources require canonical decimal-string amounts.")
        self._lock = threading.RLock()
        self._timeout = request_timeout
        self._next_id = 1
        self._failed: DurableWorkError | None = None
        self._closed = False
        self._stderr = b""
        self._run_id = config.get("runId")
        self._compatibility = config.get("compatibilityHash")
        self._process = subprocess.Popen([host_path, *host_args, "--durable"], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self._stderr_thread = threading.Thread(target=self._drain_stderr, daemon=True)
        self._stderr_thread.start()
        try:
            self._validate_status(self._call("open", config=dict(config)))
        except BaseException:
            self._terminate()
            raise

    @property
    def pid(self) -> int:
        """The exact child PID, for diagnostics/supervision only."""
        return self._process.pid

    def _drain_stderr(self) -> None:
        try:
            while True:
                chunk = self._process.stderr.read(4096)
                if not chunk:
                    return
                self._stderr = (self._stderr + chunk)[-4096:]
        except (OSError, ValueError):
            return

    def _terminate(self) -> None:
        if self._process.poll() is None:
            self._process.kill()
        try:
            self._process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass
        for stream in (self._process.stdin, self._process.stdout, self._process.stderr):
            stream.close()

    def _fatal(self, message: str) -> None:
        self._failed = self._failed or DurableWorkError(message)
        self._terminate()
        raise self._failed

    def _call(self, op: str, **fields: Any) -> dict[str, Any]:
        with self._lock:
            if self._failed:
                raise self._failed
            _require(not self._closed, "Endpoint is closed.")
            request_id = self._next_id
            _require(request_id <= (1 << 53) - 1, "Request identity space exhausted.")
            self._next_id += 1
            frame = json.dumps({**fields, "id": request_id, "protocol": 1, "op": op}, ensure_ascii=False,
                allow_nan=False, separators=(",", ":")).encode("utf-8")
            _require(len(frame) <= MAX_FRAME_BYTES, "Request exceeds frame byte bound.")
            completed: list[bytes | BaseException] = []

            def exchange() -> None:
                try:
                    remaining = memoryview(frame + b"\n")
                    while remaining:
                        written = self._process.stdin.write(remaining)
                        if not written:
                            raise BrokenPipeError("Host input pipe closed.")
                        remaining = remaining[written:]
                    self._process.stdin.flush()
                    completed.append(self._process.stdout.readline(MAX_FRAME_BYTES + 2))
                except BaseException as error:
                    completed.append(error)

            # A stalled write is bounded by the same deadline as a stalled read. Only one
            # exchange can exist at a time; killing this owned child releases either pipe.
            thread = threading.Thread(target=exchange, daemon=True)
            thread.start()
            thread.join(self._timeout)
            if thread.is_alive():
                self._fatal(f"Durable '{op}' timed out; reconcile the original store before retrying.")
            if not completed or isinstance(completed[0], BaseException):
                self._fatal("Durable pipe failed; reconcile unacknowledged work. " + str(completed))
            raw = completed[0]
            if len(raw) > MAX_FRAME_BYTES or not raw.endswith(b"\n"):
                self._fatal("Oversized, truncated or closed durable response; reconcile the original store.")
            try:
                value = _json(raw.decode("utf-8"))
                _require(isinstance(value, dict) and type(value.get("id")) is int and value["id"] == request_id
                    and type(value.get("protocol")) is int and value["protocol"] == 1 and type(value.get("ok")) is bool,
                    "Invalid correlation or durable protocol downgrade.")
            except (ValueError, DurableWorkError) as error:
                self._fatal(str(error))
            if not value["ok"]:
                _require(isinstance(value.get("error"), str), "Malformed refusal from durable host.")
                raise DurableWorkError(value["error"])
            return value

    def _checked(self, value: Any, condition: bool, message: str) -> Any:
        if not condition:
            self._fatal(message + " Reconcile the original store.")
        return value

    def _validate_status(self, value: dict[str, Any]) -> dict[str, Any]:
        source = value.get("sourceSessionId")
        return self._checked(value, value.get("runId") == self._run_id and value.get("compatibilityHash") == self._compatibility
            and type(value.get("wasRecovered")) is bool and "sourceSessionId" in value
            and (source is None or (isinstance(source, str) and bool(re.fullmatch(r"[0-9a-f]{32}", source))))
            and value.get("supportsExactSearchContinuation") is False and value.get("searchState") == "not-owned"
            and isinstance(value.get("searchContinuationGuarantee"), str) and "fork" in value["searchContinuationGuarantee"]
            and _amounts(value.get("spent")) and _amounts(value.get("reserved"))
            and all(_unsigned(value.get(key)) for key in ("admitted", "settled", "denied"))
            and type(value.get("maximumViolated")) is bool, "Invalid durable status response.")

    def enqueue(self, job: Mapping[str, Any]) -> bool:
        _require(_unsigned(job.get("evaluationId")) and _integer(job.get("attempt")), "Invalid logical work identity.")
        _require(_amounts(job.get("estimated")) and _amounts(job.get("maximum")), "Resources require decimal-string amounts.")
        value = self._call("enqueue", job=dict(job)).get("enqueued")
        return self._checked(value, type(value) is bool, "Invalid enqueue response.")

    def claim(self, worker: Mapping[str, Any]) -> dict[str, Any] | None:
        """Null means no compatible work now, never search completion."""
        value = self._call("claim", worker=dict(worker))
        lease = value.get("lease")
        valid = (value.get("available") is False and "lease" in value and lease is None) or (
            value.get("available") is True and _lease(lease) and lease["workerId"] == worker.get("workerId")
            and lease["identity"]["runId"] == self._run_id)
        return self._checked(lease, valid, "Invalid claim response.")

    def heartbeat(self, identity: Mapping[str, Any], worker_id: str) -> str:
        _require(_identity(identity), "The original complete durable identity is required.")
        value = self._call("heartbeat", identity=dict(identity), workerId=worker_id).get("status")
        return self._checked(value, isinstance(value, str) and value in _HEARTBEATS, "Invalid heartbeat response.")

    def cancel(self, evaluation_id: str, attempt: int) -> bool:
        _require(_unsigned(evaluation_id) and _integer(attempt), "Invalid logical work identity.")
        value = self._call("cancel", evaluationId=evaluation_id, attempt=attempt).get("canceled")
        return self._checked(value, type(value) is bool, "Invalid cancel response.")

    def commit(self, receipt: Mapping[str, Any]) -> str:
        _require(_identity(receipt.get("identity")) and _amounts(receipt.get("actual"))
            and isinstance(receipt.get("outcome"), str) and receipt["outcome"] in _OUTCOMES, "A complete original ticket and actual decimal receipt are required.")
        value = self._call("commit", **dict(receipt)).get("disposition")
        return self._checked(value, isinstance(value, str) and value in _DISPOSITIONS, "Invalid commit response.")

    def result(self, evaluation_id: str, attempt: int) -> dict[str, Any] | None:
        _require(_unsigned(evaluation_id) and _integer(attempt), "Invalid logical work identity.")
        reply = self._call("result", evaluationId=evaluation_id, attempt=attempt)
        value = reply.get("result")
        return self._checked(value, "result" in reply and _result(value) and (value is None or (
            value["identity"]["evaluationId"] == evaluation_id and value["identity"]["attempt"] == attempt
            and value["identity"]["runId"] == self._run_id)), "Invalid logical result response.")

    def delivery(self, identity: Mapping[str, Any], worker_id: str) -> dict[str, Any] | None:
        _require(_identity(identity), "The original complete durable identity is required.")
        reply = self._call("delivery", identity=dict(identity), workerId=worker_id)
        value = reply.get("result")
        return self._checked(value, "result" in reply and _result(value) and (value is None or value["identity"] == identity), "Invalid delivery receipt.")

    def unsettled(self, worker_id: str, after_lease_id: str | None = None) -> dict[str, Any]:
        """One keyset page for reconciliation only; new concurrent claims require restarting a scan."""
        fields = {"workerId": worker_id}
        if after_lease_id is not None:
            fields["afterLeaseId"] = after_lease_id
        value = self._call("unsettled", **fields)
        lease = value.get("lease")
        valid = value.get("reconciliationOnly") is True and "lease" in value and "nextAfterLeaseId" in value and (
            (lease is None and value["nextAfterLeaseId"] is None) or (_lease(lease)
                and lease["workerId"] == worker_id and lease["identity"]["runId"] == self._run_id
                and value["nextAfterLeaseId"] == lease["identity"]["leaseId"]))
        return self._checked(value, valid, "Invalid reconciliation response.")

    def status(self) -> dict[str, Any]:
        return self._validate_status(self._call("status"))

    def close(self) -> None:
        with self._lock:
            if self._closed:
                return
            try:
                value = self._call("close")
                _require(value.get("closed") is True, "Close was not confirmed.")
            finally:
                self._closed = True
                self._terminate()

    def __enter__(self) -> DurableWorkClient:
        return self

    def __exit__(self, kind: Any, value: Any, traceback: Any) -> None:
        if kind is None:
            self.close()
        else:
            try:
                self.close()
            except Exception:
                pass  # Preserve the evaluator's original failure, but always tear down.
