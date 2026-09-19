# Python durable workers

This dependency-free Python 3.10+ client speaks the same versioned JSON protocol as the
TypeScript client and `EvolutionWorkProtocol`. It launches an owned native host process,
not a Python.NET/FFI runtime. From this directory, install with `pip install .`, or use this
source directory on `PYTHONPATH`. Supply a locally built/released host binary explicitly.

```python
from aidotnet_evolution import DurableWorkClient

config = {
    "directory": "/owned/run-work",
    "runId": "example",
    "compatibilityHash": "task-codec-evaluator-v1",
    "limits": {"evaluation_calls": "100"},
}
with DurableWorkClient(config, host_path="/path/to/aidotnet-evolution-host") as work:
    work.enqueue({
        "evaluationId": "1", "attempt": 1, "canonicalGenomeId": "integer:7",
        "payload": "7", "estimated": {"evaluation_calls": "1"},
        "maximum": {"evaluation_calls": "1"},
    })
    lease = work.claim({"workerId": "process-incarnation-1", "compatibilityHash": config["compatibilityHash"]})
    if lease is not None:
        # Toy evaluator only. Production workers persist execution IDs and receipts;
        # do not rerun this operation simply because a reply was lost.
        output = str(int(lease["payload"]) ** 2)
        work.commit({
            "identity": lease["identity"], "workerId": lease["workerId"],
            "payload": output, "provenance": "integer-square-v1",
            "actual": {"evaluation_calls": "1"}, "outcome": "completed",
        })
```

For a managed development host use `host_path="dotnet"` and
`host_args=["/path/to/aidotnet-evolution-host.dll"]`. `--durable` is appended automatically.
All evaluation IDs and decimal resource amounts are **strings**; no implicit float conversion
is performed. `parse_evaluation_payload` preserves all 64-bit seeds from an engine bridge.

Null claims mean unavailable now, not search completion. Reopen the identical configuration
to reconcile `unsettled`, `delivery`, `result` and `status`. Never blindly re-execute lost work,
refund an expired physical reservation, or replace the journal to reset a real budget.
Timeouts kill only the owned child; context-manager teardown preserves an evaluator's
original error. Close never deletes state or cancels physical work.

This is trusted local IPC, not network authentication or exact engine recovery. The caller
owns physical execution, receipts, evaluator/codec versioning, access control and search
fork provenance. See the full [protocol contract](../../docs/DURABLE_WORKER_PROTOCOL.md).

Tests (require the host; no missing-host skips):

```powershell
$env:AIDOTNET_DURABLE_HOST_PATH = "C:/path/to/aidotnet-evolution-host.exe"
python -W error::ResourceWarning -m unittest discover -s bindings/python/tests -v
```

Run that command from the repository root. To test a managed host, set
`AIDOTNET_DURABLE_HOST_DLL` instead. Tests include a real child-process kill after dispatch,
exact decimal/Int64 recovery, cancellation/hardware matching, malformed replies and timeouts.
