# AiDotNet.Evolution

AiDotNet.Evolution is a dependency-light, task-agnostic quality-diversity engine for .NET. It provides deterministic parallel evaluation, typed genome contracts, MAP-Elites archives, island migration, bounded diagnostics, trace output, and checkpoint/resume support.

The package does not depend on AiDotNet or AiDotNet.Tensors. Domain integrations—such as model search, program evolution, and hardware-kernel autotuning—supply typed genomes, validation, variation, and evaluation through the public contracts.

The initial package version is `0.1.0-preview.1` and targets .NET 10, .NET 8, and .NET Framework 4.7.1.

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

## Integration boundaries

The engine intentionally knows nothing about models, prompts, compilers, or hardware. Integrations keep those domain
objects in their owning repository and implement the typed contracts above:

- AiDotNet uses it for MAP-Elites AutoML and program evolution. Facade builders, model materialization, LLM clients,
  prompt templates, and sandboxed execution stay in AiDotNet.
- AiDotNet.Tensors uses it for offline, startup, and background kernel search. Device benchmarks, correctness oracles,
  launch configurations, hardware fingerprints, and deployment caches stay in AiDotNet.Tensors.
- Compiler schedule search, fusion-policy search, quantization-policy search, optimizer selection, feature selection,
  architecture search, and prompt/program search can share the engine without sharing domain-specific genomes.

Finite choices should be represented by enums or other validated value types inside a genome. Strings remain
appropriate for extensible identifiers, version hashes, metric names, and serialized payloads at an explicit boundary.

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md) for local validation and
[repository setup](.github/REPOSITORY_SETUP.md) for CI, security, and release configuration.

## License and provenance

The project is licensed under Apache License 2.0. The engine was developed in the AiDotNet repository and informed by the Apache-2.0-licensed OpenEvolve project. Original commit authorship is retained in this repository's filtered Git history; see `THIRD-PARTY-NOTICES.md` for upstream attribution.
