# US-06 final local verification

Issue [#24](https://github.com/ooples/AiDotNet.Evolution/issues/24), PR [#49](https://github.com/ooples/AiDotNet.Evolution/pull/49).
Verified implementation: `0a0faa8dbe0feefa53cdc8ead9193d3e5aba1250`, September 15, 2026.
Base: US-05 `feat/evolution-us-05`; combined foundations remain in ancestry.

One final Release solution build: **0 warnings, 0 errors**, 14.78 seconds.
One final full test batch: **704 net10.0 / 658 net8.0 / 658 net471**, zero failures or skips.
Includes 26 new policy cases per target. Format verification passed without changes.
Coverage: **90.84% lines / 76.32% branches**, above ratchet minima 88.80% / 73.51%.
Legacy 32-sample replication example and new `--noise-policies` executable both exited 0.
No production-code repair or repeated build/test cycle was required. An initial restore command used a nonexistent
`.sln` suffix; corrected to the repository's `.slnx` before the final build.

The new executable confirmed the known bounded-noise improvement and refused the search-only optimistic candidate.
It recorded 16 search + 256 confirmation + 256 rejection-audit + 9 timing callback calls = **537 charged units**.
Timing's nine invocations contain six warmups and three fresh fitness samples, not nine fitness samples.
The tests additionally cover minimization, durable one-use challenge slots across restore, cancellation at batch
boundaries, budget-short confirmation/audits, unknown failed receipts, order-invariant rejection sampling,
duplicate/repeated audit refusal, warmup failure and cooperative timing deadlines.

## Raw evidence and reproduction

[final-validation.zip](final-validation.zip), 889,559 bytes.
SHA-256: `d4d39012cbe26ad4c86896ffda775bfc9448a94f3785d5607260591c5a11caeb`.
Contains build/format/coverage logs, all three TRX files, Cobertura coverage and both executable JSON reports.

```powershell
dotnet restore AiDotNet.Evolution.slnx
dotnet build AiDotNet.Evolution.slnx -c Release --no-restore -m:2
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net10.0 --no-build --collect:"XPlat Code Coverage" --results-directory TestResults/us06/net10 --logger trx
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net8.0 --no-build --logger trx
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net471 --no-build --logger trx
dotnet format AiDotNet.Evolution.slnx --no-restore --verify-no-changes
dotnet examples/ReplicatedEvaluation/bin/Release/net10.0/ReplicatedEvaluation.dll --noise-policies
dotnet examples/ReplicatedEvaluation/bin/Release/net10.0/ReplicatedEvaluation.dll 32
```

Pass the emitted Cobertura path and `coverage-baseline.json` to `eng/Test-Coverage.ps1`.
Use Windows with the repository SDK and .NET Framework prerequisites for all three targets.

## Readiness boundaries

Core policy/API scope is implemented and locally verified. CI now executes the noise-policy preset and uploads its
JSON; local success is not a claim that hosted CI or independent review passed. No consumer source changed.
The example is an executable contract preset, **not a validated production cascade recommendation**.
Actual consumer training/runtime studies, domain-specific independence/reset/drift controls, US-03 process isolation,
consumer instrumentation, dependency integration and review/merge gates remain open. No performance-superiority,
full-story/full-roadmap completion, deployment, merge or publication is claimed. Issue #24 must remain open.
See [protocol assumptions and audit interpretation](../../REPLICATED_EVALUATION.md).
