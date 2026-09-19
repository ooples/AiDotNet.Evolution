// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Configuration/ScriptProgramEvaluationOptions.cs
// Original license retained in src/AiDotNet.Evolution.Programs/Legacy/AIDOTNET-LICENSE.txt.
using System.Globalization;
using AiDotNet.Evolution;

namespace AiDotNet.Evolution.Programs;

/// <summary>Bounds script evaluation, JSON parsing and optional artifact disclosure.</summary>
public sealed class ScriptProgramEvaluationOptions
{
    /// <summary>Explicitly retain bounded artifact names/text; default false withholds payloads.</summary>
    /// <remarks>Retained text is untrusted, may contain private data, and is not labeled redacted.</remarks>
    public bool RetainArtifactText { get; set; }
    /// <summary>Maximum captured response accepted before JSON parsing.</summary>
    public int MaxResponseChars { get; set; } = 262_144;
    /// <summary>Maximum nesting depth accepted in script JSON.</summary>
    public int MaxJsonDepth { get; set; } = 32;
    /// <summary>Maximum entries in each reported metric/descriptor/objective collection.</summary>
    public int MaxMetricCount { get; set; } = 256;
    /// <summary>The largest artifact text retained per artifact, in characters.</summary>
    public const int MaxArtifactLengthCeiling = 4_000;

    /// <summary>The largest number of artifacts retained from one evaluation.</summary>
    public const int MaxArtifactCountCeiling = 16;

    /// <summary>Gets or sets the evaluator script source, or <c>null</c> when it is supplied directly to the evaluator.</summary>
    public string? EvaluatorScript { get; set; }

    /// <summary>Gets or sets the language the evaluator script is written in.</summary>
    /// <remarks>The sandbox must have an interpreter configured for this language, or every evaluation is refused.</remarks>
    public ProgramLanguage EvaluatorScriptLanguage { get; set; } = ProgramLanguage.Python;

    /// <summary>Gets or sets the text that must appear in the script for it to be accepted. Defaults to <c>"evaluate"</c>.</summary>
    public string EntryPointMarker { get; set; } = "evaluate";

    /// <summary>Gets or sets whether <see cref="EntryPointMarker"/> is enforced when the evaluator is constructed.</summary>
    public bool RequireEntryPoint { get; set; } = true;

    /// <summary>Gets or sets whether a larger reported quality is better.</summary>
    public EvolutionOptimizationDirection Direction { get; set; } = EvolutionOptimizationDirection.Maximize;

    /// <summary>Gets or sets how many artifacts from one evaluation become diagnostics. Defaults to 4.</summary>
    public int MaxArtifactCount { get; set; } = 4;

    /// <summary>Gets or sets how many characters of each artifact are retained. Defaults to 500.</summary>
    public int MaxArtifactLength { get; set; } = 500;

    /// <summary>Creates an independent copy so a running evaluator is unaffected by later mutation.</summary>
    /// <returns>A new instance carrying the same values.</returns>
    public ScriptProgramEvaluationOptions Clone() => new()
    {
        RetainArtifactText = RetainArtifactText,
        MaxResponseChars = MaxResponseChars,
        MaxJsonDepth = MaxJsonDepth,
        MaxMetricCount = MaxMetricCount,
        EvaluatorScript = EvaluatorScript,
        EvaluatorScriptLanguage = EvaluatorScriptLanguage,
        EntryPointMarker = EntryPointMarker,
        RequireEntryPoint = RequireEntryPoint,
        Direction = Direction,
        MaxArtifactCount = MaxArtifactCount,
        MaxArtifactLength = MaxArtifactLength
    };

    /// <summary>Rejects a configuration the evaluator could not honour.</summary>
    /// <exception cref="ArgumentException"><see cref="EntryPointMarker"/> is empty or white space while it is required.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="EvaluatorScriptLanguage"/> or <see cref="Direction"/> is not a defined value, or an artifact bound
    /// is negative or exceeds its ceiling.
    /// </exception>
    public void Validate()
    {
        if (MaxResponseChars is < 1 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(MaxResponseChars));
        if (MaxJsonDepth is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(MaxJsonDepth));
        if (MaxMetricCount is < 1 or > EvolutionTaskResult.MaximumNamedValues) throw new ArgumentOutOfRangeException(nameof(MaxMetricCount));
        if (!Enum.IsDefined(typeof(ProgramLanguage), EvaluatorScriptLanguage))
            throw new ArgumentOutOfRangeException(nameof(EvaluatorScriptLanguage), EvaluatorScriptLanguage,
                "Value must be a defined language.");
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), Direction))
            throw new ArgumentOutOfRangeException(nameof(Direction), Direction, "Value must be a defined direction.");
        if (RequireEntryPoint && string.IsNullOrWhiteSpace(EntryPointMarker))
            throw new ArgumentException(
                "EntryPointMarker cannot be empty while RequireEntryPoint is set.", nameof(EntryPointMarker));
        if (MaxArtifactCount < 0 || MaxArtifactCount > MaxArtifactCountCeiling)
            throw new ArgumentOutOfRangeException(nameof(MaxArtifactCount), MaxArtifactCount,
                $"Value must be between 0 and {MaxArtifactCountCeiling}.");
        if (MaxArtifactLength < 0 || MaxArtifactLength > MaxArtifactLengthCeiling)
            throw new ArgumentOutOfRangeException(nameof(MaxArtifactLength), MaxArtifactLength,
                $"Value must be between 0 and {MaxArtifactLengthCeiling}.");
    }
}
