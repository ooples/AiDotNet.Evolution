"""Record the isolated-context canary baseline for each campaign model.

One subscription call per model. A campaign passes the recorded count as
`ClaudeTransport(canary_baseline=...)` and refuses to run if it moves, because any
injected context (CLAUDE.md, memory, hooks, MCP, tools, a proxy's rewriting) changes it.
"""
import argparse
import json
from pathlib import Path
import tempfile

from claude_transport import CANARY_PROMPT, PINNED_CLI, SYSTEM_PROMPT, ClaudeTransport

MAX_READINGS = 5


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--models", nargs="+", default=["haiku", "sonnet", "opus"])
    args = parser.parse_args()
    rows = {}
    with tempfile.TemporaryDirectory() as directory:
        for model in args.models:
            # The CLI refreshes shared remote config after each call, so the first call after
            # a gap can run on a stale system prompt (observed: 421 once, then 448 x5). A
            # baseline is only recorded once two consecutive readings agree.
            transport = ClaudeTransport(args.executable, model, Path(directory) / model, MAX_READINGS)
            readings = [transport.canary_input_tokens()]
            while len(readings) < 2 or readings[-1] != readings[-2]:
                if len(readings) == MAX_READINGS:
                    raise SystemExit(f"{model} canary never stabilised: {readings}")
                readings.append(transport.canary_input_tokens())
            rows[model] = dict(resolved_model=transport.resolved_model, canary_input_tokens=readings[-1],
                               readings=readings)
    record = dict(cli=PINNED_CLI, canary_prompt=CANARY_PROMPT, system_prompt=SYSTEM_PROMPT, models=rows)
    Path(args.output).write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(record, indent=2))


if __name__ == "__main__":
    main()
