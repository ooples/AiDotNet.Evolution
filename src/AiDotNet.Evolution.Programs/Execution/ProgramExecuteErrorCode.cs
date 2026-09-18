// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Execution/ProgramExecuteErrorCode.cs
// Original license retained in ../Legacy/AIDOTNET-LICENSE.txt.
namespace AiDotNet.Evolution.Programs;

/// <summary>
/// Classifies program execution failures in a structured, machine-readable way.
/// </summary>
public enum ProgramExecuteErrorCode
{
    None = 0,
    InvalidRequest = 1,
    SourceCodeRequired = 2,
    SourceCodeTooLarge = 3,
    StdInTooLarge = 4,
    LanguageNotDetected = 5,
    SqlNotSupported = 6,
    TimeoutOrCanceled = 7,
    CompilationFailed = 8,
    ExecutionFailed = 9
}
