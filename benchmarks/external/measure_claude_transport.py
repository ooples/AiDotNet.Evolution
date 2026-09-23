"""Measure V1-02's transport metrics from real subscription calls.

Runs N canary calls through one ClaudeTransport, then reports, from the receipts alone:
receipt coverage (every admitted call has a receipt), the canary input-token delta against
the recorded baseline, and transport overhead (elapsed minus the CLI process's own
lifetime) as a fraction of each call's latency. One warm-up call is made first and kept in
the raw receipts, because the first call after a gap can run on stale remote config.
"""
import argparse
import gzip
import json
import math
from pathlib import Path
import subprocess
import tempfile

from claude_transport import PINNED_CLI, ClaudeTransport, reconcile_receipts


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(fraction * len(ordered)) - 1)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", required=True)
    parser.add_argument("--model", default="haiku")
    parser.add_argument("--calls", type=int, default=20)
    parser.add_argument("--baselines", default=str(Path(__file__).with_name("claude_canary.json")))
    parser.add_argument("--output-root", default=str(Path(__file__).parents[1] / "evidence" / "claude-transport"))
    args = parser.parse_args()
    baseline = json.loads(Path(args.baselines).read_text(encoding="utf-8"))["models"][args.model]["canary_input_tokens"]
    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    with tempfile.TemporaryDirectory() as directory:
        evidence = Path(directory) / "calls"
        transport = ClaudeTransport(args.executable, args.model, evidence, args.calls + 1)
        warmup = transport.canary_input_tokens()
        transport.canary_baseline = baseline
        readings = [transport.canary_input_tokens() for _ in range(args.calls)]
        record = reconcile_receipts(evidence)
    measured = record["rows"][1:]
    fractions = [row["transport_overhead_seconds"] / row["elapsed_seconds"] for row in measured]
    overheads = [row["transport_overhead_seconds"] for row in measured]
    summary = dict(
        story="V1-02 #109", commit=commit, cli=PINNED_CLI, model=args.model, resolved_model=transport.resolved_model,
        calls=record["calls"], warmup_reading=warmup, baseline=baseline, readings=readings,
        canary_delta_max=max(abs(reading - baseline) for reading in readings),
        receipt_coverage=record["coverage"], missing_receipts=record["missing"],
        api_key_calls=sum(row["api_key_calls"] for row in record["rows"]),
        overhead_fraction_p50=percentile(fractions, 0.5), overhead_fraction_p95=percentile(fractions, 0.95),
        overhead_seconds_p95=percentile(overheads, 0.95),
        latency_seconds_p50=percentile([row["elapsed_seconds"] for row in measured], 0.5),
        targets=dict(canary_delta_max=0, receipt_coverage=1.0, overhead_fraction_p95="< 0.01"))
    summary["passed"] = (summary["canary_delta_max"] == 0 and summary["receipt_coverage"] == 1.0 and
                         summary["overhead_fraction_p95"] < 0.01 and summary["api_key_calls"] == 0)
    output = Path(args.output_root) / commit
    output.mkdir(parents=True, exist_ok=False)
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    with gzip.open(output / "raw.json.gz", "wt", encoding="utf-8") as raw:
        json.dump(record["rows"], raw, indent=1)
    print(json.dumps(summary, indent=2))
    raise SystemExit(0 if summary["passed"] else 1)


if __name__ == "__main__":
    main()