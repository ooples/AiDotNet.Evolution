import json, re, shutil
from pathlib import Path

MAIN = Path(r"F:/evo-pilot/ae-c3-60-fe0ea75")
SUPPLEMENT = Path(r"F:/evo-pilot/ae-c3-60-supplement-fe0ea75")
OUT = Path(r"F:/evo-c3-evidence/benchmarks/evidence/pilot-e4-alphaevolve-c3-60/fe0ea75")
OUT.mkdir(parents=True, exist_ok=True)
# The combined analysis over all 36 cells is the supplement run's: its journal carries the 31 completed cells.
for name in ["power-analysis.json", "ranked-losses.json", "report.json", "rows.json", "summary.json"]:
    shutil.copyfile(SUPPLEMENT / "evidence" / name, OUT / name)
for name in ["registration.json", "sizing.json", "iterations.json"]:
    shutil.copyfile(MAIN / "registration" / name, OUT / name)
shutil.copyfile(MAIN / "state" / "cells.jsonl", OUT / "cells.jsonl")
shutil.copyfile(SUPPLEMENT / "state" / "cells.jsonl", OUT / "cells-supplement.jsonl")
first = OUT / "first-run"
first.mkdir(exist_ok=True)
for name in ["power-analysis.json", "ranked-losses.json", "report.json", "rows.json", "summary.json"]:
    shutil.copyfile(MAIN / "evidence" / name, first / name)
shutil.copyfile(Path(r"F:/evo-pilot/ae-c3-60-NOTES.txt"), OUT / "LAUNCH-NOTES.txt")

programs = OUT / "programs"
programs.mkdir(exist_ok=True)
completed = {json.loads(l)["Cell"].replace("/", "__") for l in (SUPPLEMENT / "state" / "cells.jsonl").read_text(encoding="utf-8").splitlines()
             if '"finished"' in l}
copied = 0
for name in sorted(completed):
    for root in (SUPPLEMENT, MAIN):
        cell = root / "state" / "cells" / name
        source = next((p for p in (cell / "out" / "best.py", cell / "out" / "best" / "best_program.py") if p.is_file()), None)
        if source is not None:
            shutil.copyfile(source, programs / (name + ".py"))
            copied += 1
            break

cost = re.compile(r'"total_cost_usd":([0-9.eE+-]+)')
runs, total = {}, 0.0
for label, root in (("first-run", MAIN), ("supplement", SUPPLEMENT)):
    for transport in sorted((root / "evidence").glob("transport-*")):
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
        runs[label + "/" + transport.name] = dict(
            calls=len(receipts), canaries=sum(1 for r in receipts if r.get("canary")),
            failed=sum(1 for r in receipts if r.get("status") != "completed"),
            api_key_calls=sum(r.get("api_key_calls", 0) for r in receipts),
            synthetic_continuations=sum(r.get("synthetic_continuations", 0) for r in receipts),
            list_price_estimate_usd=round(price, 4), receipts=receipts)
        total += price
(OUT / "receipts-compact.json").write_text(json.dumps(dict(monetary_cost_usd=None, list_price_estimate_usd_total=round(total, 4), runs=runs), indent=1) + "\n", encoding="utf-8")
for k, v in runs.items():
    print(k, {x: v[x] for x in v if x != "receipts"})
print("total", round(total, 4), "programs", copied)

# Which Claude Code binary served each call: 2.1.316 replaced itself with 2.1.317 during the first run.
names = {"e896a9d8": "2.1.316", "26b01ec2": "2.1.317"}
for label, run in runs.items():
    mix = {}
    for r in run["receipts"]:
        key = names.get(r["executable_sha256"][:8], r["executable_sha256"][:12]) + "/" + r["cli"].split()[0]
        mix.setdefault(key, []).append(r["call"])
    print(label, {k: (len(v), min(v), max(v)) for k, v in mix.items()})
notes = (OUT / "LAUNCH-NOTES.txt").read_text(encoding="utf-8") + Path(r"F:/evo-pilot/ae-c3-60-supplement-NOTES.txt").read_text(encoding="utf-8")
(OUT / "LAUNCH-NOTES.txt").write_text(notes, encoding="utf-8")
