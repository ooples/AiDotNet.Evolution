// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Execution/CompilationDiagnostic.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs;

/// <summary>One message a compiler or checker reported.</summary>
public sealed class CompilationDiagnostic
{
    /// <summary>Gets how serious it is.</summary>
    public CompilationDiagnosticSeverity Severity { get; init; } = CompilationDiagnosticSeverity.Error;

    /// <summary>Gets the message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the tool's code for it, such as <c>CS1002</c>, or <c>null</c>.</summary>
    public string? Code { get; init; }

    /// <summary>Gets the file it refers to, or <c>null</c>.</summary>
    public string? FilePath { get; init; }

    /// <summary>Gets the one-based line, or <c>null</c>.</summary>
    public int? Line { get; init; }

    /// <summary>Gets the one-based column, or <c>null</c>.</summary>
    public int? Column { get; init; }

    /// <summary>Gets the tool that reported it, or <c>null</c>.</summary>
    public string? Tool { get; init; }
}
