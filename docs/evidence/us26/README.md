# US-26 verification evidence

Implementation tested: `e35e843d40ef50e1b44f5bedef8ebdb8585f9fa0`.
Shared foundation retained: `10aa358ddc806cd5fc26afedfdeed91864fcf85d`.
The evidence-only commit adds no executable changes. Issue #44 remains open for independent competitive validation and dependency/merge gates.

`verification.zip`: **3,072,426 bytes**, SHA256
`988140b730c252100b4eef6cb6e90ea7fb9270b7e02f0bb58177a8d0fc21ac0a`.

## Final committed-source results

All commands ran locally on Windows with the existing SDK/runtimes. No paid API calls, deployment, package publication or merge occurred.

| Gate | Result | Raw evidence in archive |
| --- | --- | --- |
| Full solution Release build, all three library/test targets | 0 warnings, 0 errors | `us26-build-committed.log` |
| net10.0 full suite | 646 passed, 0 failed, 0 skipped | `us26-committed/net10/net10-committed.trx` |
| net8.0 full suite | 646 passed, 0 failed, 0 skipped | `us26-committed/net8/net8-committed.trx` |
| net471 full suite | 646 passed, 0 failed, 0 skipped | `us26-committed/net471/net471-committed.trx` |
| Coverage ratchet | 92.36% line / 77.60% branch; minima 88.80% / 73.51% | `us26-coverage-committed.log`, final net10 Cobertura XML |
| Full solution format verification | exit 0 | `us26-format-verified.log` (empty success log) |
| Real-engine CPU example | `NoDevelopmentImprovement`, 48 trials, 3006 work units | `us26-example-committed.log`, `us26-committed/cpu-report.json` |

Final net10 test DLL SHA256: `118bc493356362f9c7c263f393e24094e6783cd7fa1b4f7938b774d16c4f8594`.
Final net10 library DLL SHA256: `71d1bb3ba69ac05d0e32cae387c135677c83f96d776be559dcbc64a743c38926`.

The CPU plan hash is `e1d0cb9947b967bca9b8ce7a549b4f04a0e745fbc3bf161c983ffe560f0389f8`. Its non-improvement result correctly avoids spending on held-out tasks and retains the stable control. Synthetic test panels separately verify held-out acceptance, overfitting rejection, baseline multiplicity, unstable results and lack of statistical power. The example's hand-specified controls are not asserted to be strong independently tuned competitor baselines; its mathematical family names do not prove semantic independence. This is not a superiority claim.

## Commands

```powershell
dotnet build AiDotNet.Evolution.slnx -c Release --no-restore -p:GeneratePackageOnBuild=false -m:2
dotnet format AiDotNet.Evolution.slnx --no-restore --verify-no-changes --verbosity quiet
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net10.0 --no-build --no-restore --logger 'trx;LogFileName=net10-committed.trx' --results-directory .local/us26-committed/net10 --collect:'XPlat Code Coverage'
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net8.0 --no-build --no-restore --logger 'trx;LogFileName=net8-committed.trx' --results-directory .local/us26-committed/net8
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net471 --no-build --no-restore --logger 'trx;LogFileName=net471-committed.trx' --results-directory .local/us26-committed/net471
dotnet run --project examples/PolicySearch -c Release --no-build --no-restore -- .local/us26-committed/cpu-report.json
```

Tests used `DOTNET_PROCESSOR_COUNT=2`. Every command above passes `--no-restore`, so none of them restores; the restore was a separate `dotnet restore AiDotNet.Evolution.slnx` run before this sequence, which fresh machines also need. The example refuses to overwrite an existing report; choose a new output path for a new run.

## Failed gates retained, not reclassified as passes

1. Initial build passed, but `us26-net10.log` recorded 632 pass / 11 fail. Fixed capacity 1024 exceeded small descriptor grids. Capacity is now the smaller of 1024 and the validated grid size; grids above 65536 cells are rejected.
2. The first repair recorded 639 pass / 4 fail. `Observe` existed but the mixture lacked `IOutcomeAwareVariationOperator`, so the engine never reconciled pending proposals. The erroneous intermediate CPU report in `us26-results/cpu-report.json` is **invalid performance evidence**: it included 46 failed proposals in a segment but qualified partial seed quality. The interface/state contract is now implemented, inner failure handling is fail-fast, and failed/canceled/timed-out statuses cannot qualify partial quality.
3. The dispatch repair passed 643 tests. Three additional regression cases lock down dispatch/state restoration, fatal cleanup preservation and failed proposals with earlier valid seed quality. Final total is 646 per target.
4. The initial included-directory format command did not fix all new source files. Full-solution verification returned exit 2; a whitespace-format pass fixed it, full verification returned 0, and the committed source was rebuilt/retested afterward. `us26-format-verify.log` is the failed log; `us26-format-verified.log` is the successful one.

Earlier `us26-final` receipts precede that whitespace-only cleanup. The authoritative final receipts are in `us26-committed`; earlier artifacts are retained solely for audit.

Hosted current-head CI, Linux execution, reviewer approval, dependency readiness and eventual retarget/merge are separate gates, not claimed by these local receipts. CI now runs the CPU example and uploads its report.
