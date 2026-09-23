// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/Novelty/ProgramNoveltyDecision.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.Programs.Novelty;

public sealed class ProgramNoveltyDecision
{
    public const int MaxReasonLength = 240;

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

    public bool IsNovel { get; }

    public ProgramNoveltyStage DecidedBy { get; }

    public string Reason { get; }

    public string? NearestGenomeId { get; }

    public double? NearestStructuralDistance { get; }

    public double? EmbeddingSimilarity { get; }

    public int StructuralComparisons { get; }

    public int EmbeddingRequests { get; }

    public int JudgeRequests { get; }

    public bool WasFree => EmbeddingRequests == 0 && JudgeRequests == 0;

    /// <summary>One task work unit per embedding or judge request; not a monetary or token charge.</summary>
    public double CostUnits => (double)EmbeddingRequests + JudgeRequests;

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
