// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/Novelty/ProgramNoveltyDecision.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>Whether a candidate program was judged novel, which stage decided, and what the decision cost.</summary>
public sealed class ProgramNoveltyDecision
{
    /// <summary>The longest kept reason; longer reasons are truncated.</summary>
    public const int MaxReasonLength = 240;

    /// <summary>Records a novelty decision.</summary>
    /// <param name="isNovel">Whether the candidate is novel.</param>
    /// <param name="decidedBy">The stage that made the decision.</param>
    /// <param name="reason">Why; truncated to <see cref="MaxReasonLength"/> characters.</param>
    /// <param name="nearestGenomeId">The closest existing program, if one was compared.</param>
    /// <param name="nearestStructuralDistance">Its structural distance, if computed.</param>
    /// <param name="embeddingSimilarity">Its embedding cosine similarity, if computed.</param>
    /// <param name="structuralComparisons">How many structural distances were computed.</param>
    /// <param name="embeddingRequests">How many embedding requests were sent.</param>
    /// <param name="judgeRequests">How many model-judge requests were sent.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative, a measurement is not finite, or the stage is undefined.</exception>
    public ProgramNoveltyDecision(
        bool isNovel,
        ProgramNoveltyStage decidedBy,
        string reason,
        string? nearestGenomeId = null,
        double? nearestStructuralDistance = null,
        double? embeddingSimilarity = null,
        int structuralComparisons = 0,
        int embeddingRequests = 0,
        int judgeRequests = 0)
    {
        ProgramGuard.NotNull(reason);
        if (!Enum.IsDefined(typeof(ProgramNoveltyStage), decidedBy))
        {
            throw new ArgumentOutOfRangeException(nameof(decidedBy), decidedBy, "Value must be a defined stage.");
        }

        RequireNonNegative(structuralComparisons, nameof(structuralComparisons));
        RequireNonNegative(embeddingRequests, nameof(embeddingRequests));
        RequireNonNegative(judgeRequests, nameof(judgeRequests));
        RequireFinite(nearestStructuralDistance, nameof(nearestStructuralDistance));
        RequireFinite(embeddingSimilarity, nameof(embeddingSimilarity));

        IsNovel = isNovel;
        DecidedBy = decidedBy;
        Reason = reason.Length <= MaxReasonLength ? reason : reason.Substring(0, MaxReasonLength);
        NearestGenomeId = nearestGenomeId;
        NearestStructuralDistance = nearestStructuralDistance;
        EmbeddingSimilarity = embeddingSimilarity;
        StructuralComparisons = structuralComparisons;
        EmbeddingRequests = embeddingRequests;
        JudgeRequests = judgeRequests;
    }

    /// <summary>Gets whether the candidate is novel.</summary>
    public bool IsNovel { get; }

    /// <summary>Gets the stage that decided.</summary>
    public ProgramNoveltyStage DecidedBy { get; }

    /// <summary>Gets why, at most <see cref="MaxReasonLength"/> characters.</summary>
    public string Reason { get; }

    /// <summary>Gets the closest existing program, or <c>null</c>.</summary>
    public string? NearestGenomeId { get; }

    /// <summary>Gets the structural distance to the closest program, or <c>null</c>.</summary>
    public double? NearestStructuralDistance { get; }

    /// <summary>Gets the embedding cosine similarity to the closest program, or <c>null</c>.</summary>
    public double? EmbeddingSimilarity { get; }

    /// <summary>Gets how many structural distances were computed.</summary>
    public int StructuralComparisons { get; }

    /// <summary>Gets how many embedding requests were sent.</summary>
    public int EmbeddingRequests { get; }

    /// <summary>Gets how many model-judge requests were sent.</summary>
    public int JudgeRequests { get; }

    /// <summary>Gets whether the decision needed no paid request.</summary>
    public bool WasFree => EmbeddingRequests == 0 && JudgeRequests == 0;

    /// <summary>One task work unit per embedding or judge request; not a monetary or token charge.</summary>
    public double CostUnits => (double)EmbeddingRequests + JudgeRequests;

    /// <summary>Summarises the verdict, deciding stage and request counts.</summary>
    /// <returns>A short description.</returns>
    public override string ToString() =>
        (IsNovel ? "novel" : "not-novel") + " by " + DecidedBy +
        " (embeddings=" + EmbeddingRequests.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        ", judgements=" + JudgeRequests.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";

    private static void RequireNonNegative(int value, string parameterName)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(parameterName, value, "Value cannot be negative.");
    }

    private static void RequireFinite(double? value, string parameterName)
    {
        if (value is { } number && (double.IsNaN(number) || double.IsInfinity(number)))
        {
            throw new ArgumentOutOfRangeException(parameterName, number, "Value must be finite.");
        }
    }
}
