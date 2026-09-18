# Program foundation relocation verification

Local Release verification on 2026-09-16:

- Solution build: zero warnings/errors; solution formatting verification passed.
- Programs/compiler tests: 350 net8.0 + 350 net10.0, including imported contracts and
  new adversarial source-boundary/language/cost-origin tests.
- Deployment tests: 41 net8.0 + 41 net10.0.
- Workflow stack tests: four net10.0.
- All 786 scoped test executions passed; none skipped.
- Twenty-seven archived source files matched their original Git blob hashes at
  AiDotNet `9cd7d5d6c366a483874024650d02901f69a1829c`.

`verification.zip` retains the build log, five TRX reports, and the successful
unique-version package-consumer log. The first consumer compile used a nonexistent
archive-entry `Genome` property; the fixture was corrected to
`Candidate.CanonicalGenome.Genome` before the final package verification.

The package fixture uses no project references. It runs two authored deterministic
evaluations through the actual engine/task/descriptor/edit APIs and checks fitness
1 -> 2. This proves package integration, not faster execution, generated-code
correctness, or superiority to OpenEvolve. No model calls or production PTX runs occur.

Reproduce from the repository root:

```powershell
dotnet build AiDotNet.Evolution.slnx -c Release
dotnet test tests/AiDotNet.Evolution.CSharp.Tests/AiDotNet.Evolution.CSharp.Tests.csproj -c Release --no-build
dotnet test tests/AiDotNet.Evolution.Deployment.Tests/AiDotNet.Evolution.Deployment.Tests.csproj -c Release --no-build
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net10.0 --no-build --filter FullyQualifiedName~StoryPullRequestWorkflowTests
./eng/Test-DeploymentPackage.ps1
dotnet format AiDotNet.Evolution.slnx --no-restore --verify-no-changes
```
