// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/Novelty/NoveltyGatingProgramFitnessEvaluator.cs
// Original license retained in Programs/Legacy/AIDOTNET-LICENSE.txt.
using System.Globalization;

namespace AiDotNet.Evolution.Programs.Novelty;

public sealed class NoveltyGatingProgramFitnessEvaluator : IProgramFitnessEvaluator
{
    public const string RejectionCode = "program_not_novel";

    private readonly IProgramFitnessEvaluator _inner;
    private readonly ProgramNoveltyPolicy _policy;
    private readonly List<ProgramGenome> _accepted = new();
    private readonly HashSet<string> _acceptedIds = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string _innerIdentity;
    private long _trackedCharacters;

    private long _acceptedCount;
    private long _rejectedCount;
    private ProgramNoveltyDecision? _lastDecision;

    public NoveltyGatingProgramFitnessEvaluator(
        IProgramFitnessEvaluator inner,
        ProgramNoveltyPolicy? policy = null,
        string id = "novelty-gating-program-evaluator",
        ProgramNoveltyEnforcement enforcement = ProgramNoveltyEnforcement.Reject)
    {
        if (!Enum.IsDefined(typeof(ProgramNoveltyEnforcement), enforcement)) throw new ArgumentOutOfRangeException(nameof(enforcement));
        Enforcement = enforcement;
        ProgramGuard.NotNull(inner);
        ProgramGuard.NotNullOrWhiteSpace(id);

        _inner = inner;
        _innerIdentity = EvolutionHash.Combine(new[] { inner.Id, inner.VersionHash });
        _policy = policy ?? new ProgramNoveltyPolicy();
        Id = id.Trim();
        VersionHash = "novelty-gate-" + EvolutionHash.Combine(new[]
        {
            "novelty-gating-program-evaluator-v2",
            inner.Id,
            inner.VersionHash,
            _policy.VersionHash
        }.Concat(enforcement == ProgramNoveltyEnforcement.Advise ? new[] { "enforcement-advise" } : Array.Empty<string>()));
    }

    /// <summary>Gets whether a not-novel candidate is rejected or only annotated.</summary>
    public ProgramNoveltyEnforcement Enforcement { get; }

    /// <summary>The diagnostic attached, in <see cref="ProgramNoveltyEnforcement.Advise"/> mode, to a candidate the policy judged not novel.</summary>
    public const string SimilarityCode = "program_similar";

    /// <summary>Gets candidates judged not novel but still evaluated because enforcement is advisory.</summary>
    public long AdvisedCount => Interlocked.Read(ref _advisedCount);

    private long _advisedCount;

    public string Id { get; }

    public string VersionHash { get; }

    public IProgramFitnessEvaluator Inner => _inner;

    public ProgramNoveltyPolicy Policy => _policy;

    public long AcceptedCount => Interlocked.Read(ref _acceptedCount);

    public long RejectedCount => Interlocked.Read(ref _rejectedCount);

    public int TrackedCount
    {
        get
        {
            lock (_gate) return _accepted.Count;
        }
    }

    public ProgramNoveltyDecision? GetLastDecision()
    {
        lock (_gate) return _lastDecision;
    }

    public void Remember(ProgramGenome genome)
    {
        ProgramGuard.NotNull(genome);
        if (!_serial.Wait(0)) throw new InvalidOperationException("Cannot change remembered genomes during evaluation.");
        try { lock (_gate) Track(genome); }
        finally { _serial.Release(); }
    }

    /// <summary>Owned snapshot for caller-managed seed/checkpoint restoration; no engine archive is implied.</summary>
    public IReadOnlyList<ProgramGenome> GetRememberedGenomes()
    { lock (_gate) return Array.AsReadOnly(_accepted.ToArray()); }

    public void Reset()
    {
        if (!_serial.Wait(0)) throw new InvalidOperationException("Cannot reset during evaluation.");
        try { lock (_gate) { _accepted.Clear(); _acceptedIds.Clear(); _trackedCharacters = 0; _lastDecision = null; } }
        finally { _serial.Release(); }
    }

    public async ValueTask<EvolutionTaskResult> EvaluateAsync(
        ProgramGenome candidate,
        EvolutionEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        ProgramGuard.NotNull(candidate); ProgramGuard.NotNull(context);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { EnsureIdentity(); return await EvaluateCoreAsync(candidate, context, cancellationToken).ConfigureAwait(false); }
        finally { _serial.Release(); }
    }

    private async ValueTask<EvolutionTaskResult> EvaluateCoreAsync(
        ProgramGenome candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken)
    {
        ProgramGuard.NotNull(candidate);
        ProgramGuard.NotNull(context);

        ProgramGenome[] known;
        lock (_gate) known = _accepted.ToArray();

        ProgramNoveltyDecision decision = await _policy
            .EvaluateAsync(candidate, known, cancellationToken)
            .ConfigureAwait(false);

        lock (_gate) _lastDecision = decision;

        bool advisory = !decision.IsNovel && Enforcement == ProgramNoveltyEnforcement.Advise;
        if (advisory) Interlocked.Increment(ref _advisedCount);
        if (!decision.IsNovel && !advisory)
        {
            Interlocked.Increment(ref _rejectedCount);
            return new EvolutionTaskResult(
                EvolutionEvaluationStatus.Rejected,
                costUnits: decision.CostUnits,
                diagnostics: new[] { new EvolutionDiagnostic(RejectionCode, BuildRejectionMessage(decision)) });
        }

        Interlocked.Increment(ref _acceptedCount);
        cancellationToken.ThrowIfCancellationRequested(); EnsureIdentity();
        var measured = await _inner.EvaluateAsync(candidate, context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested(); EnsureIdentity();
        if (measured is null) throw new InvalidOperationException("The inner evaluator returned no work receipt.");
        if (measured.Status == EvolutionEvaluationStatus.Completed) { lock (_gate) Track(candidate); }
        // Similarity is a heuristic: an equivalent-looking program can still perform differently, so in advisory
        // mode it is measured and only annotated. Proven identity is the engine's canonical dedup, not this gate.
        IReadOnlyList<EvolutionDiagnostic> diagnostics = advisory
            ? measured.Diagnostics.Concat(new[] { new EvolutionDiagnostic(SimilarityCode, BuildRejectionMessage(decision)) }).ToArray()
            : measured.Diagnostics;
        var result = new EvolutionTaskResult(measured.Status, measured.Quality, measured.Direction,
            measured.Descriptors, measured.Objectives, measured.ConstraintViolations, measured.CostUnits + decision.CostUnits,
            diagnostics, measured.Metrics, measured.Artifacts);
        return measured.MeasurementOrigin is { } origin ? result.WithMeasurementOrigin(origin) : result;
    }

    private void EnsureIdentity()
    {
        _policy.EnsureIdentity();
        if (_innerIdentity != EvolutionHash.Combine(new[] { _inner.Id, _inner.VersionHash }))
            throw new InvalidOperationException("Inner evaluator identity changed.");
    }

    private static string BuildRejectionMessage(ProgramNoveltyDecision decision) =>
        "The candidate was turned away by the novelty gate at the " + decision.DecidedBy +
        " stage: " + decision.Reason + ". Structural comparisons: " +
        decision.StructuralComparisons.ToString(CultureInfo.InvariantCulture) +
        ", embedding requests: " + decision.EmbeddingRequests.ToString(CultureInfo.InvariantCulture) +
        ", judging requests: " + decision.JudgeRequests.ToString(CultureInfo.InvariantCulture) + ".";

    private void Track(ProgramGenome genome)
    {
        if (!_acceptedIds.Add(genome.Id)) return;
        _accepted.Add(genome);
        _trackedCharacters += genome.Source.Length;
        while (_accepted.Count > _policy.Options.MaxTrackedGenomes || _trackedCharacters > 16_777_216)
        {
            _trackedCharacters -= _accepted[0].Source.Length;
            _acceptedIds.Remove(_accepted[0].Id);
            _accepted.RemoveAt(0);
        }
    }
}
