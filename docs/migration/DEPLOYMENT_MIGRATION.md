# AiDotNet PR2202: standalone deployment migration

Source: `d4535f7376888a7c2d35d7e6229494c4c60c0ac6`.
Replacement: non-draft [Evolution PR89](https://github.com/ooples/AiDotNet.Evolution/pull/89),
based on PR88. AiDotNet PR2202 is closed as relocated; source branch retained.
Destination: optional `AiDotNet.Evolution.Deployment` project, net8.0/net10.0.
All deployment orchestration, MAP-Elites search, and its parameter sampler live
in AiDotNet.Evolution. AiDotNet 0.231.0 is pinned and assembly-aliased solely for
ordinary model/training/serialization primitives. Core/Programs/CSharp remain
independent of that optional dependency.

| Original PR change | Replacement |
|---|---|
| Nine deployment source files | Deployment project: exact artifacts/envelopes, canonical registry, validation, promotion, quarantine, selection, monitoring and bounded retuners |
| Program retuning through facade options/task | `ProgramDeploymentSearchOptions` and private standalone task; current Evolution engine, fresh correctness gate, admitted limits and source bound |
| AutoML retuner | Same bounded MAP-Elites algorithm, now `AiDotNet.Evolution.AutoML.MapElitesAutoML`; original archive/options/interface and sampler are relocated too |
| Three regression test files | `tests/AiDotNet.Evolution.Deployment.Tests`, including original real-model holdout/persistence fixture |
| Compatibility project/workflow wiring | Modern standalone project tests in `program-consumer.yml`; core retains its existing net471 gate |
| Original docs and binary evidence | `aidotnet-pr-2202-original.zip`, retained as historical provenance, not current verification |

## Clean API boundary

Use `EvolutionDeploymentRetuners.Program` with `ProgramDeploymentSearchOptions`.
Use `EvolutionDeploymentRetuners.AutoML` with the relocated MAP-Elites options.
No `AiModelBuilder` Evolution configuration or obsolete forwarding methods are
introduced. Direct standalone search exposes the archive and trial history.
The old facade-based AutoML test is replaced by the same real-model search/score
assertions through that standalone entry point.

Both candidate and incumbent require fresh independent validation; search data
must not be reused as deployment holdout. Storage hashes are integrity checks,
not authentication. Caller factories are trusted; artifact names never authorize
reflection activation. Timeouts do not kill uncooperative nested work: its slot
stays occupied until settlement and late results cannot activate.

## Verification and adversarial review

Original deployment tests cover exact bytes, every applicability mismatch,
duplicate/unknown JSON, stale selection, persistent quarantine, concurrent writes,
timeouts/cancellation, storage loss, disposal errors, and persistence-license denial.
Additional tests exercise deterministic immutable AutoML archives, expensive-trial
deduplication, invalid shapes/options, precancellation and assembly ownership.
The trained-model fixture promotes, reloads, and predicts from actual trained
MultipleRegression weights using a separate holdout.

The test project retains the original `AiDotNetTests` friend-assembly identity and
existing test-only persistence scope. This avoids consuming developer trial quota;
production adapters never enter that scope, and denial-propagation tests remain.
No entitlement, provider, GPU-speed or competitor-superiority claim is made.

Moving across assemblies exposed internal-only sampler/model-key dependencies and
the published base class's required copy factory. The sampler is now local, the
documented model key is explicit, and a new search copy snapshots its options.
No production access to AiDotNet internals was added.

Final local validation: zero-warning Release solution build, 492 scoped passing
test executions, full solution formatting, and package-only consumer success.
Raw receipts: [deployment-relocation evidence](../evidence/deployment-relocation/README.md).
Hosted checks were pending at publication, not claimed green.

This migration does not complete #2148, #2168, #2203 or the separate clean-breaking
AiDotNet removal. Keep their source PRs open until their own ports are verified.
