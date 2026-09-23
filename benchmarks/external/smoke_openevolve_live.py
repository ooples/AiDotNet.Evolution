"""Live V1-03 smoke: pinned OpenEvolve's native OpenAI client -> broker shim -> real Claude.

Consumes subscription allowance. The evaluator is the contract fixture (every program is
valid), so this proves the model path end to end, not any family result; per-family smokes
belong to V1-04, where the oracles live.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

from claude_transport import ClaudeModelSet, reconcile_receipts
import openevolve_configs
from program_broker import ProgramBroker
from program_controls import candidate_hash


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", required=True)
    parser.add_argument("--upstream", required=True)
    parser.add_argument("--iterations", type=int, default=2)
    parser.add_argument("--output-root", default=str(Path(__file__).parents[1] / "evidence" / "openevolve-shim"))
    args = parser.parse_args()
    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    baselines = {name: row["canary_input_tokens"] for name, row in
                 json.loads(Path(__file__).with_name("claude_canary.json").read_text(encoding="utf-8"))["models"].items()}
    initial = "def solve(x):\n    return x + 1\n"
    records = {"recommended": openevolve_configs.recommended(
                   Path(args.upstream) / "examples/algotune/fft_convolution/config.yaml", iterations=args.iterations, seed=37),
               "matched": openevolve_configs.matched({"haiku": 0.5, "sonnet": 0.3, "opus": 0.2},
                   "Improve the Python function solve(x). Return the complete program.", iterations=args.iterations, seed=37)}
    output = Path(args.output_root) / commit
    output.mkdir(parents=True, exist_ok=True)
    summary = dict(story="V1-03 #110", commit=commit, evaluator="contract fixture (not a family oracle)", runs={})
    for kind, record in records.items():
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            models = ClaudeModelSet(args.executable, record["models"], root / "transport", args.iterations + 4)
            # Warm each model (stale remote config on a cold first call), then hold it to its baseline.
            for name, transport in models.transports.items():
                transport.canary_input_tokens()
                transport.canary_baseline = baselines[name]
            canaries = models.verify_canaries()
            evaluate = lambda code: dict(candidate_hash=candidate_hash(code), status="valid", quality=1.0,
                                         work_units=1, unknown_work=False)
            (root / "initial.py").write_text(initial, encoding="utf-8")
            (root / "task.txt").write_text("unused in native modes", encoding="utf-8")
            (root / "record.json").write_text(json.dumps(record), encoding="utf-8")
            with ProgramBroker(models.generate, evaluate, model_calls=args.iterations, evaluations=args.iterations + 1,
                               seconds=1800, initial=initial, model_tokens=2_000_000) as broker:
                environment = dict(os.environ, EVOLUTION_BROKER_ENDPOINT=broker.endpoint,
                                   EVOLUTION_BROKER_CAPABILITY=broker.capability)
                for key in ("OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL"):
                    environment.pop(key, None)
                child = subprocess.run([sys.executable, "-X", "utf8", str(Path(__file__).with_name("openevolve_adapter.py")),
                                        "--upstream", args.upstream, "--initial", str(root / "initial.py"),
                                        "--output", str(root / "result"), "--model", "unused",
                                        "--iterations", str(args.iterations), "--seed", "37", "--task", str(root / "task.txt"),
                                        "--mode", kind, "--config-record", str(root / "record.json")],
                                       env=environment, capture_output=True, timeout=1800)
            rows = [row for row in broker.rows if row["operation"] == "model"]
            receipts = {name: reconcile_receipts(root / "transport" / name) for name in models.transports}
            run = dict(exit_code=child.returncode, stderr_tail=child.stderr.decode(errors="replace")[-1500:],
                       broker_closed=broker.closed, model_rows=len(rows), chat_received=broker.chat_received,
                       unrecorded_prompts=broker.chat_received - len(rows), models_called=[row["request"]["model"] for row in rows],
                       statuses=[row["status"] for row in broker.rows], canaries=canaries,
                       receipt_coverage={name: record_["coverage"] for name, record_ in receipts.items()},
                       tokens=broker.model_tokens, changes=record["changes"], differences=record.get("differences", []))
            run["passed"] = (child.returncode == 0 and not broker.closed and len(rows) == args.iterations and
                             run["unrecorded_prompts"] == 0 and all(s == "completed" for s in run["statuses"]) and
                             all(c == 1.0 for c in run["receipt_coverage"].values()))
            summary["runs"][kind] = run
            (output / f"live-{kind}-broker-rows.json").write_text(json.dumps(broker.rows, indent=1), encoding="utf-8")
    summary["passed"] = all(run["passed"] for run in summary["runs"].values())
    (output / "live-smoke.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({kind: {k: v for k, v in run.items() if k in ("exit_code", "model_rows", "unrecorded_prompts",
                     "models_called", "canaries", "receipt_coverage", "tokens", "passed")} for kind, run in summary["runs"].items()}, indent=1))
    raise SystemExit(0 if summary["passed"] else 1)


if __name__ == "__main__":
    main()