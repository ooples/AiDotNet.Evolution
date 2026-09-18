# PR #59: pipeline failure cleanup review

Baseline: `c1425b4f740b2251640727e95a0479a238b1182f`.
Finding: [CodeQL empty catch, comment3992938368](https://github.com/ooples/AiDotNet.Evolution/pull/59#discussion_r3992938368).

## Root correction

The drain boundary no longer silently discards recoverable task outcomes. It returns an internal typed completion status, which callers record in bounded, immutable report counters. Faults take precedence over cancellation; a drain counts once, not once per failed task. No callback exception text or genome data is retained. Successful retry drains add neither a diagnostic lock nor a schedule record.

Awaiting `Task.WhenAll` surfaces only one exception. The boundary now inspects the full aggregate before treating a batch as recoverable, so an ordinary first failure cannot hide a fatal sibling. Fatal failures remain fatal; ordinary cleanup preserves the original wave exception.

Independent adversarial review found two additional reachable lifecycle defects:

- A caller's cancellation callback could throw before owned callbacks drained and before the wave rolled back. Recoverable cancellation-callback failures now increment a separate bounded counter, preserve the original failure, and cannot bypass the nested drain/rollback cleanup.
- A fatal retry drain could bypass `EndPipelinePhase`. Retry resource closure now has its own `finally`; aborted-wave bookkeeping and cursor restoration also execute after owned tasks settle even when the drain throws.

No scheduling, resource-refund policy, checkpoint format, engine selection or normal training/search algorithm was replaced. Actual dispatched fatal work retains its conservative charge.

## Failure-first evidence

| Actual test boundary | Before | After |
| --- | --- | --- |
| Six new drain controls against the original baseline helper | 4 failed / 2 passed | All six pass |
| Two real-engine lifecycle controls against the intermediate drain-only fix, before nested cleanup | 2 failed | Both pass |
| Final focused pipeline + new review suite | — | 40 passed / 0 failed / 0 skipped |
| Full core suite, net10.0 | — | 701 passed / 0 failed / 0 skipped |
| Full core suite, net8.0 | — | 701 passed / 0 failed / 0 skipped |
| Full core suite, net471 | — | 701 passed / 0 failed / 0 skipped |
| Performance-evidence contracts | — | 55 passed |
| Python analysis contracts | — | 28 passed |

The first four failures are three unobserved completion outcomes and an ordinary-first/fatal-second aggregate that was suppressed. The lifecycle failures are not helper mocks: one receives the cancellation callback's aggregate instead of the original synthetic fatal proposal error; the other leaves the real resource adapter's retry phase active.

The intermediate lifecycle-before assemblies had SHA-256 `5E42345CCEFF957576F7933D414E36ED2A51E5608CB0DBC3D566EDCE4438A7F0` (core) and `D2FBBAEB9AA56188DB9182F95624C7EE6E44F6090D14DD52A352B71481F0F81E` (tests). These identify the drain-only intermediate, not the original baseline.

The two lifecycle tests run actual `EvolutionEngine`, variation/task contracts, `ResourceMeteredEvolutionTask`, resource ledger and transaction cursors. Task-completion gates establish ordering; 15-second guards only fail hangs. Test cleanup always releases owned callbacks. Final assertions require zero active workers, original fatal exception identity, restored counters, aborted-wave records, exact settled/unknown resource receipts, no remaining reservations, and a reusable closed resource phase. Synthetic exception instances do not induce actual memory exhaustion.

Eight new drain/report tests plus two lifecycle tests extend the original 691-case inventory to 701. The final lifecycle fixture additionally checks the new cancellation-operation counter. The original cancellation/checkpoint regression still runs, now also verifying the observed drain counters.

Independent review authored and reproduced the two lifecycle failures, inspected the nested cleanup, and independently passed all 40 focused cases after correction. Its final test-only counter assertions briefly overlapped the first root net10 run; therefore the final frozen net10 DLL was explicitly replayed through **all 701 cases with coverage**, rather than relying on that overlapping run. The net8/net471 final builds also include those assertions.

## Build, coverage and retained artifacts

All three builds use the project's existing warnings-as-errors policy. Final changed-file `dotnet format --verify-no-changes` and `git diff --check` pass. Final coverage is **92.31% line / 78.68% branch**; the unchanged repository ratchet passes (minimum88.80% /73.51%). No coverage exclusions or tolerance changes.

Local TRXs:

- Temporary directory `pr59-review-results-20260911/pr59-drain-before.trx`.
- Worktree `artifacts/pr59-review-lifecycle/pr59-lifecycle-before.trx`.
- Worktree `artifacts/pr59-review-lifecycle/pr59-lifecycle-independent-after.trx`.
- Temporary directory `pr59-review-results-20260911/pr59-final-all-net8.0.trx` and `pr59-final-all-net471.trx`.
- Temporary directory `pr59-final-review-coverage-20260911/pr59-final-net10-exact-replay.trx`.
- Temporary directory `pr59-review-results-20260911/pr59-final-performance-contracts.trx`.

Final runtime identities:

| Framework | Core DLL SHA-256 | Test DLL SHA-256 |
| --- | --- | --- |
| net10.0 | `09ABE7798F9D67BD3ADB6B85B2635481F2A7CF991DCC6692509592D258AA61D1` | `6B0BFFFC96E48C49F62F687D2E42190CD3D38B8BECDFBC728EAA53AC534703C8` |
| net8.0 | `E0A8C60233717C1DF419FB1A7C8E98B0E83D27B305742B73A62A54AD89C9B6A3` | `81395FB9FD6C044A239EA6AEAD6FDF6C509FD681767B40CBC637C5F95B0CB5A1` |
| net471 | `F065281265464E03EB032FA2CD1E2428FD3C6F88B7B087C4A420D2E7590C0767` | `0115A6C122E312069AB538ED4B1DA5EF6E05E5D1B446C5B51F03B158A7D3849B` |

## Reproduction and limits

Run sequentially from the repository root:

```powershell
foreach ($framework in @('net10.0','net8.0','net471')) {
    dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f $framework -p:GeneratePackageOnBuild=false --logger "trx;LogFileName=review-$framework.trx" --results-directory artifacts/pr59-review -v:quiet
    if ($LASTEXITCODE -ne 0) { throw "Core tests failed: $framework" }
}
dotnet test tests/AiDotNet.Evolution.Performance.Tests/AiDotNet.Evolution.Performance.Tests.csproj -c Release -p:GeneratePackageOnBuild=false -v:quiet
if ($LASTEXITCODE -ne 0) { throw 'Performance contracts failed' }
python -m unittest discover -s benchmarks/analysis -v
if ($LASTEXITCODE -ne 0) { throw 'Analysis contracts failed' }
```

For the original helper negative control, copy the final `PipelineDrainReviewTests.cs` into a separate clean baseline checkout. In that isolated copy, omit only `DiagnosticCountersAreBoundedSnapshotsAndDoNotConsumeScheduleCapacity`, which references the newly added report API. Keep the actual helper controls unchanged and use this filter to reproduce the recorded six cases:

```text
FullyQualifiedName~DrainReturnsAnObservedOutcomeInsteadOfSilentlyDiscardingIt|FullyQualifiedName~EveryFaultIsCheckedForFatalRuntimeFailures|FullyQualifiedName~DrainWaitsForEveryOwnedTaskAndPreservesTheOriginalFailure
```

Those tests reflect the actual private helper, so they compile against its former Task-only result. Require the specific four failures, not a build/discovery failure. Do not remove or filter any tests in the final branch's normal suite. The intermediate lifecycle negative control is separately identified above; it is not misrepresented as an original-head scan.

These are local correctness and lifecycle results. No fresh hosted CodeQL success, release/merge, new pipeline timing benchmark, competitor/model-performance result, or zero-overhead claim is made. The prior pilot/default-profile performance evidence remains historical and was not rerun as part of this cleanup fix.

## Follow-up verification after collaborator commit 34872a1

The collaborator added first-round and retry-round fault-during-cancellation
controls in `34872a1a2029b3e3cd6da994ccdc218ba63d0c0d`. After fast-forwarding to that
revision, this follow-up replaced its new null-forgiving Task.Exception access
with an explicit AggregateException pattern. No pipeline outcome policy changed.

The subsequent CodeQL constant-condition thread identified a redundant null
test guarded by a boolean that already proved the owner existed. The retry phase
now retains the actual successfully begun resource owner instead of a separate
boolean. Cleanup runs only for that owner, after draining, and never when begin
failed. It adds no null-forgiving operator or scanner suppression. The same
43-case cohort was rerun after this ownership simplification; all three targets
passed again (`review-phase-owner-final-<TFM>.trx` in the same results directory).

The actual pipeline, drain, and cleanup suites passed **43/43 on each of net10.0,
net8.0, and net471**, with zero failures/skips. These include the collaborator's
three new cancellation/fault controls. Evidence is in
`TestResults/pr59-review-cancellation/review-cancellation-final-<TFM>.trx`.

```powershell
foreach ($framework in @('net10.0','net8.0','net471')) {
    dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f $framework -m:1 -p:UseSharedCompilation=false -p:GeneratePackageOnBuild=false --logger "trx;LogFileName=review-cancellation-final-$framework.trx" --results-directory TestResults/pr59-review-cancellation --filter 'FullyQualifiedName~EvolutionPipelineTests|FullyQualifiedName~PipelineDrainReviewTests|FullyQualifiedName~PipelineCleanupLifecycleReviewTests' -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Pipeline review tests failed: $framework" }
}
```

This is bounded local verification of the follow-up, not a rerun of the earlier
701-case full suite or a claim that the queued hosted scanner has passed.
