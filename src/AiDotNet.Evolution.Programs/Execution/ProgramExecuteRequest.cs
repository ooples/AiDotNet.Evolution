// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Execution/ProgramExecuteRequest.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.Programs;

public sealed class ProgramExecuteRequest
{
    public ProgramLanguage Language { get; set; } = ProgramLanguage.Generic;

    public List<ProgramLanguage> AllowedLanguages { get; set; } = new();

    public ProgramLanguage? PreferredLanguage { get; set; }

    public bool AllowUndetectedLanguageFallback { get; set; }

    public string SourceCode { get; set; } = string.Empty;

    public string? StdIn { get; set; }

    /// <summary>
    /// When true, the server should compile (or parse) the program but skip running it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is primarily intended for typed-language verification workflows (e.g., C# compilation checks) where
    /// you want to validate code generation output without executing untrusted code.
    /// </para>
    /// </remarks>
    public bool CompileOnly { get; set; }
}
