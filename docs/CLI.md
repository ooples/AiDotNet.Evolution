# aidotnet-evolve

`aidotnet-evolve` is a `dotnet tool` (package `AiDotNet.Evolution.Cli`) that runs and resumes program evolutions,
and inspects, compares, exports and reports them from their trace files.

```text
aidotnet-evolve run     <run.json>
aidotnet-evolve resume  <run.json>
aidotnet-evolve preflight <run.json>
aidotnet-evolve inspect <trace>
aidotnet-evolve compare <traceA> <traceB>
aidotnet-evolve export  <trace> <output-directory> [--include-source <program-file>]
aidotnet-evolve inspect-export <export-directory>
aidotnet-evolve report  <trace> <output.html>
aidotnet-evolve watch   <trace> <output.html> [--interval <seconds>]
```

Exit codes: `0` success; `2` a usage or input error (the message is on standard error); `3` a run in which every
model call failed, so only the seed was scored (the summary is still printed); `4` `preflight` found that the seed
fails the evaluator; `130` a run aborted by a second Ctrl+C, or by a Ctrl+C before the run started.

## Run file

The inputs match OpenEvolve's: an initial program, an evaluator, and a model. Relative paths resolve against the
run file's directory. Unknown fields are refused, and enums are written as names, never as integers.

```json
{
  "schema": "aidotnet-evolve-run-v1",
  "runId": "circle-packing",
  "container": { "image": "python:3.12-slim", "lifecycle": "Warm" },
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
| `model.provider` | no (`OpenAiCompatible`) | `OpenAiCompatible`, `ClaudeCode` (the `claude` CLI and its login; run isolated from your profile) or `Manual` (a person answers each prompt through `model.manualQueue`). |
| `model.endpoint` | for `OpenAiCompatible` | An OpenAI-compatible base URL; requests go to `<endpoint>/chat/completions`. Plain `http` is accepted only for loopback addresses. |
| `model.name` | yes | The model name sent in each request. |
| `model.apiKeyEnvironmentVariable` | no | The environment variable holding a bearer key. Omit it for a local endpoint that needs none. The key never appears in the run file. |
| `model.topP` / `model.reasoningEffort` | no | Nucleus sampling in (0, 1], and `Low`, `Medium` or `High` for models that support reasoning effort. |
| `model.maxRetries` / `model.retryDelaySeconds` | no (3 / 5) | Retries for throttled (429), failed (5xx) or timed-out calls; the delay doubles each retry. Other errors are not retried. |
| `model.maxBudgetUsd` / `model.claudeExecutable` | no | `ClaudeCode` only: a per-call spending cap passed as `--max-budget-usd`, and the executable (`claude` on PATH by default). |
| `model.manualQueue` / `model.manualTimeoutSeconds` | for `Manual` | The directory where `NNNNNN.prompt.json` is written and `NNNNNN.response.txt` is read (write it under another name, then rename), and how long to wait for each answer (default 3600). |
| `budget.maxEvaluations` | yes | The total number of evaluations, seed included. `resume` continues toward the same total, so raise it in the run file to extend a finished run. |
| `budget.parallelism` | no (1) | Concurrent evaluations. It is also the proposal batch size, so a graceful stop takes effect within one round. Keep it unchanged between `run` and `resume`: the batch size is part of the checkpoint's compatibility, so a different value is refused on resume. |
| `output` | yes | Holds `checkpoints/`, one `trace-NNN.jsonl` per run or resume session, and `best.<ext>`. |
| `direction` | no (`Maximize`) | `Maximize` or `Minimize` the evaluator's `quality`. |
| `warmStart` | no | A `repertoire-NNN.json` written by an earlier run. Its programs join the seeds and are evaluated afresh; see [Warm start](#warm-start). `run` uses it and `resume` ignores it. |
| `container.image` | one of `container` or `allowUnsandboxedExecution` | Runs the evaluator, and the programs it executes, in a locked-down Docker container from this image (no network, read-only root, non-root user, no capabilities, default seccomp profile, memory, CPU and process caps). The image must already be pulled and must hold the evaluator's interpreter and every package it imports. Nothing from the host is mounted, except with `openEvolveEvaluator`: then the evaluator's directory is mounted read-only at `/aidotnet/evaluator`, so the shim can import it and the helpers beside it, and each `openEvolveEvaluator.pythonPath` directory is mounted read-only at `/aidotnet/pythonpath/N` and put on `PYTHONPATH`. Candidates can read those directories, so keep anything they must not see out of them, and make them readable by the container's user (`nobody`). `openEvolveEvaluator.python` is refused with a container, whose image's `python3` runs the evaluator. |
| `container.lifecycle` | no (`Warm`) | `Warm` reuses one container per concurrent evaluation, with every process killed and the workdir emptied between evaluations; `Cold` starts and removes a container per evaluation. |
| `allowUnsandboxedExecution` | one of `container` or `allowUnsandboxedExecution` | `true` runs the evaluator as a host process instead. That bounds time and output but not what the code can touch: it runs as you, with your files, credentials and network. A run file with neither is refused. `run --openevolve-config` writes `true`, because OpenEvolve runs evaluators the same way, and says so in a note. See [Choosing an execution mode](PROGRAM_EVOLUTION.md#choosing-an-execution-mode). |
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

## Warm start

Every session also writes `repertoire-NNN.json` beside its trace. It holds the archive's elites, best first (at most
256), and cites the session's trace by SHA-256. It is written without any evaluator or model call. A later run
names it in `warmStart` (see [Portable warm-start repertoires](WARM_START_REPERTOIRES.md)).

- The file is checksummed. A tampered or oversized file is refused with exit code `2`, before any model call.
- Import requires the same task and genome schema. A changed evaluator is allowed, because every imported program is
  evaluated afresh under the new run's evaluator. No quality, descriptor or measurement is carried over.
- The summary's `WarmStart` object reports the source run and how many programs were accepted, duplicated or rejected.
  It also gives `PriorCostUnits` and `CostUnit`: what building the repertoire cost the earlier run. That cost is
  reported beside this session's own spend. It is never charged again or folded into it.
