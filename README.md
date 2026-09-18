<h1 align="center">AiDotNet.Evolution</h1>

<p align="center">
  <strong>Evolutionary search for .NET that keeps the receipts &mdash;
  deterministic, budgeted, and resumable.</strong>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/AiDotNet.Evolution"><img src="https://img.shields.io/nuget/vpre/AiDotNet.Evolution?logo=nuget" alt="NuGet"></a>
  <a href="https://github.com/ooples/AiDotNet.Evolution/actions/workflows/build.yml"><img src="https://github.com/ooples/AiDotNet.Evolution/actions/workflows/build.yml/badge.svg?branch=main" alt="Build and Test"></a>
  <a href="./LICENSE"><img src="https://img.shields.io/badge/license-Apache--2.0-blue.svg" alt="Apache 2.0"></a>
  <img src="https://img.shields.io/badge/.NET-10%20%7C%208%20%7C%20Framework%204.7.1-512BD4?logo=dotnet&logoColor=white" alt=".NET 10, .NET 8, .NET Framework 4.7.1">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/third--party%20deps-none-2ea043" alt="No third-party dependencies">
  <img src="https://img.shields.io/badge/LLM-optional-8b5cf6" alt="LLM optional">
  <img src="https://img.shields.io/badge/replay-byte--identical-3b82f6" alt="Deterministic replay">
  <img src="https://img.shields.io/badge/telemetry-none-2ea043" alt="No telemetry">
  <img src="https://img.shields.io/badge/12-runnable%20examples-0d9488" alt="12 runnable examples">
</p>

---

You have something you can score &mdash; a model config, a GPU kernel, a compiler
schedule, a prompt, a piece of generated code &mdash; and too many possible
versions of it to try by hand. **This searches that space for you, in .NET.**

It is a quality-diversity engine, so it returns a map of the best candidate of
each *kind* rather than a thousand variations of one local optimum. It is a
library: no service, no account, no telemetry.

## Quick start

```bash
dotnet add package AiDotNet.Evolution --prerelease
```

```csharp
using AiDotNet.Evolution;

// 1. Describe the space you want to search.
EvolutionSearchSpace space = new EvolutionSearchSpaceBuilder()
    .Add(EvolutionParameter.Integer("depth", 1, 20))
    .Add(EvolutionParameter.Logarithmic("rate", 0.0001, 1.0))
    .Build();

// 2. Say how good a candidate is. Anything you can score works here.
var task = new EvolutionSearchTask(space, "quickstart", "v1", "score-v1", (genome, _, _) =>
    new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(
        quality: -Math.Abs(genome.Number("depth") - 7) - Math.Abs(Math.Log10(genome.Number("rate") / 0.01)),
        descriptors: new Dictionary<string, double> { ["depth"] = genome.Number("depth") },
        costUnits: 1)));

// 3. Run the search.
var engine = new EvolutionEngine<EvolutionSearchGenome>(
    task,
    EvolutionSearchPresets.CreateAdaptiveMixed(space),
    _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("depth", 1, 21, 10) }),
    new EvolutionEngineOptions { RunId = "quickstart", Seed = 42, MaxEvaluationAttempts = 200 });

EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(
    new[] { space.Sample(StableRandom.CreateStream(42, 0)) });

Console.WriteLine($"best quality : {result.Best!.Evaluation.Quality:F4}");
Console.WriteLine($"depth        : {result.Best.Candidate.CanonicalGenome.Genome.Number("depth")}");
```

```text
best quality : -0.0111
depth        : 7
```

Three objects: a **space**, a **task** that scores a candidate, and an
**engine**. Everything below is optional.

> The three ids on `EvolutionSearchTask` &mdash; task id, task version,
> evaluator version &mdash; are how the engine refuses a checkpoint or a cached
> score produced by a *different* scoring function. Change how you score, bump
> the evaluator version, and stale results stop being reused.

## What it gives you

- **Deterministic runs.** Same seed, same result, and every run emits a
  `StateHash` you can compare. One example runs a search across three processes,
  kills them mid-flight, and asserts the resumed report is byte-identical to the
  uninterrupted one.
- **Budgets you cannot overrun.** Every evaluation is admitted against a
  resource ledger before it runs and charged after. A worker that dies holding a
  reservation leaves a retained liability, not a silent refund.
- **Real checkpoint/resume.** Archives, operator learning, island topology,
  random streams and the ledger restore together; an incompatible checkpoint is
  refused rather than reinterpreted.
- **LLMs optional.** The engine knows nothing about models or prompts. Supply
  model-driven mutation through a typed contract and it is metered like any
  other operator, or leave it out and pay nothing per evaluation.
- **Runs where you already are.** .NET 10, .NET 8, and .NET Framework 4.7.1.
  No third-party dependencies (`System.Text.Json` only, and only on 4.7.1 where
  it is not in-box).

## What people search with it

| You want to tune | Your genome is | Your score is |
| --- | --- | --- |
| Model hyperparameters | depth, learning rate, family | held-out metric |
| A GPU kernel or compiler schedule | tile sizes, unroll factors, fusion | measured runtime |
| Generated code or programs | the program itself | tests passed, then speed |
| A prompt or agent policy | template and parameter choices | task success rate |
| Feature subsets | which columns are in | cross-validated score |
| Deployment configs | replicas, batch size, cache sizes | cost under an SLA |

The engine knows nothing about any of these. You implement
`IEvolutionTask<TGenome>` and it stays out of your domain.

## Twelve examples you can run

```bash
dotnet run --project examples/TypedParameterSearch -c Release
```

| Example | What it shows |
| --- | --- |
| [TypedParameterSearch](examples/TypedParameterSearch) | Mixed integer/real/categorical space with conditional parameters |
| [ParetoSearch](examples/ParetoSearch) | Competing objectives, keeping the tradeoff front instead of one winner |
| [AdaptiveIslandSearch](examples/AdaptiveIslandSearch) | Parallel islands, migration, restarting stalled populations |
| [SurrogateSearch](examples/SurrogateSearch) | A cheap learned model ranks candidates before you pay for real evaluation |
| [MultiFidelitySearch](examples/MultiFidelitySearch) | Cheap-first screening with successive halving and promotion |
| [ReplicatedEvaluation](examples/ReplicatedEvaluation) | Noisy scores, repeated sampling, confirmation before believing a win |
| [PersistentEvaluation](examples/PersistentEvaluation) | Reusing earlier evaluations across runs, with freshness and force-fresh |
| [OperatorCreditSearch](examples/OperatorCreditSearch) | Learning which mutation strategies earn their cost |
| [ProposalPipeline](examples/ProposalPipeline) | Overlapping proposal generation with evaluation, bounded in flight |
| [CompilerGuidedSearch](examples/CompilerGuidedSearch) | Using compiler feedback to steer program improvement |
| [PolicySearch](examples/PolicySearch) | Searching over search policies themselves, with a held-out panel |
| [DurableWork](examples/DurableWork) | External workers, crash recovery, and exactly-once receipts |

## Measured, not asserted

Results that were not measured are labeled as not measured. The repository ships
retained evidence &mdash; including failed and discarded campaigns &mdash; each
with an offline verifier you can run without trusting us.

| Evidence | Scale |
| --- | --- |
| [Pareto search](benchmarks/evidence/pareto) | 180 matched-budget runs, reproduced from the recorded state hashes in CI |
| [Proposal pipeline](benchmarks/evidence/pipeline) | 576 live executions and 576 offline replays; 13,824 proposal calls, 18,432 evaluations |
| [Surrogate ranking](benchmarks/evidence/surrogates) | 240 runs, 27,248 pilot calls |
| [Adaptive islands](benchmarks/evidence/adaptive-islands) | 62,720 local objective calls |
| [Engine performance](benchmarks/evidence/performance) | Isolated per-core measurement, with the failed campaigns retained |

The external harness runs SciPy differential evolution and upstream pyribs
CMA-ME against **the same C# evaluator** &mdash; no Python reimplementation of
the objective to tilt the result. See the
[comparison contract](benchmarks/external/COMPARISON.md) and run it yourself.

`main` enforces a coverage ratchet (≥89.8% line, ≥74.51% branch), builds all
three target frameworks, and runs 1,400+ tests with zero skips. A pull request
that skips a test fails the build rather than reporting green.

## Going further

| Capability | Doc |
| --- | --- |
| Mixed, conditional and log-scaled spaces | [Typed search spaces](docs/TYPED_SEARCH_SPACES.md) |
| Competing objectives | [Pareto search](docs/PARETO_SEARCH.md) |
| Budgets and liabilities | [Resource accounting](docs/RESOURCE_ACCOUNTING.md) |
| Noisy scores and confirmation | [Replicated evaluation](docs/REPLICATED_EVALUATION.md) |
| Cheap screening first | [Screening](docs/SCREENING.md) · [Multi-fidelity](docs/MULTI_FIDELITY.md) |
| Learned candidate ranking | [Surrogate selection](docs/SURROGATE_SELECTION.md) |
| Which operators earn their keep | [Operator credit](docs/OPERATOR_CREDIT.md) · [Portfolios](docs/OPERATOR_PORTFOLIOS.md) |
| Parallel islands and restarts | [Adaptive islands](docs/ADAPTIVE_ISLANDS.md) |
| Overlapping proposal and evaluation | [Proposal pipeline](docs/PROPOSAL_PIPELINE.md) |
| Reusing past evaluations safely | [Persistent reuse](docs/PERSISTENT_EVALUATION_REUSE.md) · [Warm starts](docs/WARM_START_REPERTOIRES.md) |
| Where a measurement came from | [Measurement origin](docs/MEASUREMENT_ORIGIN.md) |
| High-dimensional archives | [Centroid archives](docs/CENTROID_ARCHIVES.md) |

### Contracts you implement

`IEvolutionTask<TGenome>` (identity, validation, evaluation) ·
`IVariationOperator<TGenome>` (proposes immutable genomes) ·
`IEvolutionArchive<TGenome>` (what to keep; `MapElitesArchive<TGenome>` ships) ·
`IEvolutionGenomeCodec<TGenome>` (checkpointing without imposing a serializer) ·
`ISelectionPolicy<TGenome>`, `ICandidateRefiner<TGenome>`, `IMigrationPolicy<TGenome>`.

Reference-typed genomes implement `IImmutableEvolutionGenome<TGenome>` and return
an independently owned copy from `CreateOwnedSnapshot`. The engine takes that
snapshot once at canonicalization, so archives, migration and selection never
clone on their hot paths.

### Related packages

`AiDotNet.Evolution.Programs` and `AiDotNet.Evolution.CSharp` add standalone
program contracts, process execution, script metrics, model judging and novelty
screening. The core package depends on neither AiDotNet nor AiDotNet.Tensors.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for local validation, and
[repository setup](.github/REPOSITORY_SETUP.md) for CI, security and release
configuration.

## License

Apache License 2.0. This is an independent .NET implementation with its own
engine, orchestration, persistence and integration contracts; no OpenEvolve
runtime code is copied or ported. OpenEvolve was reviewed as prior art during
design, and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) records that review
under its Apache-2.0 terms.
