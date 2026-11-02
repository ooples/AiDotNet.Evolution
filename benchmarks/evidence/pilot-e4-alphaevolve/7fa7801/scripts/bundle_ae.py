import json, re, shutil
from pathlib import Path

R = Path(r"F:/evo-pilot/ae-pilot-7fa7801")
OUT = Path(r"F:/evo-pilot-main/benchmarks/evidence/pilot-e4-alphaevolve/7fa7801")
OUT.mkdir(parents=True, exist_ok=True)
for name in ["power-analysis.json", "ranked-losses.json", "report.json", "rows.json", "summary.json"]:
    shutil.copyfile(R / "evidence" / name, OUT / name)
for name in ["registration.json", "sizing.json"]:
    shutil.copyfile(R / "registration" / name, OUT / name)
shutil.copyfile(R / "state" / "cells.jsonl", OUT / "cells.jsonl")
shutil.copyfile(Path(r"F:/evo-pilot/ae-pilot-NOTES.txt"), OUT / "LAUNCH-NOTES.txt")

# Every returned program, so the endpoint can be re-run by anyone.
programs = OUT / "programs"
programs.mkdir(exist_ok=True)
for cell in sorted((R / "state" / "cells").iterdir()):
    if not cell.is_dir() or cell.name.startswith("__"):
        continue
    source = cell / "out" / "best.py" if (cell / "out" / "best.py").is_file() else cell / "candidate.py"
    if source.is_file():
        shutil.copyfile(source, programs / (cell.name + ".py"))

cost = re.compile(r'"total_cost_usd":([0-9.eE+-]+)')
runs, total = {}, 0.0
for transport in sorted((R / "evidence").glob("transport-*")):
    receipts, price = [], 0.0
    base = transport / "sonnet"
    calls = sorted((p for p in base.glob("*") if p.is_dir()), key=lambda p: int(p.name)) if base.is_dir() else []
    for call in calls:
        if not (call / "receipt.json").is_file():
            continue
        receipt = json.loads((call / "receipt.json").read_text(encoding="utf-8"))
        receipt.pop("arguments", None)
        receipt["call"] = int(call.name)
        events = (call / "events.jsonl").read_text(encoding="utf-8", errors="replace") if (call / "events.jsonl").is_file() else ""
        found = cost.findall(events)
        if found:
            price += float(found[-1])
        receipts.append(receipt)
    runs[transport.name] = dict(
        calls=len(receipts), canaries=sum(1 for r in receipts if r.get("canary")),
        failed=sum(1 for r in receipts if r.get("status") != "completed"),
        api_key_calls=sum(r.get("api_key_calls", 0) for r in receipts),
        synthetic_continuations=sum(r.get("synthetic_continuations", 0) for r in receipts),
        list_price_estimate_usd=round(price, 4), receipts=receipts)
    total += price
(OUT / "receipts-compact.json").write_text(json.dumps(dict(monetary_cost_usd=None, list_price_estimate_usd_total=round(total, 4), runs=runs), indent=1) + "\n", encoding="utf-8")
for k, v in runs.items():
    print(k, {x: v[x] for x in v if x != "receipts"})
print("total", round(total, 4), "programs", len(list(programs.iterdir())))
