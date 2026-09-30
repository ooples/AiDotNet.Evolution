// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/ProgramSynthesis/Execution/ProgramExecuteRequest.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.Programs;

/// <summary>One program to run in the sandbox.</summary>
public sealed class ProgramExecuteRequest
{
    /// <summary>Gets or sets the program's language, or <see cref="ProgramLanguage.Generic"/> to detect it from the source.</summary>
    public ProgramLanguage Language { get; set; } = ProgramLanguage.Generic;

    /// <summary>Gets or sets the languages the program may be run as; empty allows any configured language.</summary>
    public List<ProgramLanguage> AllowedLanguages { get; set; } = new();

    /// <summary>Gets or sets the language used when <see cref="Language"/> is generic and detection fails.</summary>
    public ProgramLanguage? PreferredLanguage { get; set; }

    /// <summary>Gets or sets whether, when detection fails and no preferred language is set, the first allowed language is used.</summary>
    public bool AllowUndetectedLanguageFallback { get; set; }

    /// <summary>Gets or sets the program text. Required.</summary>
    public string SourceCode { get; set; } = string.Empty;

    /// <summary>Gets or sets the text written to the program's standard input, or <c>null</c> for none.</summary>
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
