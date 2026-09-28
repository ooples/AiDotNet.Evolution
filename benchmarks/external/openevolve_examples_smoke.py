"""V1-56 (#169): run every OpenEvolve 0.3.2 example through `aidotnet-evolve run --openevolve-config`, unmodified.

Each example runs for 3 iterations against a local stand-in model that answers with the example's own initial program,
so the smoke exercises import, the evaluator shim, the sandbox and the run loop rather than model quality. An example
passes when the run completes and its seed program is scored. A failure is classified with the reason recorded:
  refused      the import refused a key (the report names it)
  dependency   the evaluator imports a module this environment lacks (the module is named)
  evaluator    the evaluator itself failed (first line of its error)
  no-config    the example has no top-level config.yaml to import
Usage: python openevolve_examples_smoke.py --upstream <openevolve checkout> --cli <aidotnet-evolve.dll> --output <json>
"""
import argparse
import http.server
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
from pathlib import Path

SEED_PROGRAM = {"text": ""}


class StandInModel(http.server.BaseHTTPRequestHandler):
    """An OpenAI-compatible chat endpoint that answers with the current example's initial program."""

    def do_POST(self):  # noqa: N802 - http.server's naming
        length = int(self.headers.get("Content-Length", "0"))
        self.rfile.read(length)
        body = json.dumps({
            "model": "stand-in",
            "choices": [{"message": {"role": "assistant", "content": "```python\n" + SEED_PROGRAM["text"] + "\n```"}}],
            "usage": {"prompt_tokens": 1, "completion_tokens": 1},
        }).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


def find_files(example):
    """The example's task: its own files, or else the first subdirectory that holds a complete one."""
    for directory in [example] + sorted(p for p in example.rglob("*") if p.is_dir()):
        configs = sorted(directory.glob("config*.yaml")) or sorted(directory.glob("*config*.yaml"))
        initial = next(iter(sorted(directory.glob("initial_program.*"))), None)
        evaluator = directory / "evaluator.py"
        if configs and initial is not None and evaluator.exists():
            return configs[0], initial, evaluator
    return None, None, None


def classify(stdout, stderr):
    text = stdout + "\n" + stderr
    if "cannot be imported" in text:
        refused = [line.strip() for line in text.splitlines() if ": not supported" in line or "not an OpenEvolve" in line]
        return "refused", "; ".join(refused) or "import refused"
    missing = re.search(r"No module named '([^']+)'", text)
    if missing:
        return "dependency", "needs " + missing.group(1)
    for line in text.splitlines():
        if line.startswith("error:"):
            return "evaluator", line[len("error:"):].strip()[:300]
    return "evaluator", (text.strip().splitlines() or ["unknown failure"])[-1][:300]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--cli", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--python", default=sys.executable)
    parser.add_argument("--iterations", type=int, default=3)
    args = parser.parse_args()

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), StandInModel)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    endpoint = f"http://127.0.0.1:{server.server_address[1]}/v1"
    env = dict(os.environ, OPENAI_API_KEY="stand-in", OPENAI_API_BASE=endpoint)

    rows = []
    for example in sorted(p for p in (Path(args.upstream) / "examples").iterdir() if p.is_dir()):
        config, initial, evaluator = find_files(example)
        if config is None or initial is None or evaluator is None:
            rows.append(dict(example=example.name, status="no-config", reason="no directory holds a config.yaml, initial_program and evaluator.py"))
            continue
        SEED_PROGRAM["text"] = initial.read_text(encoding="utf-8", errors="replace")
        # The stand-in endpoint replaces the example's api_base, as OPENAI_API_BASE would, by rewriting a copy.
        work = Path(tempfile.mkdtemp(prefix="oe-smoke-" + example.name + "-"))
        text = config.read_text(encoding="utf-8", errors="replace")
        text = re.sub(r"(?m)^(\s*api_base:).*$", r"\1 " + endpoint, text)
        text = re.sub(r"(?m)^(\s*-?\s*name:)\s*[\"']?[^\"'\n]+[\"']?\s*$", r"\1 stand-in", text)
        copied = work / config.name
        copied.write_text(text, encoding="utf-8")
        run = subprocess.run(
            ["dotnet", args.cli, "run", "--openevolve-config", str(copied), str(initial), str(evaluator),
             "--iterations", str(args.iterations), "--output", str(work / "out"), "--python", args.python],
            capture_output=True, text=True, timeout=1800, env=env, cwd=str(initial.parent))
        if run.returncode == 0:
            result = json.loads(run.stdout)
            rows.append(dict(example=example.name, status="passed", completed=result.get("CompletedEvaluations"),
                             best=result.get("BestQuality")))
        else:
            status, reason = classify(run.stdout, run.stderr)
            rows.append(dict(example=example.name, status=status, reason=reason, exit=run.returncode))
        print(json.dumps(rows[-1]), flush=True)
        Path(args.output).write_text(json.dumps(dict(examples=rows), indent=1) + "\n", encoding="utf-8")
    server.shutdown()
    passed = sum(row["status"] == "passed" for row in rows)
    print(f"passed {passed} of {len(rows)}")


if __name__ == "__main__":
    main()
