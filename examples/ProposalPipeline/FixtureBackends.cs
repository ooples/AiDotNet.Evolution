using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AiDotNet.Evolution;

namespace ProposalPipeline;

internal sealed record Point(double X, double Y) : IImmutableEvolutionGenome<Point>
{
    public Point CreateOwnedSnapshot() => new(X, Y);
    public string Identity => EvolutionHash.Combine(new[] { "point-v1", EvolutionHash.EncodeDouble(X), EvolutionHash.EncodeDouble(Y) });
}

internal sealed record ProposalResponse(long Generation, string RequestIdentity, Point Value, decimal CostUnits);
internal sealed record EvaluationResponse(long EvaluationId, int Attempt, string RequestIdentity, double Quality, double X, double Y, double CostUnits);

/// <summary>Authored delay/receipt fixture, not a real model provider. Replay never calls a model or invents new responses.</summary>
internal sealed class FixtureProposalSource(string profile, IReadOnlyDictionary<long, ProposalResponse>? replay = null)
    : IDeterministicConcurrentCostedEvolutionProposalSource<Point>
{
    private readonly ConcurrentDictionary<long, ProposalResponse> _responses = new();
    private readonly List<long> _observed = new();
    private int _physical, _replayed;
    public string Id => "authored-point-proposals";
    public string VersionHash => "snapshot-local-point-proposals-v1";
    public bool SupportsDeterministicConcurrency => true;
    public int PhysicalCalls => Volatile.Read(ref _physical);
    public int ReplayedCalls => Volatile.Read(ref _replayed);
    public IReadOnlyList<long> Observed => _observed.AsReadOnly();
    public IReadOnlyDictionary<long, ProposalResponse> Responses => _responses;

    public async ValueTask<EvolutionResourceResult<Point>> ProposeAsync(EvolutionVariationContext<Point> context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string request = EvolutionHash.Combine(new[] { "fixture-proposal-request-v1", context.Generation.ToString(CultureInfo.InvariantCulture),
            context.Island.ToString(CultureInfo.InvariantCulture), context.Parent.Evaluation.GenomeId, context.ProposalIdentity ?? "legacy-context",
            context.Archive!.DefinitionHash, context.Archive.Version.ToString(CultureInfo.InvariantCulture) }
            .Concat(context.Inspirations.Select(entry => entry.Evaluation.GenomeId))
            .Concat(context.Archive.Entries.Select(entry => entry.Evaluation.GenomeId)));
        ProposalResponse response;
        if (replay is not null)
        {
            if (!replay.TryGetValue(context.Generation, out response!) || response.RequestIdentity != request)
                throw new InvalidOperationException("Replay proposal input does not match the recorded immutable context.");
            Interlocked.Increment(ref _replayed);
        }
        else
        {
            Interlocked.Increment(ref _physical);
            int delay = profile == "ProposalBound" ? 8 : profile == "Mixed" ? 1 + (int)(context.Generation % 5) * 2 : 0;
            if (delay > 0) await Task.Delay(delay, cancellationToken);
            Point parent = context.Parent.Candidate.CanonicalGenome.Genome;
            double dx = (context.Random.NextDouble() - 0.5) * 0.5, dy = (context.Random.NextDouble() - 0.5) * 0.5;
            var value = new Point(Math.Max(-1, Math.Min(1, parent.X + dx)), Math.Max(-1, Math.Min(1, parent.Y + dy)));
            response = new ProposalResponse(context.Generation, request, value, 0.25m);
        }
        if (!_responses.TryAdd(context.Generation, response)) throw new InvalidOperationException("Repeated proposal dispatch.");
        return new EvolutionResourceResult<Point>(response.Value, EvolutionResources.Of("cost_units", response.CostUnits));
    }

    public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) => _observed.Add(evaluation.Lineage.Generation);
    public string CaptureState() => JsonSerializer.Serialize(_observed);
    public void RestoreState(string state) { _observed.Clear(); _observed.AddRange(JsonSerializer.Deserialize<List<long>>(state) ?? throw new InvalidDataException("Missing feedback state.")); }
}

/// <summary>Real deterministic numeric objective with authored delays; replay uses only original recorded measurements.</summary>
internal sealed class FixtureTask(string family, string profile, IReadOnlyDictionary<long, EvaluationResponse>? replay = null) : IEvolutionTask<Point>
{
    private readonly ConcurrentDictionary<long, EvaluationResponse> _responses = new();
    private int _physical, _replayed;
    public string Id => "authored-point-task";
    public string VersionHash => "point-" + family + "-v1";
    public string EvaluatorVersionHash => VersionHash;
    public int PhysicalCalls => Volatile.Read(ref _physical);
    public int ReplayedCalls => Volatile.Read(ref _replayed);
    public IReadOnlyDictionary<long, EvaluationResponse> Responses => _responses;
    public ValueTask<EvolutionCanonicalGenome<Point>> CanonicalizeAsync(Point genome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(genome.X) || !double.IsFinite(genome.Y) || Math.Abs(genome.X) > 1 || Math.Abs(genome.Y) > 1)
            throw new ArgumentException("Fixture points must be finite and bounded.");
        return new(new EvolutionCanonicalGenome<Point>(genome, genome.Identity));
    }
    public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<Point> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string request = EvolutionHash.Combine(new[] { VersionHash, candidate.CanonicalGenome.Id, context.EvaluationId.ToString(CultureInfo.InvariantCulture),
            context.AttemptCount.ToString(CultureInfo.InvariantCulture), context.RootSeed.ToString(CultureInfo.InvariantCulture) });
        EvaluationResponse response;
        if (replay is not null)
        {
            if (!replay.TryGetValue(context.EvaluationId, out response!) || response.RequestIdentity != request || response.Attempt != context.AttemptCount)
                throw new InvalidOperationException("Replay evaluation input differs from the recorded request.");
            Interlocked.Increment(ref _replayed);
        }
        else
        {
            Interlocked.Increment(ref _physical);
            int delay = profile == "ProposalBound" ? 1 : profile == "Mixed" ? 1 + (int)(context.EvaluationId % 7) * 2 : 0;
            if (delay > 0) await Task.Delay(delay, cancellationToken);
            Point point = candidate.CanonicalGenome.Genome;
            double loss = point.X * point.X + point.Y * point.Y;
            if (family == "Rugged") loss += 0.2 * (2 - Math.Cos(10 * point.X) - Math.Cos(10 * point.Y));
            response = new EvaluationResponse(context.EvaluationId, context.AttemptCount, request, 1 / (1 + loss), point.X, point.Y, 1);
        }
        if (!_responses.TryAdd(context.EvaluationId, response)) throw new InvalidOperationException("Repeated evaluator dispatch.");
        return EvolutionTaskResult.Completed(response.Quality, new Dictionary<string, double> { ["x"] = response.X, ["y"] = response.Y }, costUnits: response.CostUnits);
    }
}
