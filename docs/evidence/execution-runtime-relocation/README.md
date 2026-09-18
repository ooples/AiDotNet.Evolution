# Execution runtime verification

Environment: Windows, local .NET 8 and .NET 10, Release configuration.
No model/provider calls, paid APIs, generated hostile programs or competitor runs.

| Check | Result |
|---|---:|
| Solution Release build | 0 warnings, 0 errors |
| Programs/compiler suite, net8.0 | 630 passed, 0 skipped |
| Programs/compiler suite, net10.0 | 630 passed, 0 skipped |
| Deployment suite, both frameworks | 82 passed, 0 skipped |
| Story workflow contract, net10.0 | 4 passed |
| Total test executions | 1,346 passed |
| Package-only fresh consumer | Passed; real child process plus evaluator |
| Original source archive | 25 Git blobs verified byte-for-byte |

The preceding model-runtime increment had 508 Programs/compiler tests per framework;
this increment adds 122 per framework, including actual process, cancellation, output,
queue/disposal and adversarial identity tests. This is coverage of behavior, not a
performance or correctness-rate improvement claim about evolved algorithms.

The package consumer performs two authored search-fixture evaluations (quality 1 to 2)
and one actual child-process output evaluation (quality 1, one dispatched call).
Those scores are fixture assertions, not an OpenEvolve comparison or measured speedup.

[verification.zip](verification.zip) contains final build/package logs and five TRX
files for Programs/compiler, Deployment and workflow tests (2 + 2 + 1 = **5 TRX**).
Formatting verification passed with `dotnet format --verify-no-changes` (exit 0).

Reproduction from the repository root:

```powershell
dotnet build AiDotNet.Evolution.slnx -c Release
dotnet test tests/AiDotNet.Evolution.CSharp.Tests -c Release --no-build
dotnet test tests/AiDotNet.Evolution.Deployment.Tests -c Release --no-build
dotnet test tests/AiDotNet.Evolution.Tests -c Release --no-build -f net10.0 --filter FullyQualifiedName~StoryPullRequestWorkflowTests
./eng/Test-DeploymentPackage.ps1
dotnet format AiDotNet.Evolution.slnx --no-restore --verify-no-changes
```

Shell availability and observed descendant startup are required, not silently skipped.
The real-process tests use trusted OS shell fixtures. They do not establish a secure
hostile-code containment boundary; see the [migration contract](../../migration/EXECUTION_RUNTIME_MIGRATION.md).
