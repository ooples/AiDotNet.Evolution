import json
import sys

mode = sys.argv[1]
for line in sys.stdin:
    request = json.loads(line)
    response = {"id": request["id"], "protocol": 0 if mode == "downgrade" else 1, "ok": True}
    if request["op"] == "open":
        response.update(runId="run", compatibilityHash="compat", wasRecovered=False, sourceSessionId=None,
            supportsExactSearchContinuation=False, searchContinuationGuarantee="explicit fork required", searchState="not-owned",
            spent={}, reserved={}, admitted="0", settled="0", denied="0", maximumViolated=False)
    elif mode == "timeout":
        continue
    elif mode == "oversized":
        sys.stdout.write("x" * (16 * 1024 * 1024 + 1) + "\n")
        sys.stdout.flush()
        continue
    elif mode == "uncorrelated":
        response["id"] += 1
    elif request["op"] == "claim":
        response["available"] = False  # Deliberately omit the required null lease.
    else:
        response["closed"] = True
    print(json.dumps(response), flush=True)
