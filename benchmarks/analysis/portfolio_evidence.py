"""Fail-closed checks tying US-08 endpoints and rewards to engine outcomes."""
import math
import statistics


def finite(value, low=0, high=float("inf")):
    return type(value) in (int, float) and math.isfinite(value) and low <= value <= high


def verify_row(row, budget):
    try:
        _verify(row, budget)
    except (KeyError, TypeError, IndexError, ZeroDivisionError) as error:
        raise ValueError("Incomplete portfolio evidence") from error


def _verify(row, budget):
    if not all(finite(row[k]) for k in ("Seconds", "CpuSeconds")):
        raise ValueError("Invalid measured time")
    observations = row["Observations"]
    receipts = row["Resources"]["Receipts"]
    by_operation = {r["OperationId"]: r for r in receipts}
    if len(by_operation) != len(receipts) or sum(r["Charged"]["Amounts"]["cost_units"] for r in receipts) != row["Resources"]["Spent"]["cost_units"]:
        raise ValueError("Ledger total lacks unique retained receipts")
    if sum(r["Stage"] == "Proposal" for r in receipts) != row["ProposalCalls"]:
        raise ValueError("Proposal dispatch count differs from ledger")
    measured = {}
    for observation in observations:
        q, descriptors = observation["Quality"], observation["Descriptors"]
        if (not finite(q, 0, 1) or len(descriptors) != 2 or
                not all(finite(d, -1, 1) for d in descriptors) or
                type(observation["PrimitiveCases"]) is not int or observation["PrimitiveCases"] <= 0):
            raise ValueError("Invalid raw observation")
        times = observation["TimingsMilliseconds"]
        if row["Family"] == "kernel":
            if len(times) != 4 or not all(finite(t) and t > 0 for t in times) or not math.isclose(q, 1 / (1 + statistics.median(times[1:]))):
                raise ValueError("Kernel quality is not bound to raw timing")
        elif times:
            raise ValueError("Unexpected timing source")
        measured.setdefault(observation["Genome"], []).append(observation)
    terminals = row["Terminals"]
    identities = [t["EvaluationId"] for t in terminals]
    if len(set(identities)) != len(identities) or not terminals:
        raise ValueError("Missing or duplicate engine outcomes")
    by_id = {t["EvaluationId"]: t for t in terminals}
    for terminal in terminals:
        if terminal["Status"] == "Completed":
            if not any(o["Quality"] == terminal["Quality"] for o in measured.get(terminal["GenomeId"], [])):
                raise ValueError("Engine quality lacks a raw observation")
        elif terminal["Status"] not in ("Duplicate", "Rejected", "Failed", "Skipped"):
            raise ValueError("Unexpected engine failure")
        if terminal["Status"] in ("Failed", "Skipped", "Rejected") and not (
                "resource_budget_reached" in terminal["Diagnostics"] or
                ("variation_failure" in terminal["Diagnostics"] and row["Resources"]["Spent"]["cost_units"] > budget - 3)):
            raise ValueError("Unexpected engine failure")
    cells = set()
    for elite in row["Elites"]:
        cell = tuple(elite["Cell"])
        if len(cell) != 2 or cell in cells:
            raise ValueError("Invalid final archive cells")
        cells.add(cell)
        if not any(o["Quality"] == elite["Quality"] and tuple(min(7, int((d + 1) * 4)) for d in o["Descriptors"]) == cell
                   for o in measured.get(elite["GenomeId"], [])):
            raise ValueError("Final elite lacks matching raw evidence")
        if not any(t["Status"] == "Completed" and t["GenomeId"] == elite["GenomeId"] and t["Quality"] == elite["Quality"] for t in terminals):
            raise ValueError("Uncommitted final elite")
    if row["Status"] == "completed" and (
            row["Quality"] != max((e["Quality"] for e in row["Elites"]), default=0) or
            row["Diversity"] != len(cells) / 64):
        raise ValueError("Unbound final endpoint")
    credits = row["Credits"]
    adaptive = row["Method"].startswith("adaptive-") or row["Method"] == "uniform-portfolio"
    if adaptive and {c["EvaluationId"] for c in credits} != {t["EvaluationId"] for t in terminals if t["Generation"] > 0}:
        raise ValueError("Missing or extra engine credit")
    if not adaptive and credits:
        raise ValueError("Static operator emitted adaptive credit")
    for credit in credits:
        terminal = by_id[credit["EvaluationId"]]
        if any(credit[k] != terminal[k] for k in ("Generation", "GenomeId", "Status", "Quality")) or credit["EvaluationAttempts"] != terminal["Attempts"] or credit["EvaluationCostUnits"] != terminal["CostUnits"]:
            raise ValueError("Credit does not identify its engine outcome")
        receipt = credit["ProposalCost"]
        cost = receipt["Charged"]["Amounts"]["cost_units"]
        if credit["OperatorId"] not in ("resource-metered:mutation", "resource-metered:crossover", "resource-metered:restart", "resource-metered:refinement") or not finite(cost):
            raise ValueError("Unknown operator or cost")
        if cost:
            retained = by_operation[receipt["OperationId"]]
            if retained["Stage"] != "Proposal" or any(retained[k] != receipt[k] for k in ("Charged", "Outcome", "ExceededMaximum")):
                raise ValueError("Credit receipt differs from settled ledger operation")
            expected_cost = 3 if credit["OperatorId"] == "resource-metered:refinement" else 1
            if cost != expected_cost:
                raise ValueError("Refinement or dispatch cost was changed")
        valid = (credit["Status"] == "Completed" and credit["EvaluationAttempts"] > 0 and
                 credit["CacheStatus"] != "Hit" and (credit.get("MeasurementOrigin") is None or credit["MeasurementOrigin"]["Kind"] == "Measured") and
                 credit["Insertion"] in ("Inserted", "Replaced", "InsertedWithEviction") and
                 receipt["Outcome"] == "Completed" and not receipt["ExceededMaximum"])
        gain = 0
        if valid:
            gain = max(0, min(1, credit["Quality"] - credit["ParentQuality"])) if row["Method"] == "adaptive-parent" else 1
        expected = gain / max(1, cost + credit["EvaluationCostUnits"])
        if not finite(credit["Reward"], 0, 1) or not math.isclose(credit["Reward"], expected, abs_tol=1e-14):
            raise ValueError("Reward differs from registered cost-normalized gain")
