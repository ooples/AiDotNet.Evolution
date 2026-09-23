# aidotnet-evolve

`aidotnet-evolve` is a `dotnet tool` (package `AiDotNet.Evolution.Cli`) that runs and resumes program evolutions,
and inspects, compares, exports and reports them from their trace files.

```text
aidotnet-evolve run     <run.json>
aidotnet-evolve resume  <run.json>
aidotnet-evolve inspect <trace>
aidotnet-evolve compare <traceA> <traceB>
aidotnet-evolve export  <trace> <output-directory>
aidotnet-evolve report  <trace> <output.html>
```

Exit codes: `0` success; `2` a usage or input error (the message is on standard error); `3` a run in which every
model call failed, so only the seed was scored (the summary is still printed); `130` a run aborted by a second Ctrl+C.

## Run file

The inputs match OpenEvolve's: an initial program, an evaluator, and a model. Relative paths resolve against the
run file's directory. Unknown fields are refused, and enums are written as names, never as integers.

```json
{
  "schema": "aidotnet-evolve-run-v1",
  "runId": "circle-packing",
  "initialProgram": "initial.py",
  "evaluator": "evaluator.py",
  "model": {
    "endpoint": "https://api.openai.com/v1",
    "name": "gpt-4.1",
    "apiKeyEnvironmentVariable": "OPENAI_API_KEY",
    "temperature": 0.7,
    "maxOutputTokens": 8192,
    "timeoutSeconds": 300
  },
  "budget": {
    "maxEvaluations": 100,
    "seed": 0,
    "parallelism": 4,
    "evaluationTimeLimitSeconds": 60,
    "maxProgramChars": 65536
  },
  "output": "runs/circle-packing",
  "language": "Python",
  "evaluatorLanguage": "Python",
  "direction": "Maximize",
  "taskDescription": "Pack 26 circles in a unit square to maximise the sum of radii.",
  "runtimeVersion": "aidotnet-evolve-local"
}
```

| Field | Required | Meaning |
|---|---|---|
| `schema` | yes | Must be `aidotnet-evolve-run-v1`. |
| `runId` | yes | Checkpoint identity. `resume` continues the run with this id. |
| `initialProgram` | yes | The seed program. It is evaluated first and needs no model call. |
| `evaluator` | yes | A script that reads the candidate source on **standard input** and prints one JSON object with a numeric `quality`, and optionally `metrics`, `descriptors` and `artifacts`. It must contain the entry-point marker `evaluate`. |
| `model.endpoint` | yes | An OpenAI-compatible base URL; requests go to `<endpoint>/chat/completions`. Plain `http` is accepted only for loopback addresses. |
| `model.name` | yes | The model name sent in each request. |
| `model.apiKeyEnvironmentVariable` | no | The environment variable holding a bearer key. Omit it for a local endpoint that needs none. The key never appears in the run file. |
| `budget.maxEvaluations` | yes | The total number of evaluations, seed included. `resume` continues toward the same total, so raise it in the run file to extend a finished run. |
| `budget.parallelism` | no (1) | Concurrent evaluations. It is also the proposal batch size, so a graceful stop takes effect within one round. |
| `output` | yes | Holds `checkpoints/`, one `trace-NNN.jsonl` per run or resume session, and `best.<ext>`. |
| `direction` | no (`Maximize`) | `Maximize` or `Minimize` the evaluator's `quality`. |
| `runtimeVersion` | no | The interpreter image identity. It is part of the checkpoint compatibility hash, so it must not change between `run` and `resume`. Change it when the interpreter or its packages change, and the old checkpoint is then refused rather than silently mixed. |

A minimal evaluator:

```python
import json, sys

def evaluate(source):
    namespace = {}
    exec(source, namespace)
    return {"quality": float(namespace["score"]())}

print(json.dumps(evaluate(sys.stdin.read())))
```

The evaluator and candidates run as child processes under the evaluation time limit. A child process alone
does **not** isolate filesystem or network access. Run untrusted candidates inside a container or VM that you
provision (see [execution migration](migration/EXECUTION_RUNTIME_MIGRATION.md)).

## Run, interrupt, resume

- `run` refuses an `output` that already holds a checkpoint for `runId`. Runs never overwrite.
- `resume` refuses when there is no checkpoint. It restores the archive, counters and lineage, and re-evaluates
  nothing that was already committed.
- The first Ctrl+C lets the current batch commit, writes the checkpoint, and reports with `StopReason`
  `Canceled`. No model call is wasted.
- A second Ctrl+C aborts. The in-flight batch is rolled back, the final checkpoint is still written, and the exit
  code is `130`. The rolled-back proposals are made again on `resume`, so an abort can cost up to one batch of
  model calls.
- Each session prints a JSON summary with provider-reported token usage (`ModelUsage`). Usage is what the provider
  reported. It is not a monetary charge.

Each session writes its own trace, starting at `trace-000.jsonl`. `inspect`, `report` and `export` read one
session's trace.
