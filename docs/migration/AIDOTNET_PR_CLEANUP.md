# AiDotNet PR relocation audit

All Evolution feature development belongs in AiDotNet.Evolution. This audit covers the
12 open Evolution feature/tracking PRs found in AiDotNet on 2026-09-16, separately
from package-removal/integration PR2092. Unrelated AiDotNet PRs are excluded.
Source branch tips and all 329 changed-file records are pinned in
[aidotnet-pr-inventory.json](aidotnet-pr-inventory.json).

## Documentation-only PRs: closed after publishing Evolution PR80

| AiDotNet PR | Story | Destination |
|---|---|---|
| [2163](https://github.com/ooples/AiDotNet/pull/2163) | US-03 | Current [Evolution PR75](https://github.com/ooples/AiDotNet.Evolution/pull/75), issue21 |
| [2164](https://github.com/ooples/AiDotNet/pull/2164) | US-11 | [Current checklist](../evolution-stories/US-11.md), issue29 |
| [2165](https://github.com/ooples/AiDotNet/pull/2165) | US-17 | Merged [Evolution PR73](https://github.com/ooples/AiDotNet.Evolution/pull/73), issue35 |
| [2166](https://github.com/ooples/AiDotNet/pull/2166) | US-18 | [Current checklist](../evolution-stories/US-18.md), issue36 |
| [2167](https://github.com/ooples/AiDotNet/pull/2167) | US-19 | [Current checklist](../evolution-stories/US-19.md), issue37 |

Each old PR changes exactly one Markdown file. Its exact original Git blob is retained
under `aidotnet-pr-N/US-XX.md`; original ownership/status text there is historical,
not current direction. Current checklists preserve the Given/When/Then criteria.
Closing these PRs means **relocated**, not **story completed**. Keep their branches and
issues; do not delete source history.

## Code-bearing PRs: disposition

AiDotNet PR2182 is now closed as relocated to non-draft
[Evolution PR81](https://github.com/ooples/AiDotNet.Evolution/pull/81), dependent on
PR80. Its complete eight-file change is accounted for in the
[runtime migration contract](PROGRAM_RUNTIME_MIGRATION.md). Source history remains.
AiDotNet PR2210 and PR2212 are closed as relocated to non-draft
[Evolution PR88](https://github.com/ooples/AiDotNet.Evolution/pull/88), dependent on
[PR87](https://github.com/ooples/AiDotNet.Evolution/pull/87) and its foundation stack.
Their complete disposition is in [the consumer migration map](PROGRAM_CONSUMER_MIGRATION.md).
Fresh validation: 3,733 core/program/compiler test executions, 18 consumer-study
runs, six corruption controls, output refusal checks, and byte-verified original
evidence. Hosted checks were pending at publication; closure is relocation, not merge.
AiDotNet PR2202 is also closed as relocated to non-draft
[Evolution PR89](https://github.com/ooples/AiDotNet.Evolution/pull/89), dependent on PR88.
The [deployment migration map](DEPLOYMENT_MIGRATION.md) accounts for lifecycle,
program/model retuners, local MAP-Elites orchestration and original evidence.
492 scoped tests and package-only consumption passed; hosted PR89 checks were pending
at publication. PR88 and PR89 hosted checks are now all green, including PR89's
clean-package fix at `a6b931a`.
The next [program foundation increment](PROGRAM_FOUNDATION_MIGRATION.md) moves the
actual task/edit/descriptor/language APIs and tests into Programs without an AiDotNet
dependency. It is only part of PR2148/2168 and does not authorize their closure.
PR2203's complete host contribution is now accounted for by the
[standalone model runtime and comparison host](MODEL_RUNTIME_MIGRATION.md); its source
PR2203 was closed after publishing non-draft Evolution PR91; all PR91 checks now pass.
The [execution runtime increment](EXECUTION_RUNTIME_MIGRATION.md) adds the real process
runner, execution contracts and input/output fitness evaluators without an AiDotNet dependency.
PR2148 and PR2168 still
require their remaining implementations to be ported. Partial foundation ports do not
justify closing those two PRs.
The [script/metric scoring increment](SCRIPT_METRICS_MIGRATION.md) additionally ports
script evaluation and typed scalarization, with bounded evidence and retained numeric
metrics. The [model judge increment](JUDGE_RUNTIME_MIGRATION.md) ports fitness feedback,
weighted panels, retries, and critique forwarding with adversarial validation and
request accounting. Novelty, artifact/output, facade and CLI integration remain outstanding.

| AiDotNet PR | Files | Work requiring preservation/port review |
|---|---:|---|
| 2148 | 208 | Foundation: public builder integration, C# compiler/proposal adapter, program evaluator, worker, output and correctness/cache contracts; includes inherited embedded-engine removal |
| 2168 | 59 | US-24 CLI, preflight, run control, inspection/evidence bundles, telemetry and raw-sample correctness hardening |
| 2182 (closed) | 8 | Fully relocated and tested in Evolution PR81; original raw-evidence files preserved |
| 2202 (closed) | 18 | Relocated to Evolution PR89: deployment lifecycle, model/program adapters and local MAP-Elites orchestration, original evidence preserved |
| 2203 (closed) | 3 | Complete replacement in Evolution PR91, benchmarks/ProgramEvolutionComparison, backed by standalone prompts/variation/provenance; hosted checks passed |
| 2210 (closed) | 11 | Relocated to Evolution PR88; standalone noise session, fresh model/sorting benchmark, verifiers, and original evidence |
| 2212 (closed) | 17 | Relocated to Evolution PR88; portfolio, standalone compiler-arm factory/provider contract, integration tests, usage aggregation and benchmark verification |

Current Evolution PR74–79 replace or extend several generic/benchmark capabilities,
but their presence does not prove all C# consumer contracts were ported.
Merged PR73 supplies standalone program/compiler projects; compare against those APIs
before importing older implementations. Do not overwrite current US-03–08 evidence
or resurrect a second copy of the engine.

The previous local consolidation commit331b359 staged US-11 examples but explicitly
left integration project references unresolved. It is not proof of shipping migration.

## AiDotNet removal boundary

[AiDotNet PR2092](https://github.com/ooples/AiDotNet/pull/2092) deletes the embedded engine
but explicitly retains the program/facade/AutoML implementations. It does not satisfy
the broader ownership correction by itself. Retain it while preparing the separate
removal change; do not close it as if a replacement already exists.

`AiModelBuilder.Evolution.cs` is a partial of a type owned by AiDotNet. Moving its file
to another assembly cannot extend that original type. The removal needs an explicit
public API transition. The user explicitly approved a **clean breaking removal** on
2026-09-16: no obsolete forwarding APIs. Replace old calls with standalone Evolution
entry points, and remove the old AiDotNet APIs in the separate removal PR.

No source PR is marked complete or closed merely because its diff is inventoried.
Cleanup PR publication and superseded source-PR closure are authorized. No merge,
package publication, branch deletion or protection change is part of this cleanup.
