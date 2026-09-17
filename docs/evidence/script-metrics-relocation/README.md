# Script metrics verification

Final local verification on Windows, Release configuration:

| Check | Result |
|---|---:|
| Solution build | 0 warnings, 0 errors |
| Programs/compiler, net8.0 | 716 passed, 0 skipped |
| Programs/compiler, net10.0 | 716 passed, 0 skipped |
| Deployment, both frameworks | 82 passed, 0 skipped |
| Workflow contracts, net10.0 | 4 passed |
| Total test executions | 1,518 passed |
| Fresh package-only consumer | Passed |
| Formatting verification | Exit 0 |
| Original source archive | 13 Git blobs byte-verified |

Programs/compiler coverage increased from 630 to 716 test cases per framework;
these are additional behavior checks, **not a measured algorithmic speedup**.

The package consumer performs two authored search-fixture evaluations and two real
child-process evaluations. Its new script prints `speed=4` and `accuracy=2`; the
standalone evaluator derives quality3 and retains speed4 in reporting metrics.
These are fixture values, not measured speed, accuracy or competitor results.
No model calls, paid APIs or hostile generated code were used.

[verification.zip](verification.zip) contains final build/test/package logs and five
TRX files: two Programs/compiler, two Deployment and one workflow-contract result.

```powershell
dotnet build AiDotNet.Evolution.slnx -c Release
dotnet test tests/AiDotNet.Evolution.CSharp.Tests -c Release --no-build
dotnet test tests/AiDotNet.Evolution.Deployment.Tests -c Release --no-build
dotnet test tests/AiDotNet.Evolution.Tests -c Release --no-build -f net10.0 --filter FullyQualifiedName~StoryPullRequestWorkflowTests
./eng/Test-DeploymentPackage.ps1
dotnet format AiDotNet.Evolution.slnx --no-restore --verify-no-changes
```

See [migration contract](../../migration/SCRIPT_METRICS_MIGRATION.md) for scoring,
disclosure and execution-boundary limitations, and remaining cleanup scope.
