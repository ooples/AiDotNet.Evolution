"""V1-03 shim metrics: latency the loopback shim adds, and prompts it failed to record.

An instant model sits behind the real broker, so each client-timed round trip is exactly
what the shim adds to a call: HTTP framing, capability check, translation, admission and
the OpenAI-shaped response. The broker caps a run at 64 model calls, so samples are spread
over several broker instances.
"""
import argparse
import gzip
import json
import math
from pathlib import Path
import subprocess
import http.client
import time

from program_broker import ProgramBroker, request
from program_controls import candidate_hash


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(fraction * len(ordered)) - 1)]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--brokers", type=int, default=10)
    parser.add_argument("--calls", type=int, default=60)
    parser.add_argument("--output-root", default=str(Path(__file__).parents[1] / "evidence" / "openevolve-shim"))
    args = parser.parse_args()
    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    initial = "def solve(x):\n    return x\n"
    generate = lambda system, messages, model=None: {"text": "```python\n" + initial + "```", "cost_units": 1,
                                                     "cost_metric": "reported_input_plus_cache_plus_output_tokens"}
    evaluate = lambda code: dict(candidate_hash=candidate_hash(code), status="valid", quality=1.0, work_units=0, unknown_work=False)
    body = json.dumps({"model": "haiku", "temperature": 0.7, "max_tokens": 4096,
                       "messages": [{"role": "system", "content": "S" * 4000}, {"role": "user", "content": "U" * 12000}]}).encode()
    samples, received, recorded = [], 0, 0
    for _ in range(args.brokers):
        with ProgramBroker(generate, evaluate, model_calls=args.calls, evaluations=args.calls + 1, seconds=600,
                           initial=initial) as broker:
            request(broker.endpoint, broker.capability, "evaluate", {"code": initial})
            # One persistent connection, as OpenEvolve's pooled client holds; its first
            # sample includes the connect.
            connection = http.client.HTTPConnection("127.0.0.1", int(broker.endpoint.rsplit(":", 1)[1]), timeout=10)
            headers = {"Authorization": "Bearer " + broker.capability, "Content-Type": "application/json"}
            for _ in range(args.calls):
                started = time.perf_counter()
                connection.request("POST", "/v1/chat/completions", body, headers)
                reply = json.loads(connection.getresponse().read())
                samples.append(time.perf_counter() - started)
                if reply["choices"][0]["message"]["content"] is None:
                    raise ValueError("Empty shim reply")
            connection.close()
            received += broker.chat_received
            recorded += sum(row["operation"] == "model" and row["status"] == "completed" for row in broker.rows)
    summary = dict(story="V1-03 #110", commit=commit, samples=len(samples), request_bytes=len(body),
                   shim_ms_p50=percentile(samples, 0.5) * 1000, shim_ms_p95=percentile(samples, 0.95) * 1000,
                   shim_ms_max=max(samples) * 1000, connection="persistent (HTTP/1.1 keep-alive), connect included once per broker", chat_received=received, recorded=recorded,
                   unrecorded_prompts=received - recorded, targets=dict(shim_ms_p95="< 5", unrecorded_prompts=0))
    summary["passed"] = summary["shim_ms_p95"] < 5 and summary["unrecorded_prompts"] == 0
    output = Path(args.output_root) / commit
    output.mkdir(parents=True, exist_ok=True)
    (output / "shim-latency.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    with gzip.open(output / "shim-latency-raw.json.gz", "wt", encoding="utf-8") as raw:
        json.dump(samples, raw)
    print(json.dumps(summary, indent=2))
    raise SystemExit(0 if summary["passed"] else 1)


if __name__ == "__main__":
    main()