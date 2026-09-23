"""Resumable, position-balanced campaign runner over a frozen V1-05 registration (V1-06).

- ORDER. Arms share one subscription account, so caching and rate limits carry over from
  one run to the next; a fixed order once faked a 63% competitor result. Each (family,
  seed) block runs its arms in a Williams-design sequence (Latin squares balanced for
  first-order carryover), cycling through the design, and every run records its position.
- BUDGET. The projected calls and tokens are printed before launch, and launch refuses
  unless the operator passes that projection's digest back as confirmation.
- RESUME. Cell state is an fsynced journal. A killed campaign skips finished cells and
  re-enters the in-flight one with resume=True; the cell executor resumes the search itself
  (broker replay for our deterministic engine, OpenEvolve's own checkpoint for OpenEvolve).
- OUTPUT. Raw rows in exactly the shape campaign_registration.analyze consumes, plus
  position and throttle wait, which is reported apart from engine and evaluation time.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parents[1] / "analysis"))
from campaign_registration import OURS, load_registration  # noqa: E402
from design import digest  # noqa: E402


def williams(k):
    """Sequences in which every arm is in every position equally often, and (for the full
    design) every ordered pair of arms is adjacent equally often. Odd k needs 2k rows."""
    if type(k) is not int or k < 1:
        raise ValueError("Need at least one arm")
    first, low, high = [0], 1, k - 1
    for index in range(1, k):
        if index % 2:
            first.append(low)
            low += 1
        else:
            first.append(high)
            high -= 1
    rows = [[(value + shift) % k for value in first] for shift in range(k)]
    return rows if k % 2 == 0 else rows + [list(reversed(row)) for row in rows]


def arms_for(family):
    return [OURS] + [f"openevolve-{config}" for config in family["Configs"]]


def schedule(registration):
    cells, block = [], 0
    for family in registration["Spec"]["Families"]:
        arms = arms_for(family)
        design = williams(len(arms))
        for seed in registration["Seeds"]:
            sequence = design[block % len(design)]
            for position, arm_index in enumerate(sequence):
                arm = arms[arm_index]
                cells.append(dict(Cell=f"{family['Family']}/{arm}/{seed}", Family=family["Family"], Arm=arm,
                                  Seed=seed, Block=block, Position=position))
            block += 1
    return cells


def project(registration, mean_tokens_per_call=None):
    """Upper bounds from the registered budget; an expected token figure when a pilot supplies one."""
    budget, cells = registration["Spec"]["Budget"], schedule(registration)
    projection = dict(RegistrationSha256=registration["RegistrationSha256"], Cells=len(cells),
                      MaxModelCalls=len(cells) * budget["ModelCalls"], MaxModelTokens=len(cells) * budget["ModelTokens"],
                      MaxWallSeconds=len(cells) * budget["WallSeconds"],
                      ExpectedModelTokens=None if mean_tokens_per_call is None else
                      round(len(cells) * budget["ModelCalls"] * mean_tokens_per_call))
    projection["ProjectionSha256"] = digest(projection)
    return projection


def projection_error(projection, actual_tokens):
    """|expected - actual| / actual, for V1-06's <= 20% target on the pilot."""
    if projection["ExpectedModelTokens"] is None or actual_tokens <= 0:
        raise ValueError("Need an expected projection and a positive actual")
    return abs(projection["ExpectedModelTokens"] - actual_tokens) / actual_tokens


class CellJournal:
    def __init__(self, path):
        self.path = Path(path)
        self.started, self.finished = set(), {}
        if self.path.exists():
            lines = self.path.read_bytes().split(b"\n")
            for index, line in enumerate(lines):
                if not line:
                    continue
                try:
                    event = json.loads(line)
                except ValueError:
                    if any(lines[index + 1:]):
                        raise ValueError("Campaign journal is corrupt before its end") from None
                    continue  # a torn final line: the event was never durable
                (self.started.add(event["Cell"]) if event["Event"] == "started"
                 else self.finished.__setitem__(event["Cell"], event["Row"]))

    def write(self, event):
        with self.path.open("ab") as stream:
            stream.write((json.dumps(event, allow_nan=False) + "\n").encode("utf-8"))
            stream.flush()
            os.fsync(stream.fileno())


def run(registration_path, state, execute_cell, *, confirm, mean_tokens_per_call=None, log=print):
    """Run (or resume) every registered cell once. execute_cell(cell, directory, resume) -> dict
    with Status ('completed'|'failed'), Value, and optional ThrottleSeconds/ModelCalls/ModelTokens/LostCalls."""
    registration = load_registration(registration_path)
    projection = project(registration, mean_tokens_per_call)
    log(json.dumps(projection, indent=2))
    if confirm != projection["ProjectionSha256"]:
        raise ValueError("Launch refused: pass the printed ProjectionSha256 as confirmation")
    root = Path(state)
    root.mkdir(parents=True, exist_ok=True)
    journal = CellJournal(root / "cells.jsonl")
    for cell in schedule(registration):
        if cell["Cell"] in journal.finished:
            continue
        resume = cell["Cell"] in journal.started
        if not resume:
            journal.write(dict(Event="started", Cell=cell["Cell"]))
        directory = root / "cells" / cell["Cell"].replace("/", "__")
        directory.mkdir(parents=True, exist_ok=True)
        outcome = execute_cell(cell, directory, resume)
        if outcome.get("Status") not in ("completed", "failed"):
            raise ValueError(f"Cell {cell['Cell']} returned no terminal status")
        row = dict(Family=cell["Family"], Arm=cell["Arm"], Seed=cell["Seed"], Status=outcome["Status"],
                   Value=outcome.get("Value") if outcome["Status"] == "completed" else None,
                   Position=cell["Position"], Block=cell["Block"], Resumed=resume,
                   ThrottleSeconds=float(outcome.get("ThrottleSeconds", 0.0)),
                   ModelCalls=outcome.get("ModelCalls"), ModelTokens=outcome.get("ModelTokens"),
                   LostCalls=outcome.get("LostCalls", 0))
        journal.write(dict(Event="finished", Cell=cell["Cell"], Row=row))
        journal.finished[cell["Cell"]] = row
    rows = [journal.finished[cell["Cell"]] for cell in schedule(registration)]
    (root / "raw.json").write_text(json.dumps(rows, indent=1) + "\n", encoding="utf-8", newline="\n")
    return rows