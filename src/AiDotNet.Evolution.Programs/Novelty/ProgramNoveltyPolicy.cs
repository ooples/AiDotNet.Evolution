// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/Novelty/ProgramNoveltyPolicy.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.Globalization;

namespace AiDotNet.Evolution.Programs.Novelty;

public sealed class ProgramNoveltyPolicy
{
    private readonly EmbeddingNoveltyOptions _options;
    private readonly IGenomeDistance<ProgramGenome> _structuralDistance;
    private readonly EmbeddingCosineGenomeDistance? _embeddingDistance;
    private readonly IProgramNoveltyJudge? _judge;

    private long _decisions;
    private long _structuralComparisons;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string _identity;
    private long _judgeRequests;
    private long _freeDecisions;

    public ProgramNoveltyPolicy(
        EmbeddingNoveltyOptions? options = null,
        IGenomeDistance<ProgramGenome>? structuralDistance = null,
        IProgramEmbeddingClient? embeddingClient = null,
        IProgramNoveltyJudge? judge = null)
    {
        _options = options ?? new EmbeddingNoveltyOptions();
        _structuralDistance = structuralDistance ?? new ProgramTokenSetDistance();
        _embeddingDistance = embeddingClient is null
            ? null
            : new EmbeddingCosineGenomeDistance(embeddingClient, _structuralDistance);
        _judge = judge;
        _identity = CaptureIdentity();
        VersionHash = "program-novelty-policy-v2-" + EvolutionHash.Combine(new[]
        {
            _options.ToVersionString(),
            _identity
        });
    }

    public EmbeddingNoveltyOptions Options => _options;

    public IGenomeDistance<ProgramGenome> StructuralDistance => _structuralDistance;

    public bool HasEmbeddingStage => _embeddingDistance is not null;

    public bool HasJudgeStage => _judge is not null;

    public long Decisions => Interlocked.Read(ref _decisions);

    public long StructuralComparisons => Interlocked.Read(ref _structuralComparisons);

    public long EmbeddingRequests => _embeddingDistance?.PrimeRequests ?? 0;

    public long JudgeRequests => Interlocked.Read(ref _judgeRequests);

    public long FreeDecisions => Interlocked.Read(ref _freeDecisions);

    public string VersionHash { get; }

    public async ValueTask<ProgramNoveltyDecision> EvaluateAsync(
        ProgramGenome candidate,
        IReadOnlyList<ProgramGenome> known,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(known);
        if (known.Count > _options.MaxTrackedGenomes) throw new ArgumentException("Known set exceeds its configured bound.", nameof(known));
        var snapshot = known.Take(_options.MaxTrackedGenomes + 1).ToArray();
        if (snapshot.Length > _options.MaxTrackedGenomes) throw new ArgumentException("Known set exceeds its configured bound.", nameof(known));
        if (snapshot.Any(g => g is null)) throw new ArgumentException("Known genomes cannot be null.", nameof(known));
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureIdentity();
            var decision = await EvaluateCoreAsync(candidate, snapshot, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); EnsureIdentity();
            if (decision.WasFree) Interlocked.Increment(ref _freeDecisions);
            return decision;
        }
        finally { _serial.Release(); }
    }

    private async ValueTask<ProgramNoveltyDecision> EvaluateCoreAsync(
        ProgramGenome candidate, IReadOnlyList<ProgramGenome> known, CancellationToken cancellationToken)
    {
        ProgramGuard.NotNull(candidate);
        ProgramGuard.NotNull(known);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _decisions);

        if (known.Count == 0)
        {
            return new ProgramNoveltyDecision(
                isNovel: true,
                decidedBy: ProgramNoveltyStage.None,
                reason: "nothing to compare against");
        }

        var duplicate = known.FirstOrDefault(g => g.Id == candidate.Id);
        if (duplicate is not null)
            return new ProgramNoveltyDecision(false, ProgramNoveltyStage.Structural, "exact source and language already known",
                duplicate.Id, nearestStructuralDistance: 0);
        var ranked = new List<Neighbour>(known.Count);
        for (int index = 0; index < known.Count; index++)
        {
            ProgramGenome other = known[index];
            if (other is null)
            {
                throw new ArgumentException("The known set cannot contain a null genome.", nameof(known));
            }

            cancellationToken.ThrowIfCancellationRequested();
            double distance = candidate.Language == other.Language ? _structuralDistance.Distance(candidate, other) : 1;
            if (!double.IsFinite(distance) || distance is < 0 or > 1)
                throw new InvalidOperationException("Structural novelty distance must be finite and within [0,1].");
            ranked.Add(new Neighbour(other, distance, index));
        }

        Interlocked.Add(ref _structuralComparisons, known.Count);

        // Ordering by distance and then by arrival index keeps the choice of nearest neighbour deterministic when
        // several are equidistant, which a replayed run depends on.
        ranked.Sort(static (left, right) =>
        {
            int byDistance = left.Distance.CompareTo(right.Distance);
            return byDistance != 0 ? byDistance : left.Order.CompareTo(right.Order);
        });

        Neighbour nearest = ranked[0];
        if (_options.StructuralNoveltyThreshold > 0 && nearest.Distance >= _options.StructuralNoveltyThreshold)
        {
            return new ProgramNoveltyDecision(
                isNovel: true,
                decidedBy: ProgramNoveltyStage.Structural,
                reason: "nearest structural distance " + Format(nearest.Distance) + " reached the threshold " +
                    Format(_options.StructuralNoveltyThreshold),
                nearestGenomeId: nearest.Genome.Id,
                nearestStructuralDistance: nearest.Distance,
                structuralComparisons: known.Count);
        }

        if (_embeddingDistance is null && _judge is null)
        {
            return new ProgramNoveltyDecision(
                isNovel: _options.StructuralNoveltyThreshold == 0,
                decidedBy: ProgramNoveltyStage.Structural,
                reason: _options.StructuralNoveltyThreshold == 0 ? "structural screening disabled; no optional stages configured" :
                    "nearest structural distance " + Format(nearest.Distance) + " is below the threshold " + Format(_options.StructuralNoveltyThreshold),
                nearestGenomeId: nearest.Genome.Id,
                nearestStructuralDistance: nearest.Distance,
                structuralComparisons: known.Count);
        }

        int embeddingRequests = 0;
        double? bestSimilarity = null;
        Neighbour mostSimilar = nearest;

        if (_embeddingDistance is not null)
        {
            var batch = new List<ProgramGenome> { candidate };
            int comparisons = Math.Min(_options.MaxEmbeddingComparisons, ranked.Count);
            for (int index = 0; index < comparisons; index++) batch.Add(ranked[index].Genome);

            var receipt = await _embeddingDistance
                .PrimeWithReceiptAsync(batch, cancellationToken)
                .ConfigureAwait(false);
            embeddingRequests = receipt.Requests;

            if (!receipt.Success)
            {
                return new ProgramNoveltyDecision(
                    isNovel: _options.FailOpenOnEmbeddingFailure,
                    decidedBy: ProgramNoveltyStage.Embedding,
                    reason: _options.FailOpenOnEmbeddingFailure
                        ? "the embedding provider was unavailable and the policy fails open"
                        : "the embedding provider was unavailable and the policy fails closed",
                    nearestGenomeId: nearest.Genome.Id,
                    nearestStructuralDistance: nearest.Distance,
                    structuralComparisons: known.Count,
                    embeddingRequests: embeddingRequests);
            }

            for (int index = 0; index < comparisons; index++)
            {
                double? similarity = _embeddingDistance.Similarity(candidate, ranked[index].Genome);
                if (similarity is not { } value) continue;
                if (bestSimilarity is null || value > bestSimilarity.Value)
                {
                    bestSimilarity = value;
                    mostSimilar = ranked[index];
                }
            }

            if (bestSimilarity is { } best && best < _options.EmbeddingSimilarityThreshold)
            {
                return new ProgramNoveltyDecision(
                    isNovel: true,
                    decidedBy: ProgramNoveltyStage.Embedding,
                    reason: "highest cosine similarity " + Format(best) + " is below the threshold " +
                        Format(_options.EmbeddingSimilarityThreshold),
                    nearestGenomeId: mostSimilar.Genome.Id,
                    nearestStructuralDistance: nearest.Distance,
                    embeddingSimilarity: best,
                    structuralComparisons: known.Count,
                    embeddingRequests: embeddingRequests);
            }
        }

        if (_judge is null)
        {
            return new ProgramNoveltyDecision(
                isNovel: false,
                decidedBy: ProgramNoveltyStage.Embedding,
                reason: bestSimilarity is { } similarity
                    ? "highest cosine similarity " + Format(similarity) + " reached the threshold " +
                        Format(_options.EmbeddingSimilarityThreshold)
                    : "no embedding comparison was possible and no judge is configured",
                nearestGenomeId: mostSimilar.Genome.Id,
                nearestStructuralDistance: nearest.Distance,
                embeddingSimilarity: bestSimilarity,
                structuralComparisons: known.Count,
                embeddingRequests: embeddingRequests);
        }

        Interlocked.Increment(ref _judgeRequests);
        ProgramNoveltyVerdict verdict;
        try { verdict = await _judge.JudgeAsync(candidate, mostSimilar.Genome, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        { verdict = ProgramNoveltyVerdict.Unavailable; }

        bool isNovel = verdict switch
        {
            ProgramNoveltyVerdict.Novel => true,
            ProgramNoveltyVerdict.NotNovel => false,
            _ => _options.FailOpenOnJudgeFailure
        };

        string judgeReason = verdict switch
        {
            ProgramNoveltyVerdict.Novel => "the judge found the change meaningful",
            ProgramNoveltyVerdict.NotNovel => "the judge found the change trivial",
            _ => _options.FailOpenOnJudgeFailure
                ? "the judge gave no usable answer and the policy fails open"
                : "the judge gave no usable answer and the policy fails closed"
        };

        return new ProgramNoveltyDecision(
            isNovel: isNovel,
            decidedBy: ProgramNoveltyStage.LanguageModel,
            reason: judgeReason,
            nearestGenomeId: mostSimilar.Genome.Id,
            nearestStructuralDistance: nearest.Distance,
            embeddingSimilarity: bestSimilarity,
            structuralComparisons: known.Count,
            embeddingRequests: embeddingRequests,
            judgeRequests: 1);
    }

    internal void EnsureIdentity()
    {
        _embeddingDistance?.EnsureIdentity();
        if (CaptureIdentity() != _identity) throw new InvalidOperationException("Novelty dependency identity changed.");
    }

    private string CaptureIdentity()
    {
        var parts = new[] { _structuralDistance.Id, _structuralDistance.VersionHash,
            _embeddingDistance?.VersionHash ?? "no-embedding", _judge?.Id ?? "no-judge", _judge?.VersionHash ?? "no-judge" };
        foreach (string part in parts) ProgramGuard.NotNullOrWhiteSpace(part);
        return EvolutionHash.Combine(parts);
    }

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private readonly struct Neighbour
    {
        internal Neighbour(ProgramGenome genome, double distance, int order)
        {
            Genome = genome;
            Distance = distance;
            Order = order;
        }

        internal ProgramGenome Genome { get; }

        internal double Distance { get; }

        internal int Order { get; }
    }
}
