# Contributing

Use a Conventional Commit PR title. Keep the core task-agnostic and dependency-light; consumer-specific model, tensor, LLM, sandbox, and facade behavior belongs in the consuming repository.

Before opening a PR, run:

```powershell
dotnet format AiDotNet.Evolution.slnx --verify-no-changes
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net10.0
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net8.0
dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net471
dotnet pack src/AiDotNet.Evolution/AiDotNet.Evolution.csproj -c Release -o artifacts
```

Finite choices must use enums or typed values. Changes to canonicalization, random streams, persistence, selection, or archive ordering require deterministic regression tests.
