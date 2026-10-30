# V1-53 (#166): meta-prompt evolution, paired dev-partition ablation

Does evolving the system instruction move the primary endpoint? This driver answers it by measurement,
for criterion 4 of #166. Criteria 1-3 are implemented and covered by `MetaPromptEvolutionTests`.

## Why this is a C# driver and not a run file

**Meta-prompt evolution is not reachable from the CLI.** `RunCommand` has no run-file field for it, so no
`aidotnet-evolve run` can switch it on. Measuring through the CLI would have meant adding that field first;
this driver exercises the shipped API directly and keeps the two pieces of work separate.

It also needed a bridge that does not ship. `ProgramMetaPromptEvolver` supplies instructions through
`IProgramInstructionSource`, which the variation operator consumes, but nothing in the engine tells it what
the programs achieved — the interface's own documentation says "whoever owns the run tells the source what
the programs achieved". `MetaPromptCredit` is that owner: it decorates the source to record which
generations were routed, credits each from the engine's `Evaluated` events, and asks for a new instruction
on the evolver's own `ShouldPropose` cadence.

Two decisions in it are worth stating, because the alternatives look equivalent and are not:

- **Routed generations are recorded, not inferred.** `Observe` refuses a generation it never routed, and the
  engine evaluates seed candidates that no proposal produced. Inferring "anything past generation zero was
  routed" is correct today and wrong the moment a warm start or a cache hit changes which generations reach
  the operator.
- **A failed evaluation is credited as the worst outcome, not skipped.** Skipping would let an instruction
  whose programs never parse keep a clean record.

## Design

Each engine seed runs once per arm, and the arms differ only in whether the variation operator is given an
evolver as its instruction source — same task, same budget, same seed, same model. `Fixed` pins the seed
instruction literally; `Evolving` may replace it.

The endpoint is **not** the evaluator's score. The evaluator shows six input/output pairs of a hidden rule
and the endpoint is accuracy on 55 held-out inputs it never sees, scored afterwards by `task/score_heldout.py`
in a separate process. A program can reach a perfect visible score either by recovering the rule or by
storing the six answers, and only the held-out set separates those.

## Running it

```powershell
dotnet build benchmarks/EvolutionMetaPrompt/EvolutionMetaPrompt.csproj -c Release
./benchmarks/EvolutionMetaPrompt/bin/Release/net10.0/EvolutionMetaPrompt.exe `
  benchmarks/EvolutionMetaPrompt/task out.json 6 5 python claude sonnet --allow-unsandboxed
```

`--allow-unsandboxed` is required: the evaluator and the held-out scorer run model-written programs as host processes,
with this machine's files and network, so run it on a machine provisioned for that.

Real model calls through the ClaudeCode provider; there is no stub option, because a stub answers both arms
identically and a run against one would measure the stub. Cost is set by the call count rather than prompt
size — each isolated `claude -p` pays a fresh ~31.6k-token cache creation — so about $0.13 and 9 s per call.

Verified with a stub before spending anything: the `Evolving` arm records instruction proposals, credited
outcomes and meta cost units while `Fixed` records zeros, and recording the prompts shows the `Evolving`
arm's proposer receiving two distinct system instructions against `Fixed`'s one.
