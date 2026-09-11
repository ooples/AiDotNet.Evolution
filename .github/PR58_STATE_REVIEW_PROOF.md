# PR #58 state-validation review proof

This bounded follow-up addresses CodeQL review comments **3990510444**
(`PRRT_kwDOUOzYOs6hhcGQ`) and **3990510459** (`PRRT_kwDOUOzYOs6hhcGa`).
The PR branch is `feat/evolution-us-16`, targeting
`feat/competitive-evolution-platform`, not `main`. The exact before revision is
`95e388360d56f23ce4a49aadd09019b2e5030c00`.

## Scope and preserved contracts

- The allocation condition is separated into allocation accounting and recorded
  history validation. Decision validation separately checks ordering, destination,
  restart policy and pending attribution, in the original order.
- A private readonly validated-state value carries the deserialized non-null
  collections and counters. No graph copy, missing-field default, JSON schema or
  version-hash change is introduced. Individual child payloads remain nullable.
- Every policy check and child-presence check finishes before the first child
  restore. Policy collections/counters are published only after all children
  restore. A child failure still requires discarding the instance; child rollback
  is not promised.
- All null-forgiving operators are removed from `AdaptiveIslandSearch.State.cs`.
  New test controls use enums, not string-based case dispatch.

## Identical-test before/after results

The 84 existing adaptive-island cases are joined by 42 typed review cases. They
cover missing/null required fields, allocation and history boundaries, null and
out-of-order decisions, pending attribution, child-presence mismatches, restore
ordering, no early publication, and independently mutable source/restored policies.
They also retain meaningful existing behavior: zero-default omitted scalar
counters for an empty checkpoint, an empty but present child payload, and
non-consecutive generations up to `long.MaxValue`.

The final review test file is byte-identical in the before and after worktrees:
SHA-256 `5E0CFCC7A9938B5F3FC55E2B59F7FC8792A1727CC21B243C50AA972D0AEEDD46`.

| Run | net10.0 | net8.0 | net471 | Skipped |
| --- | ---: | ---: | ---: | ---: |
| Unchanged before library, expanded adaptive-island cohort | 126 passed | 126 passed | 126 passed | 0 |
| Refactored library, full test suite | 787 passed | 787 passed | 787 passed | 0 |

Before TRXs are `artifacts/pr58-review/pr58-final-identical-baseline-<tfm>.trx`.
Full after TRXs are
`artifacts/pr58-review/final-<tfm>/pr58-final-full-after-<tfm>.trx`.
A fresh-process focused replay also passed 126/126 on net10.0:
`pr58-final-focused-after-net10.0.trx`. This is behavior-preservation evidence;
the old implementation was not claimed to fail these valid/invalid contracts.

An independent reviewer replayed the final net10.0 assembly in another process:
**126 passed, zero failed, zero skipped**, recorded in
`artifacts/pr58-review/pr58-root-independent.trx`. The production diff, typed
corruption fixtures, validation/error ordering and child-restore boundaries were
also reviewed independently before publication.

| Loaded net10.0 artifact | Before SHA-256 | After SHA-256 |
| --- | --- | --- |
| `AiDotNet.Evolution.dll` | `9972778743FCC293634193E1095B6BEFACF0EF36E0E50C5214666B6937BC15F7` | `11B815772E2CE1A55AAA500A141230EFB02D4AF9A24EF5E26D7ADEE591EC71B9` |
| `AiDotNet.Evolution.Tests.dll` | `A1979AD6D83C4BC7E0253FA97FC76624CFDF51C3C891C4B911973FCBB24F61F7` | `45418DD9E422D4F0152090B2FA8590C7C8F30D855D97F5FB68E05531D6D6C2EF` |

## Negative controls

Four isolated, uncommitted mutants each removed exactly one new guard while
keeping the final tests unchanged. The reviewed worktree was never mutated.
The isolated worktree's production source was restored to the exact before
revision after the experiments; its last mutant binaries are not baseline proof.

| Removed guard | Failed cases | Remaining selected cases passed | TRX |
| --- | --- | ---: | --- |
| Epoch capacity | `EpochCapacity` | 16 | `pr58-mutant-epoch-capacity.trx` |
| Completed-epoch exploration floor | `CompletedEpochFloor` | 16 | `pr58-mutant-epoch-floor.trx` |
| Recorded decision count | `DecisionCount` | 16 | `pr58-mutant-history-count.trx` |
| Terminal generation | `LastDecisionGeneration`, `NullLastDecision` | 15 | `pr58-mutant-terminal-generation.trx` |

These controls use coherent totals and terminal records where applicable, so an
unrelated accounting check cannot conceal removal of the intended guard. The
null-terminal control additionally detects a change in the original error category.

## Local workflow checks

- All three target frameworks built with zero warnings and zero errors.
- Whole-solution `dotnet format --verify-no-changes` passed.
- net10.0 coverage: **92.29% line**, **78.82% branch**; the actual coverage-ratchet
  script passed against minimums 88.80% and 73.51%.
- The actual package build/content/dependency validator passed for all three
  framework assemblies. Package SHA-256:
  `6866EFA5F1AEBB85DC0AE54FF6CED58CD778CBDCCD8DD47A60CF9910704B5A5F`.
- Release-workflow security script: 4/4 passed. Adaptive-island example: eight
  primary runs, with replay/accounting/checkpoint checks passing.

These commands ran locally on Windows, not on GitHub's Ubuntu runner. The CodeQL
CLI was not installed/on PATH, so **no local CodeQL scan is claimed**; a fresh
CodeQL job must confirm the alerts are cleared. No remote publication, review
resolution, package release or whole-PR readiness claim is included here.

## Reproduction

From the follow-up checkout, build and run against its actual library:

```powershell
$project = 'tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj'
dotnet build $project -c Release -p:GeneratePackageOnBuild=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed; do not test stale assemblies.' }
foreach ($framework in @('net10.0', 'net8.0', 'net471')) {
    dotnet test $project -c Release -f $framework --no-build --no-restore --logger "trx;LogFileName=pr58-full-$framework.trx" --results-directory artifacts/pr58-review --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Tests failed for $framework." }
}
dotnet format AiDotNet.Evolution.slnx --no-restore --verify-no-changes
dotnet pack src/AiDotNet.Evolution/AiDotNet.Evolution.csproj -c Release --no-build --no-restore --output artifacts/pr58-review/packages
./eng/Test-Package.ps1 -PackagePath artifacts/pr58-review/packages/AiDotNet.Evolution.0.1.0-preview.1.nupkg -ExpectedVersion 0.1.0-preview.1
./eng/Test-ReleaseWorkflow.ps1 -NoRestore
dotnet run --project examples/AdaptiveIslandSearch -c Release -- 1 64 artifacts/pr58-review/adaptive-islands-smoke.json
```

For a before replay, create a fresh detached worktree at the exact before SHA.
Apply only the new `AdaptiveIslandSearchStateReviewTests.cs` test-file diff from
this follow-up; do not apply the production refactor. Run the same test project
there for each framework with
`--filter 'FullyQualifiedName~AdaptiveIslandSearchTests'`. Both the loaded library
hash and the exact test-file hash should be recorded before any mutant or later
build replaces the output. Worktree roots are caller-provided paths; no
author-specific directory is required.
