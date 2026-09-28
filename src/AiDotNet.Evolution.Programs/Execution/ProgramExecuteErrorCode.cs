// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Execution/ProgramExecuteErrorCode.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs;

/// <summary>
/// Classifies program execution failures in a structured, machine-readable way.
/// </summary>
public enum ProgramExecuteErrorCode
{
    /// <summary>No failure.</summary>
    None = 0,
    /// <summary>The request is malformed, names an undefined or disallowed language, or has no interpreter configured.</summary>
    InvalidRequest = 1,
    /// <summary>The source is empty.</summary>
    SourceCodeRequired = 2,
    /// <summary>The source exceeds the sandbox's size limit.</summary>
    SourceCodeTooLarge = 3,
    /// <summary>Standard input exceeds the sandbox's size limit.</summary>
    StdInTooLarge = 4,
    /// <summary>The language was generic and could not be detected or defaulted.</summary>
    LanguageNotDetected = 5,
    /// <summary>The program is SQL, which the sandbox does not run.</summary>
    SqlNotSupported = 6,
    /// <summary>The wall-clock limit was reached, or the caller cancelled.</summary>
    TimeoutOrCanceled = 7,
    /// <summary>A compile-only request failed to compile.</summary>
    CompilationFailed = 8,
    /// <summary>The program could not start or exited non-zero.</summary>
    ExecutionFailed = 9
}
