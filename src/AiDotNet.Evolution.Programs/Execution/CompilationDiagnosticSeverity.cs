// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Execution/CompilationDiagnosticSeverity.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs;

/// <summary>How serious a <see cref="CompilationDiagnostic"/> is.</summary>
public enum CompilationDiagnosticSeverity
{
    /// <summary>Informational.</summary>
    Info = 0,
    /// <summary>A warning; the build can still succeed.</summary>
    Warning = 1,
    /// <summary>An error; the build fails.</summary>
    Error = 2
}
