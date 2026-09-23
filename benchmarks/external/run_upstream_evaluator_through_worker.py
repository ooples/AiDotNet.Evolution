"""Runs an unmodified OpenEvolve example evaluator through the aidotnet_evolution reference worker (V1-25).

The example's evaluator.py and initial_program.py are checked blob-for-blob against the pinned OpenEvolve
commit before anything runs, OpenEvolve itself is importable exactly as an installed copy would be (the
evaluators import openevolve.evaluation_result), and the real durable host settles every lease.

usage: run_upstream_evaluator_through_worker.py <openevolve-checkout> <example> <host-dll> <new-output.json>
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "bindings/python"))
from aidotnet_evolution import DurableWorkClient, load_openevolve_evaluator, serve  # noqa: E402

PINNED = "411fb59c886c18704caaffb611e17cf9e7d824d2"
BROKEN = "def search_algorithm(:\n"  # a syntax error: the evaluator must report it, the worker must commit it


def git(checkout: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(checkout), *args], check=True, capture_output=True, text=True).stdout.strip()


def main(argv: list[str]) -> int:
    if len(argv) != 5:
        print(__doc__, file=sys.stderr)
        return 2
    checkout, example, dll, output = Path(argv[1]).resolve(), argv[2], argv[3], Path(argv[4])
    if output.exists():
        print(f"{output} exists; evidence is never overwritten", file=sys.stderr)
        return 2
    if git(checkout, "rev-parse", "HEAD") != PINNED:
        print("the OpenEvolve checkout is not at the pinned commit " + PINNED, file=sys.stderr)
        return 2
    files = {}
    for name in ("evaluator.py", "initial_program.py"):
        relative = f"examples/{example}/{name}"
        on_disk, pinned = git(checkout, "hash-object", relative), git(checkout, "rev-parse", f"{PINNED}:{relative}")
        if on_disk != pinned:
            print(f"{relative} differs from the pinned blob ({on_disk} != {pinned}); it must run unmodified", file=sys.stderr)
            return 2
        files[relative] = pinned
    sys.path.insert(0, str(checkout))  # openevolve importable, as `pip install openevolve` would make it

    evaluate = load_openevolve_evaluator(checkout / "examples" / example / "evaluator.py")
    initial = (checkout / "examples" / example / "initial_program.py").read_text(encoding="utf-8")
    with tempfile.TemporaryDirectory(prefix="upstream-evaluator-") as scratch:
        config = {"directory": os.path.join(scratch, "work"), "runId": "upstream-" + example,
                  "compatibilityHash": f"openevolve-{PINNED[:12]}-{example}", "limits": {"evaluations": "10"}}
        worker = {"workerId": "reference-1", "compatibilityHash": config["compatibilityHash"]}
        with DurableWorkClient(config, host_path="dotnet", host_args=[dll]) as work:
            for index, source in enumerate([initial, BROKEN], 1):
                work.enqueue({"evaluationId": str(index), "attempt": 1, "canonicalGenomeId": f"program:{index}",
                              "payload": source, "estimated": {"evaluations": "1"}, "maximum": {"evaluations": "1"}})
            dispositions = serve(work, worker, evaluate, provenance=f"openevolve-{example}-evaluator", actual={"evaluations": "1"})
            receipts = {index: work.result(str(index), 1) for index in (1, 2)}
            status = work.status()
    evidence = {
        "schema": "aidotnet-upstream-evaluator-through-worker-v1",
        "openevolve_commit": PINNED,
        "example": example,
        "unmodified_blobs": files,
        "python": sys.version.split()[0],
        "platform": platform.platform(),
        "dispositions": dispositions,
        "receipts": {str(index): {"outcome": receipt["outcome"], "payload": json.loads(receipt["payload"])}
                     for index, receipt in receipts.items()},
        "host_status": status,
        "limitations": "One unmodified initial program and one broken program; the example's stochastic metrics vary run to run. "
                       "The upstream evaluator scores a broken program 0 with an error artifact instead of raising, so that "
                       "receipt is completed, as in OpenEvolve. Only evaluate(program_path) is served; cascade stages are "
                       "not invoked. The home directory is redacted to ~ in tracebacks.",
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(evidence, indent=2, sort_keys=True) + "\n"
    home = str(Path.home())
    text = text.replace(json.dumps(home)[1:-1], "~").replace(home, "~")  # tracebacks carry local paths
    with open(output, "x", encoding="utf-8", newline="\n") as handle:
        handle.write(text)
    print(json.dumps({"dispositions": dispositions, "outcomes": [receipts[1]["outcome"], receipts[2]["outcome"]]}))
    return 0 if dispositions == ["accepted", "accepted"] else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
