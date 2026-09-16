# AiDotNet PR relocation audit

All Evolution feature development belongs in AiDotNet.Evolution. This audit covers the
12 open Evolution feature/tracking PRs found in AiDotNet on 2026-09-16, separately
from package-removal/integration PR2092. Unrelated AiDotNet PRs are excluded.
Source branch tips and all 329 changed-file records are pinned in
[aidotnet-pr-inventory.json](aidotnet-pr-inventory.json).

## Documentation-only PRs: safe to close after publishing this relocation

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

## Code-bearing PRs: do not close until their unique implementation is accounted for

| AiDotNet PR | Files | Work requiring preservation/port review |
|---|---:|---|
| 2148 | 208 | Foundation: public builder integration, C# compiler/proposal adapter, program evaluator, worker, output and correctness/cache contracts; includes inherited embedded-engine removal |
| 2168 | 59 | US-24 CLI, preflight, run control, inspection/evidence bundles, telemetry and raw-sample correctness hardening |
| 2182 | 8 | US-23 raw sample evidence store and tests; overlaps 2168 but must be checked at exact source tips |
| 2202 | 18 | US-25 deployment registry, validated selection/rollback/retuning and model/program adapters |
| 2203 | 3 | US-02 C# program comparison host, not just the newer generic/Python comparison |
| 2210 | 11 | US-06 typed C# program noise session and benchmark/verification scripts |
| 2212 | 17 | US-08 program-specific portfolio, compiler-arm factory, usage aggregation and benchmark verification |

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
No merge, publication, branch deletion or protection change is part of this cleanup.
