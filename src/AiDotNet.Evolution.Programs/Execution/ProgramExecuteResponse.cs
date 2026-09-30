// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Execution/ProgramExecuteResponse.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.Programs;

/// <summary>What happened when a program ran. Ordinary failures are reported here, not thrown.</summary>
public sealed class ProgramExecuteResponse
{
    /// <summary>Gets whether the program (or, for a compile-only request, the compilation) succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Gets the language the program was run as.</summary>
    public required ProgramLanguage Language { get; init; }

    /// <summary>Gets whether the request was compile-only.</summary>
    public bool CompilationAttempted { get; init; }

    /// <summary>Gets whether compilation succeeded, or <c>null</c> when it was not attempted.</summary>
    public bool? CompilationSucceeded { get; init; }

    /// <summary>Gets structured compiler diagnostics, when the toolchain reported any.</summary>
    public List<CompilationDiagnostic> CompilationDiagnostics { get; init; } = new();

    /// <summary>Gets the exit code, or -1 when the program was not run or was terminated.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Gets the captured standard output.</summary>
    public string StdOut { get; init; } = string.Empty;

    /// <summary>Gets the captured standard error.</summary>
    public string StdErr { get; init; } = string.Empty;

    /// <summary>Gets whether standard output exceeded its cap and was cut.</summary>
    public bool StdOutTruncated { get; init; }

    /// <summary>Gets whether standard error exceeded its cap and was cut.</summary>
    public bool StdErrTruncated { get; init; }

    /// <summary>Gets a human-readable failure description, or <c>null</c> on success.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the failure category, or <c>null</c> on success.</summary>
    public ProgramExecuteErrorCode? ErrorCode { get; init; }
}
