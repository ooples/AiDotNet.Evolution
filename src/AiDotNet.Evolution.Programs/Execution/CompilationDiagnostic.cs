// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Execution/CompilationDiagnostic.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs;

public sealed class CompilationDiagnostic
{
    public CompilationDiagnosticSeverity Severity { get; init; } = CompilationDiagnosticSeverity.Error;

    public string Message { get; init; } = string.Empty;

    public string? Code { get; init; }

    public string? FilePath { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    public string? Tool { get; init; }
}
