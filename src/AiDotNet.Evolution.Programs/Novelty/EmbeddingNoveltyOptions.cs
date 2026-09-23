// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Configuration/EmbeddingNoveltyOptions.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.Globalization;

namespace AiDotNet.Evolution.Programs.Novelty;

public sealed class EmbeddingNoveltyOptions
{
    public const double DefaultStructuralNoveltyThreshold = 0.15;

    public const double DefaultEmbeddingSimilarityThreshold = 0.99;

    public const int DefaultMaxEmbeddingComparisons = 8;

    public const int DefaultMaxTrackedGenomes = 512;

    public EmbeddingNoveltyOptions(
        double structuralNoveltyThreshold = DefaultStructuralNoveltyThreshold,
        double embeddingSimilarityThreshold = DefaultEmbeddingSimilarityThreshold,
        int maxEmbeddingComparisons = DefaultMaxEmbeddingComparisons,
        bool failOpenOnEmbeddingFailure = true,
        bool failOpenOnJudgeFailure = true,
        int maxTrackedGenomes = DefaultMaxTrackedGenomes)
    {
        ValidateUnitInterval(structuralNoveltyThreshold, nameof(structuralNoveltyThreshold));
        ValidateUnitInterval(embeddingSimilarityThreshold, nameof(embeddingSimilarityThreshold));

        if (maxEmbeddingComparisons < 1 || maxEmbeddingComparisons > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEmbeddingComparisons), maxEmbeddingComparisons,
                "Value must be between 1 and 256.");
        }

        if (maxTrackedGenomes < 1 || maxTrackedGenomes > 8_192)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTrackedGenomes), maxTrackedGenomes,
                "Value must be between 1 and 8192.");
        }

        StructuralNoveltyThreshold = structuralNoveltyThreshold;
        EmbeddingSimilarityThreshold = embeddingSimilarityThreshold;
        MaxEmbeddingComparisons = maxEmbeddingComparisons;
        FailOpenOnEmbeddingFailure = failOpenOnEmbeddingFailure;
        FailOpenOnJudgeFailure = failOpenOnJudgeFailure;
        MaxTrackedGenomes = maxTrackedGenomes;
    }

    public double StructuralNoveltyThreshold { get; }

    public double EmbeddingSimilarityThreshold { get; }

    public int MaxEmbeddingComparisons { get; }

    public bool FailOpenOnEmbeddingFailure { get; }

    public bool FailOpenOnJudgeFailure { get; }

    public int MaxTrackedGenomes { get; }

    public string ToVersionString() => string.Join("|", new[]
    {
        StructuralNoveltyThreshold.ToString("R", CultureInfo.InvariantCulture),
        EmbeddingSimilarityThreshold.ToString("R", CultureInfo.InvariantCulture),
        MaxEmbeddingComparisons.ToString(CultureInfo.InvariantCulture),
        FailOpenOnEmbeddingFailure ? "embed-open" : "embed-closed",
        FailOpenOnJudgeFailure ? "judge-open" : "judge-closed",
        MaxTrackedGenomes.ToString(CultureInfo.InvariantCulture)
    });

    private static void ValidateUnitInterval(double value, string parameterName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "Value must be a finite number between 0 and 1.");
        }
    }
}
