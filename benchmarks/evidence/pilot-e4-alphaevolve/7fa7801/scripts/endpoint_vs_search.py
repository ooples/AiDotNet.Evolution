import json, sys
from pathlib import Path

R = Path(r"F:/evo-pilot/ae-pilot-7fa7801/state")
rows = {}
for line in (R / "cells.jsonl").read_text(encoding="utf-8").splitlines():
    r = json.loads(line)
    if r.get("Event") == "finished":
        rows[r["Cell"].replace("/", "__")] = r["Row"]

out = []
for name, row in sorted(rows.items()):
    cell = R / "cells" / name / "out"
    searched, at_seed, evaluated = None, 0, 0
    if row["Arm"] == "aidotnet":
        traces = sorted(cell.glob("trace-*.jsonl"))
        q = []
        for t in traces:
            for line in t.read_text(encoding="utf-8").splitlines():
                d = json.loads(line)
                if d.get("status") == "Completed" and d.get("quality") is not None:
                    q.append(d["quality"])
        evaluated = len(q)
        if q:
            searched = max(q)
            at_seed = sum(1 for v in q[1:] if v == q[0])
    else:
        info = cell / "best" / "best_program_info.json"
        if info.is_file():
            searched = json.loads(info.read_text(encoding="utf-8"))["metrics"].get("combined_score")
    out.append(dict(Family=row["Family"], Arm=row["Arm"], Seed=row["Seed"], Endpoint=row["Value"],
                    SearchBest=searched, Gap=None if searched is None else searched - row["Value"],
                    Evaluated=evaluated, EqualToSeed=at_seed))
json.dump(out, open(sys.argv[1], "w"), indent=1)
for o in out:
    print(o["Family"][:2], o["Arm"][:14].ljust(14), o["Seed"], round(o["Endpoint"], 5), None if o["SearchBest"] is None else round(o["SearchBest"], 5), None if o["Gap"] is None else f"{o['Gap']:+.5f}", o["Evaluated"], o["EqualToSeed"])
