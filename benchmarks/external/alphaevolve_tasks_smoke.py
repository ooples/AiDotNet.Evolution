"""E-4 (#347): run every generated AlphaEvolve task through both engines against a stand-in model. No spend.

    python alphaevolve_tasks_smoke.py --cli <aidotnet-evolve.dll> --upstream <openevolve checkout> --output <json>

The stand-in answers every call with one fixed edit per problem whose construction beats the seed
(`improved`, a short seeded descent from the seed). A row passes when the engine scored the seed AND
reported that program's score as its best, which can only happen if a proposal travelled from the
model through the engine into the task's evaluator and back. It proves the wiring, never a result.
"""
from __future__ import annotations

import argparse
import http.server
import json
import os
import pathlib
import random
import re
import subprocess
import sys
import tempfile
import threading

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import alphaevolve_headroom as headroom  # noqa: E402
import alphaevolve_tasks as tasks  # noqa: E402

ANSWER = {"text": ""}


class StandInModel(http.server.BaseHTTPRequestHandler):
    """An OpenAI-compatible chat endpoint that answers with the current task's improved program."""

    def do_POST(self):  # noqa: N802 - http.server's naming
        self.rfile.read(int(self.headers.get("Content-Length", "0")))
        body = json.dumps({
            "model": "stand-in",
            "choices": [{"message": {"role": "assistant", "content": ANSWER["text"]}}],
            "usage": {"prompt_tokens": 1, "completion_tokens": 1},
        }).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


def improved(problem, iterations=400, seed=7):
    """A construction better than the seed: greedy coordinate steps from it, kept only when feasible."""
    spec = headroom.IN_SCOPE[problem]
    wins = headroom.better(spec["direction"])
    rng = random.Random(seed)
    heights = list(eval(tasks.SEEDS[problem]))  # noqa: S307 - the module's own constant
    best = spec["objective"](heights)
    for _ in range(iterations):
        trial = list(heights)
        i, j = rng.randrange(len(trial)), rng.randrange(len(trial))
        step = rng.uniform(0.0, 0.2)
        # Moving mass from one entry to another keeps c5's sum at n / 2 exactly.
        trial[i] += step
        trial[j] -= step
        value = spec["objective"](trial)
        if value is not None and wins(value, best):
            heights, best = trial, value
    return heights


def answer(heights):
    """A SEARCH/REPLACE edit anchored on the signature, which every candidate keeps.

    The tasks evolve by diff, as OpenEvolve does by default, so both engines refuse a whole-program
    fence; anchoring on the seed's body instead would apply to the seed only.
    """
    body = "    return [" + ", ".join(repr(float(h)) for h in heights) + "]"
    return "\n".join(["<<<<<<< SEARCH", "def construct():", "=======", "def construct():", body, ">>>>>>> REPLACE"])


def configured(text, endpoint):
    text = re.sub(r"(?m)^(\s*-\s*name:).*$", r"\1 stand-in", text)
    return text.replace("llm:\n", "llm:\n  api_base: " + endpoint + "\n", 1)


def ours(cli, directory, work, python, endpoint, iterations):
    config = work / "config.yaml"
    config.write_text(configured((directory / "config.yaml").read_text(encoding="utf-8"), endpoint), encoding="utf-8")
    run = subprocess.run(
        ["dotnet", cli, "run", "--openevolve-config", str(config), str(directory / "initial_program.py"),
         str(directory / "evaluator.py"), "--iterations", str(iterations), "--output", str(work / "ours"),
         "--python", python, "--allow-unsandboxed-execution"],
        capture_output=True, text=True, timeout=1800, env=dict(os.environ, OPENAI_API_KEY="stand-in"))
    if run.returncode != 0:
        return {"status": "failed", "reason": (run.stderr.strip() or run.stdout.strip()).splitlines()[-1:]}
    result = json.loads(run.stdout)
    outcome = {"status": "ran", "completed": result.get("CompletedEvaluations"), "best": result.get("BestQuality")}
    if not outcome["completed"]:
        outcome["reason"] = first_diagnostic(work / "ours")
    return outcome


def first_diagnostic(output):
    """The first evaluator diagnostic in our run's traces, so a row that scored nothing says why."""
    for trace in sorted(output.glob("trace-*.jsonl")):
        for line in trace.read_text(encoding="utf-8", errors="replace").splitlines():
            record = json.loads(line) if line.strip().startswith("{") else {}
            for diagnostic in record.get("diagnostics") or []:
                return (diagnostic.get("code", "") + ": " + diagnostic.get("message", ""))[:300]
    return "no evaluation completed and no diagnostic was recorded"


def upstream(checkout, directory, work, python, endpoint, iterations):
    config = work / "openevolve.yaml"
    config.write_text(configured((directory / "config.yaml").read_text(encoding="utf-8"), endpoint), encoding="utf-8")
    run = subprocess.run(
        [python, str(pathlib.Path(checkout) / "openevolve-run.py"), str(directory / "initial_program.py"),
         str(directory / "evaluator.py"), "--config", str(config), "--iterations", str(iterations),
         "--output", str(work / "openevolve")],
        capture_output=True, text=True, timeout=1800, env=dict(os.environ, OPENAI_API_KEY="stand-in"))
    best = work / "openevolve" / "best" / "best_program_info.json"
    if run.returncode != 0 or not best.is_file():
        return {"status": "failed", "reason": (run.stderr.strip() or run.stdout.strip()).splitlines()[-1:]}
    metrics = json.loads(best.read_text(encoding="utf-8")).get("metrics", {})
    return {"status": "ran", "best": metrics.get("combined_score")}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--cli", required=True)
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--python", default=sys.executable)
    parser.add_argument("--iterations", type=int, default=3)
    arguments = parser.parse_args(argv)
    # Absolute, because each engine runs the evaluator from its own working directory. abspath and not
    # resolve: resolving a venv's interpreter follows its symlink out of the venv and loses its packages.
    arguments.python = os.path.abspath(arguments.python)
    arguments.cli = os.path.abspath(arguments.cli)
    arguments.upstream = os.path.abspath(arguments.upstream)

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), StandInModel)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    endpoint = "http://127.0.0.1:%d/v1" % server.server_address[1]
    root = pathlib.Path(tempfile.mkdtemp(prefix="alphaevolve-smoke-"))
    rows = []
    try:
        for directory in tasks.write(root / "tasks"):
            problem = directory.name
            heights = improved(problem)
            ANSWER["text"] = answer(heights)
            seed_score = tasks.score(problem, eval(tasks.SEEDS[problem]))["combined_score"]  # noqa: S307
            expected = tasks.score(problem, heights)["combined_score"]
            row = {"problem": problem, "seed": seed_score, "expected": expected}
            for arm, run in (("ours", ours), ("openevolve", upstream)):
                work = root / arm / problem
                work.mkdir(parents=True)
                try:
                    outcome = run(arguments.cli if arm == "ours" else arguments.upstream, directory, work,
                                  arguments.python, endpoint, arguments.iterations)
                except subprocess.TimeoutExpired as expired:
                    # A hung engine fails its row; the other rows and the results file still get written.
                    outcome = {"status": "failed", "reason": "timed out after %s s" % expired.timeout}
                outcome["passed"] = (outcome["status"] == "ran" and outcome.get("best") is not None
                                     and abs(outcome["best"] - expected) <= 1e-9 * abs(expected))
                row[arm] = outcome
            rows.append(row)
            print(json.dumps(row), flush=True)
    finally:
        server.shutdown()
    pathlib.Path(arguments.output).write_text(json.dumps({"rows": rows}, indent=1) + "\n", encoding="utf-8")
    passed = sum(row["ours"]["passed"] and row["openevolve"]["passed"] for row in rows)
    print("passed %d of %d" % (passed, len(rows)))
    return 0 if passed == len(rows) else 1


if __name__ == "__main__":
    sys.exit(main())
