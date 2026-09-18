# Model-runtime transfer verification

The verification archive retains the final solution build log, five TRX reports,
offline host-test log and unique-version package consumer log. Local .NET validation:
1,102 passing executions, zero skipped. Four Python contracts cover ten host processes.

Reproduce:

```powershell
dotnet build AiDotNet.Evolution.slnx -c Release
dotnet test tests/AiDotNet.Evolution.CSharp.Tests -c Release --no-build
dotnet test tests/AiDotNet.Evolution.Deployment.Tests -c Release --no-build
dotnet test tests/AiDotNet.Evolution.Tests -c Release -f net10.0 --no-build --filter FullyQualifiedName~StoryPullRequestWorkflowTests
python benchmarks/ProgramEvolutionComparison/test_host.py
./eng/Test-DeploymentPackage.ps1
dotnet format AiDotNet.Evolution.slnx --no-restore --verify-no-changes
```

All providers/scorers in this verification are authored fixtures. No paid API, live
competitor comparison, candidate execution or PTX benchmark is involved. Broker receipt
counts and token/work units establish accounting, not product-quality improvement.
