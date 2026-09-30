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


def first(directory, patterns):
    for pattern in patterns:
        found = sorted(p for p in directory.glob(pattern) if p.is_file())
        if found:
            return found[0]
    return None


def find_files(example):
    """The example's task: its own files, or else the first subdirectory that holds one.

    OpenEvolve takes the initial program, evaluator and config as arguments, so examples name them freely
    (initial_program.py, init_program.py, initial_prompt.txt; evaluator.py, evaluator_stub.py; config.yaml,
    config.yml). The config is optional there: without one, OpenEvolve runs on its defaults, and so does this.
    """
    for directory in [example] + sorted(p for p in example.rglob("*") if p.is_dir()):
        initial = first(directory, ["initial_program.*", "init_program.*", "initial_*.*"])
        evaluator = first(directory, ["evaluator.py", "evaluator*.py"])
        if initial is not None and evaluator is not None:
            config = first(directory, ["config.yaml", "config.yml", "config*.yaml", "config*.yml", "*config*.yaml"])
            return config, initial, evaluator
    return None, None, None


def working_directory(config_text, example, upstream):
    """The directory the example's own command runs from.

    OpenEvolve resolves a relative template_dir against the working directory, and examples disagree on which one
    they expect: lm_eval runs from the repository root ("examples/lm_eval/prompts"), llm_prompt_optimization from its
    own folder ("templates"). The directory in which the configured template_dir exists is the one its command uses.
    """
    match = re.search(r"(?m)^\s*template_dir:\s*[\"']?([^\"'\n#]+?)[\"']?\s*(?:#.*)?$", config_text)
    if match and not Path(match.group(1)).is_absolute():
        for candidate in (upstream, example):
            if (candidate / match.group(1)).is_dir():
                return candidate
    return upstream


def first_diagnostic(output):
    for trace in sorted(output.glob("trace-*.jsonl")):
        for line in trace.read_text(encoding="utf-8", errors="replace").splitlines():
            record = json.loads(line) if line.strip().startswith("{") else {}
            for diagnostic in record.get("diagnostics") or []:
                return (diagnostic.get("code", "") + ": " + diagnostic.get("message", ""))[:300]
    return "no evaluation completed and no diagnostic was recorded"


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
        if initial is None or evaluator is None:
            rows.append(dict(example=example.name, status="no-task",
                             reason="no directory holds both an initial program and an evaluator"))
            print(json.dumps(rows[-1]), flush=True)
            continue
        SEED_PROGRAM["text"] = initial.read_text(encoding="utf-8", errors="replace")
        # The stand-in endpoint replaces the example's api_base, as OPENAI_API_BASE would, by rewriting a copy.
        work = Path(tempfile.mkdtemp(prefix="oe-smoke-" + example.name + "-"))
        text = config.read_text(encoding="utf-8", errors="replace") if config is not None else ""
        text = re.sub(r"(?m)^(\s*api_base:).*$", r"\1 " + endpoint, text)
        text = re.sub(r"(?m)^(\s*-?\s*name:)\s*[\"']?[^\"'\n]+[\"']?\s*$", r"\1 stand-in", text)
        # Every provider is pointed at the stand-in, so an example written for claude_code runs without the CLI.
        text = re.sub(r"(?m)^(\s*-?\s*provider:).*$", r"\1 openai", text)
        copied = work / (config.name if config is not None else "config.yaml")
        copied.write_text(text, encoding="utf-8")
        run = subprocess.run(
            ["dotnet", args.cli, "run", "--openevolve-config", str(copied), str(initial), str(evaluator),
             "--iterations", str(args.iterations), "--output", str(work / "out"), "--python", args.python],
            # OpenEvolve's examples are run from the repository root (openevolve-run.py examples/<name>/...), and some
            # configs name paths relative to it, such as lm_eval's template_dir.
            capture_output=True, text=True, timeout=1800, env=env, cwd=str(working_directory(text, example, Path(args.upstream))))
        if run.returncode == 0:
            result = json.loads(run.stdout)
            completed = result.get("CompletedEvaluations") or 0
            if completed > 0:
                rows.append(dict(example=example.name, status="passed", completed=completed,
                                 best=result.get("BestQuality")))
            else:
                # The run finished but no evaluation completed: the evaluator failed on every program, usually for a
                # missing dependency or data file. Report the evaluator's own diagnostic.
                rows.append(dict(example=example.name, status="evaluator", reason=first_diagnostic(work / "out"),
                                 completed=0))
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
