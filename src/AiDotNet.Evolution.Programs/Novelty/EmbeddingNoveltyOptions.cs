// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Configuration/EmbeddingNoveltyOptions.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.Globalization;

namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>Thresholds and limits for <see cref="ProgramNoveltyPolicy"/>'s three stages.</summary>
public sealed class EmbeddingNoveltyOptions
{
    /// <summary>The default structural distance at or above which a candidate is novel without further checks.</summary>
    public const double DefaultStructuralNoveltyThreshold = 0.15;

    /// <summary>The default cosine similarity below which a candidate is novel.</summary>
    public const double DefaultEmbeddingSimilarityThreshold = 0.99;

    /// <summary>The default number of nearest neighbours compared by embedding.</summary>
    public const int DefaultMaxEmbeddingComparisons = 8;

    /// <summary>The default number of programs remembered for comparison.</summary>
    public const int DefaultMaxTrackedGenomes = 512;

    /// <summary>Creates novelty options.</summary>
    /// <param name="structuralNoveltyThreshold">Structural distance, 0 to 1, at or above which a candidate is novel; 0 disables the structural shortcut.</param>
    /// <param name="embeddingSimilarityThreshold">Cosine similarity, 0 to 1, below which a candidate is novel.</param>
    /// <param name="maxEmbeddingComparisons">How many nearest neighbours are embedded and compared, 1 to 256.</param>
    /// <param name="failOpenOnEmbeddingFailure">Whether a failed embedding request counts the candidate as novel.</param>
    /// <param name="failOpenOnJudgeFailure">Whether an unusable judge answer counts the candidate as novel.</param>
    /// <param name="maxTrackedGenomes">How many programs may be compared against, 1 to 8192.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
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

    /// <summary>Gets the structural distance at or above which a candidate is novel.</summary>
    public double StructuralNoveltyThreshold { get; }

    /// <summary>Gets the cosine similarity below which a candidate is novel.</summary>
    public double EmbeddingSimilarityThreshold { get; }

    /// <summary>Gets how many nearest neighbours are compared by embedding.</summary>
    public int MaxEmbeddingComparisons { get; }

    /// <summary>Gets whether a failed embedding request counts the candidate as novel.</summary>
    public bool FailOpenOnEmbeddingFailure { get; }

    /// <summary>Gets whether an unusable judge answer counts the candidate as novel.</summary>
    public bool FailOpenOnJudgeFailure { get; }

    /// <summary>Gets how many programs may be compared against.</summary>
    public int MaxTrackedGenomes { get; }

    /// <summary>Encodes every setting for version hashes.</summary>
    /// <returns>A stable, culture-invariant string.</returns>
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
