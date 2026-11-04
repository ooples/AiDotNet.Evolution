"""Builds the supplement root: the c3 run's frozen registration and its completed cells, without the failed ones.

campaign_runner skips every cell its journal records as finished, so the launcher run on this root (with the same
--confirm) reruns exactly the 2 cells the weekly usage limit failed, at their registered seeds, and analyses all 18
cells together.
"""
import json, shutil
from pathlib import Path

SOURCE = Path(r"F:/evo-pilot/ae-c3-60-fe0ea75")
TARGET = Path(r"F:/evo-pilot/ae-c3-60-supplement-fe0ea75")
if TARGET.exists():
    raise SystemExit("refusing: " + str(TARGET) + " exists")
shutil.copytree(SOURCE / "registration", TARGET / "registration")
(TARGET / "state").mkdir(parents=True)
shutil.copyfile(SOURCE / "state" / "provider-mode.json", TARGET / "state" / "provider-mode.json")
events = [json.loads(line) for line in (SOURCE / "state" / "cells.jsonl").read_text(encoding="utf-8").splitlines()]
completed = {e["Cell"] for e in events if e["Event"] == "finished" and e["Row"]["Status"] == "completed"}
failed = sorted({e["Cell"] for e in events if e["Event"] == "finished" and e["Row"]["Status"] != "completed"})
kept = [e for e in events if e["Cell"] in completed]
with (TARGET / "state" / "cells.jsonl").open("w", encoding="utf-8") as out:
    for e in kept:
        out.write(json.dumps(e) + "\n")
print("completed kept", len(completed), "failed to rerun", failed)
