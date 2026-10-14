# Meta-prompt evolution

The system instructions can evolve alongside the programs. `ProgramMetaPromptEvolver` keeps its own
archive of candidate instructions, each scored by the downstream quality of the programs written
under it, and proposes new ones from the ones that worked.

AlphaEvolve credits this component in its ablations. OpenEvolve 0.3.2 declares `use_meta_prompting`
and `meta_prompt_weight`, and its own config comment says the feature is not implemented, so there
is no upstream behaviour to match — only the paper's description.

```csharp
var evolver = new ProgramMetaPromptEvolver(chatClient, seedInstruction, new ProgramMetaPromptOptions
{
    Enabled = true,
    ProposeEveryGenerations = 25,
    Selection = ProgramInstructionSelection.EpsilonGreedy,
    ProposalCostUnits = 1m
});

// Per proposal: pick the instruction, use it as the system message, then credit it with what the
// program it produced measured.
string instruction = evolver.Select(context.Random, context.Generation);
...
evolver.Observe(context.Generation, evaluation.Quality ?? 0.0);

// Periodically, ask for a better instruction. The call is charged to the run's ledger.
if (evolver.ShouldPropose(generation)) await evolver.ProposeAsync(generation, ledger);
```

Nothing is contacted unless `Enabled` is set and `ProposeAsync` is called.

## Wiring it into a run

`LlmProgramVariationOptions.InstructionSource` takes an `IProgramInstructionSource`, which the
evolver implements. The operator asks it once per proposal, on the engine's seeded stream, and uses
the answer as that proposal's system message:

```csharp
var variation = new LlmProgramVariationOperator(chatClient, programOptions, new LlmProgramVariationOptions
{
    Mode = ProgramEvolutionMode.FullRewrite,
    InstructionSource = evolver            // null leaves SystemMessage in charge
});
```

The seam is the per-proposal prompt context (`ProgramPromptContext.SystemMessageOverride`), not the
operator's options: those are derived once when the operator is built, so an instruction set there
could never change during a run. A source that returns null for a proposal leaves the configured
`SystemMessage` in place for that proposal, so adding one changes nothing for existing callers.

The source's `VersionHash` joins the operator's identity, because an interface-typed option cannot
be serialised into it and two operators differing only by their instruction source must not claim
the same version.

**`ProgramInstructionCreditObserver` is the crediting path**, and writing your own is the exception. The operator supplies proposals and never sees outcomes,
so somebody has to call `Observe(generation, quality)` after each evaluation - and `Abandon` for a
proposal whose outcome will never arrive. Without that, instructions are proposed, used, and never
judged: the archive's scores stay empty and selection has nothing to prefer, which makes the feature
inert rather than broken.

Hand the same instance to the operator **and** to the engine, because it is both halves - the source
that records which generation got which instruction, and the observer that credits it when the
outcome arrives:

```csharp
var credit = new ProgramInstructionCreditObserver(evolver, ledger);   // ledger may be null
var variation = new LlmProgramVariationOperator(chatClient, programOptions, new LlmProgramVariationOptions
{
    Mode = ProgramEvolutionMode.FullRewrite,
    InstructionSource = credit
});

// The engine takes one observer, so compose when you also want a trace:
var engine = new EvolutionEngine<ProgramGenome>(task, variation, archiveFactory, options,
    observer: new CompositeEvolutionObserver<ProgramGenome>(
        new IEvolutionObserver<ProgramGenome>[] { tracer, credit }));
```

`CompositeEvolutionObserver<TGenome>` isolates one observer's failure from the other, matching the
engine's own policy: a trace problem must not cost a credit. It refuses the same observer twice,
because a repeated observer would double whatever it counts.

Two behaviours of the credit observer are worth knowing, because the alternatives look equivalent:

- It credits **only** generations it actually routed. The engine evaluates seed candidates that no
  proposal produced, and `Observe` refuses a generation it never routed, so "anything past generation
  zero" would throw on the seed's own evaluation.
- A **failed** evaluation earns no score. It releases its route and is counted in `FailedOutcomes`
  instead, because any stand-in value competes with the real ones and whichever is chosen can beat a
  measured outcome: `0.0` beats every positive cost when minimizing, where the evolver negates quality,
  and beats every negative quality when maximizing. A stream of credits is never revised, so an early
  stand-in stays in the mean for the rest of the run.

  Leaving it unscored is only safe because selection treats **untried**, not unscored, as unexplored: an
  instruction whose outcomes all failed is not drawn ahead of every instruction that works, and it is the
  first evicted when the archive is full. What it does **not** do is rank by failure rate - an instruction
  that fails nine times in ten is ranked on the one measurement it has. `FailedOutcomes` is reported
  because the archive cannot show it.

If a run proposes instructions and both `CreditedOutcomes` and `FailedOutcomes` stay zero, the wiring is
wrong rather than the feature ineffective - which is worth being able to tell apart.

## What scores an instruction

Only the programs it produced. `Observe` credits the instruction that wrote a proposal with that
proposal's measured quality, so `ProgramInstructionCandidate.MeanQuality` is the mean primary
quality of the evaluations routed to it. Nothing the model says about its own instructions enters
the score — that is the one signal a model asked to improve its own prompt cannot talk up.

An outcome for a generation the evolver did not route is refused rather than ignored, and a
generation already awaiting an outcome cannot be selected again: either would credit the wrong
instruction and silently invert the signal the archive is built on.

## Selection

`ProgramInstructionSelection` decides which instruction writes the next proposal:

| value | behaviour |
| --- | --- |
| `EpsilonGreedy` | the best mean quality, exploring with `ExplorationProbability` |
| `Greedy` | always the best mean quality — the control arm with no exploration |
| `Uniform` | uniformly at random — the control arm where credit is ignored |

An unscored instruction counts as unexplored and is tried before any scored one, whatever the
policy, so a freshly proposed instruction cannot be starved by an incumbent that leads early.

Selection draws from the stream you pass in, which is the engine's seeded stream, so a replay with
the same seed selects the same instructions in the same order. The evolver holds no private RNG.

## The archive

`Archive` is ordered best mean quality first, with unscored instructions last. Each
`ProgramInstructionCandidate` carries its `Id` (derived from the instruction text, so the same text
is the same candidate), its `ParentId`, the `Generation` it entered at, and its `Uses` and `Scored`
counts.

When the archive is full the worst-scoring scored instruction is dropped. The seed instruction is
never dropped, so a run can always fall back to what it started from, and an unscored instruction is
never dropped either — it has not had its chance yet.

An answer that is not usable as an instruction is refused and counted in `RefusedProposals`: longer
than `MaxInstructionChars` (a model asked for instructions sometimes answers with a program), too
short to be instructions, or text already in the archive. Re-adding existing text would split one
instruction's score across two entries and make both look worse than the instruction is.

## Cost

Each instruction proposal is one model call. Supply the run's `EvolutionResourceLedger` and it is
charged there, as a `Proposal`-stage operation, so a budget that cannot pay refuses the call before
the model is contacted. The meta level is a second consumer of the same budget, so it is also
reported as its own line: `ProposalCalls`, `ProposalCostUnits`, `RefusedProposals`, and
`WriteCostDescriptors` which writes those under the `MetricPrefix` into an evaluation's descriptors.
A refusal still costs a call and is still reported, because a run paying for answers it cannot use
should be able to see that rather than infer it from a flat archive.

## Checkpoints

**Through the CLI, a resume starts the instruction archive over.** The engine checkpoints the
variation operator's state, not its instruction source, so `aidotnet-evolve resume` rebuilds the
evolver from `metaPrompt.seedInstruction` and loses the archive, its scores, its counters and any
outstanding routes. The evolver's own `CaptureState`/`RestoreState` work and are tested - what is
missing is their place in the engine's checkpoint, which is a change to that contract rather than to
the CLI. Use the API directly if a resumed run must keep what its instructions learned.

`CaptureState` carries the archive, the lineage, the counters and the outstanding routes, so a
resumed run can still credit an instruction for a proposal that was in flight. `StateHash` hashes
all of it for checkpoint equality.

`RestoreState` refuses a state captured under different settings, from a different seed instruction,
with a newer schema, with no archive, or with more instructions or routes than this evolver holds.
Restoring any of those would replay a selection sequence the archive never produced.

## What is not measured

The paper's reason for this component is its ablation. A pre-registered dev-partition ablation —
meta-prompt evolution on against off, paired seeds, reporting the effect on the primary endpoint and
the tokens to target whichever way it goes — needs real model spend and is not in this repository.
Until it exists, this is a faithful implementation of a component whose benefit here is unmeasured,
and `Enabled` defaults to off.
