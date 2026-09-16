# AiDotNet.Evolution

AiDotNet.Evolution is a dependency-light, task-agnostic quality-diversity engine for .NET. It provides deterministic parallel evaluation, typed genome contracts, MAP-Elites archives, island migration, bounded diagnostics, trace output, and checkpoint/resume support.

The package does not depend on AiDotNet or AiDotNet.Tensors. Domain integrations—such as model search, program evolution, and hardware-kernel autotuning—supply typed genomes, validation, variation, and evaluation through the public contracts.

The initial package version is `0.1.0-preview.1` and targets .NET 10, .NET 8, and .NET Framework 4.7.1.

Optional [cost-aware operator portfolios](docs/OPERATOR_PORTFOLIOS.md) share admission
budgets across mutation, refinement and consumer-provided model strategies, with
typed outcome credit and checkpointed learning. The
[registered local comparison](benchmarks/analysis/PORTFOLIOS.md) requires evidence
against both a strong static strategy and a uniform mixture before default promotion.

## Core contracts

- `IEvolutionTask<TGenome>` owns canonical identity, validation, and evaluation.
- `IVariationOperator<TGenome>` proposes immutable typed genomes; stateful operators can additionally implement
  `ICheckpointableVariationOperator<TGenome>`.
- `ISelectionPolicy<TGenome>`, `ICandidateRefiner<TGenome>`, and `IMigrationPolicy<TGenome>` are explicit extension
  points rather than mode strings.
- `IEvolutionArchive<TGenome>` controls quality/diversity retention. `MapElitesArchive<TGenome>` is provided.
- `IEvolutionGenomeCodec<TGenome>` enables checkpoint/resume without imposing a serializer on genome types.
- Reference-typed genomes implement `IImmutableEvolutionGenome<TGenome>` and return a new, independently owned copy
  from `CreateOwnedSnapshot`; strings and value types with entirely value-based fields satisfy the boundary directly.
  A value type holding a reference must also implement the contract because copying the value alone does not copy its
  reachable state. Nested arrays, collections, and objects must be copied recursively. The engine takes this snapshot
  once at canonicalization, so archives, migration, and selection do not clone on their hot paths.

Evaluation caches inside the engine are run-local memoization keyed by canonical genome identity. Deployment caches
owned by consumers—such as a GPU kernel autotune cache—remain separate because their keys also need hardware, driver,
compiler, and correctness-policy fingerprints.

[Portable warm-start repertoires](docs/WARM_START_REPERTOIRES.md) export bounded canonical seeds with applicability
and prior-cost provenance. Import revalidates seeds under the current task and never imports old fitness;
persistent evaluation reuse is a separate contract, not checkpoint resume or a deployment-cache hit.

[Measurement origin](docs/MEASUREMENT_ORIGIN.md) preserves original sample identities, uncertainty and acquisition
cost through cache copies, migration, checkpoints and traces; fresh replication rejects declared reused samples.

[Persistent evaluation reuse](docs/PERSISTENT_EVALUATION_REUSE.md) adds exact-key storage, explicit freshness and
force-fresh policies, metered store calls and a runnable cold/warm/force-fresh engine example.

[Noise-aware confirmation](docs/evolution-stories/US-06.md) adds finite incumbent challenges,
audits of screen-rejected candidates, charged warmups and independent paired timing confirmation
for the Evolution-owned program benchmark. Correctness alone does not establish a performance win.
[Opt-in cheap screening](docs/SCREENING.md) advances candidates to full evaluation, audits a frozen
sample of rejects and charges every stage; local screening-on/off controls expose savings and overhead.

[Feature ablations](docs/evolution-stories/US-07.md) compare 19 search configurations with
paired quality, diversity, success and cost metrics, plus independent confirmation gates for presets.

## Integration boundaries

The engine intentionally knows nothing about models, prompts, compilers, or hardware. Integrations keep those domain
objects in their owning repository and implement the typed contracts above:

- Evolution-specific program/compiler, AutoML orchestration, and kernel-search integrations belong in this repository.
  General model, chat-client, and tensor/device primitives remain in AiDotNet and AiDotNet.Tensors.
- Optional `AiDotNet.Evolution.Programs` and `AiDotNet.Evolution.CSharp` projects target .NET 8 and .NET 10.
  They provide standalone program contracts, metered portfolios, correctness-gated fitness reuse and compiler-guided
  improvement without an AiDotNet dependency. See the [migration contract](docs/migration/PROGRAM_RUNTIME_MIGRATION.md).
  Other old consumer integrations are still being ported; the [PR audit](docs/migration/AIDOTNET_PR_CLEANUP.md)
  records what remains. Old AiModelBuilder evolution APIs will be removed in a separate breaking-removal PR,
  without obsolete forwarding APIs.
- Compiler schedule search, fusion-policy search, quantization-policy search, optimizer selection, feature selection,
  architecture search, and prompt/program search can share the engine without sharing domain-specific genomes.

Finite choices should be represented by enums or other validated value types inside a genome. Strings remain
appropriate for extensible identifiers, version hashes, metric names, and serialized payloads at an explicit boundary.

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md) for local validation and
[repository setup](.github/REPOSITORY_SETUP.md) for CI, security, and release configuration.

## License and provenance

The project is licensed under Apache License 2.0. The engine was developed in the AiDotNet repository and informed by the Apache-2.0-licensed OpenEvolve project. Original commit authorship is retained in this repository's filtered Git history; see `THIRD-PARTY-NOTICES.md` for upstream attribution.
