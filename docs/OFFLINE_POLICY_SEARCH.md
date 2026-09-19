# US-26: bounded offline policy search

`EvolutionPolicyOptimizer` searches a finite catalogue of immutable `EvolutionSearchPolicy` recipes. It is opt-in research infrastructure, not self-modifying engine code and not automatic production activation.

## User stories and acceptance contracts

As an optimization researcher, I want to search engine policies without evolving executable infrastructure.

- Given a predeclared `EvolutionPolicySpace`, when development search samples or mutates a recipe, then only registered operator ratios, selection schedules, restart intervals and context contracts can change.
- Given proportional operator weights, when identities are computed, then equivalent ratios are one policy, not extra statistical trials.
- Given a recipe, when `EvolutionPolicyEngineTrial.Create` executes it, then fresh pinned task/operator instances run through `EvolutionEngine`, with metered proposals/evaluations and limits shared across restarts.
- Given a restricted context, when an operator receives a parent or inspiration, then archive access and alternate artifact/diagnostic paths are removed. This is a metadata contract, not a sandbox against malicious executable backends or information encoded in genomes.

As a benchmark owner, I want development gains tested independently before recommending a policy.

- Given predeclared, disjoint development and held-out families, when development ends, then its champion is frozen before any held-out callback executes.
- Given at least two registered manual baselines, when confirmation runs, then every baseline gets the same task/seed blocks and inner limits. Task utilities use fixed protocol-owned normalization; families receive equal weight.
- Given repeated seeds within a task family, when significance is computed, then the independent units are families, not seeds. A one-sided exact sign test excludes ties and uses a Bonferroni threshold across registered baselines. Positive practical mean gain and bounded within-task variability must also pass against every baseline.
- Given no development improvement, failed confirmation or inconclusive evidence, when the report is returned, then `SuggestedPolicy` remains the registered stable preset. Nothing activates it automatically.

As an operator, I want unsuccessful research to remain bounded and auditable.

- Given declared campaign and trial limits, when work is admitted, then maximum resources are reserved first. Actual costs, failed trials, duplicate outer proposals, engine counters and conservative unknown charges are retained.
- Given timeout or a provider that ignores cancellation, when the wait limit expires, then the campaign stops, reserves are conservatively charged, no replacement trial launches, and late quality cannot change the report. `HasOutstandingWork` exposes unfinished work; it does not authorize another campaign.
- Given an invalid receipt, unit mismatch, reused measurement, overrun or unstable candidate, when validated, then no candidate is recommended. Fatal runtime exceptions propagate; `LastReport` is retained when possible.
- Given historical baseline tuning, when costs are reported, then supplied prior costs remain separate from new campaign charges, avoiding double charging. Inner elapsed time and residual outer wall time are reported, not inferred CPU usage or money.
- Given large inline evidence, when the aggregate limit is exceeded, then the campaign stops after reconciling that receipt. Its final rejected receipt can exceed the configured raw evidence limit by at most 128 KiB; JSON escaping and object overhead are additional memory.

## Integration

1. Register immutable genomes, codecs, descriptor axes and fixed evaluators. Choose normalization bounds before observing held-out outcomes.
2. Register `EvolutionPolicyEngineOperator<T>` factories returning `ICostedEvolutionProposalSource<T>`. Factories must create isolated instances and cannot do unmetered external setup work. The adapter owns `IDisposable` results.
3. Create independent family trials with `EvolutionPolicyEngineTrial.Create`; task/operator IDs and version hashes must match their factory products.
4. Supply a bounded policy catalogue, at least two manual baselines with tuning evidence hashes/rationales/costs, and a stable preset drawn from those baselines.
5. Freeze `EvolutionPolicyOptimizationOptions`; call `RunAsync` once and retain `report.ToJson()`, referenced external evidence, exact source revision and environment.

The adapter deliberately refuses cascaded tasks: coordinating stage limits needs a separate driver. Cache reuse is disabled, core parallelism is one, restarts create independent resource ledgers from the remaining allowance, and all segment totals are summed. Reusing one ledger across fresh engines would collide with reset operation IDs. Failed setup/cleanup without a trustworthy final receipt is charged at the entire admitted trial maximum.

Resource names are caller-declared units. Producers must enforce their maxima externally. Cooperative cancellation cannot terminate arbitrary model servers, child processes, GPU kernels or malicious code. Coordinate admission across campaigns and implement process/container containment before using such backends. No API credentials or paid calls are required by this feature.

## What the statistics do not prove

Family labels cannot establish semantic independence. The benchmark owner must curate genuinely independent task/data families, avoid repeated peeking, justify normalization, retain failed campaigns and preregister strong manually tuned baselines. Merely supplying a baseline evidence hash proves neither the artifact exists nor the baseline is competitive. Using this API repeatedly on the same holdout invalidates its advertised inference unless a separate sequential-testing protocol accounts for reuse.

The sign test concerns direction across independent family gains; it is not a confidence interval on effect magnitude. The mean-gain gate is a practical threshold, not a separate significance claim. Two families cannot establish significance at the default corrected threshold regardless of how many seeds are run. Test fixtures with synthetic scores validate these contracts, not algorithm superiority.

## CPU demonstration

```powershell
dotnet run --project examples/PolicySearch -c Release -- TestResults/policy-search/report.json
```

The output path must not already exist. The example runs the real metered engine over a 36-policy catalogue and numerical objectives. Development and held-out function names are separated, but these small mathematical landscapes are not asserted to be statistically independent real-world families. Two explicitly hand-specified controls cover local exploitation and restarted diversity; no prior tuning campaign is claimed (historical charges are zero). All runs have an objective-call/proposal work-unit protocol, exact seeds, recipes, state hashes and raw segment evidence. A non-winning outcome is valid and retained. CI runs this demonstration and uploads its report.

This is an executable integration example, **not evidence that AiDotNet.Evolution exceeds OpenEvolve or that these controls are the strongest competitor baselines**. Those claims require the full independently curated benchmark protocol and measured companion-story results.
