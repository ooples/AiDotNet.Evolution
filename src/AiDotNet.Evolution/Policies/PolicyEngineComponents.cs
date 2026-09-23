using System.Diagnostics.CodeAnalysis;
namespace AiDotNet.Evolution;

internal sealed class PolicyEngineMixture<TGenome> : IOutcomeAwareVariationOperator<TGenome>
{
    private readonly EvolutionSearchPolicy _policy;
    private readonly (ResourceMeteredVariationOperator<TGenome> Operator, int Weight)[] _operators;
    private readonly Dictionary<long, ResourceMeteredVariationOperator<TGenome>> _pending = new();
    private readonly int _totalWeight;

    internal PolicyEngineMixture(EvolutionSearchPolicy policy, EvolutionPolicyEngineOperator<TGenome>[] definitions,
        EvolutionResourceLedger ledger, string costUnitVersionHash, List<IDisposable> owned)
    {
        _policy = policy;
        var enabled = new List<(ResourceMeteredVariationOperator<TGenome>, int)>();
        foreach (var definition in definitions)
        {
            int weight = policy.OperatorWeights[definition.Id];
            if (weight == 0) continue;
            var source = definition.Create();
            if (source is IDisposable disposable) owned.Add(disposable);
            if (source.Id != definition.Id || source.VersionHash != definition.VersionHash)
                throw new InvalidOperationException("Policy operator factory changed its declared protocol.");
            enabled.Add((new ResourceMeteredVariationOperator<TGenome>(source, ledger,
                definition.MaximumProposalResources, costUnitVersionHash), weight));
        }
        _operators = enabled.ToArray();
        _totalWeight = _operators.Sum(item => item.Weight);
        VersionHash = EvolutionHash.Combine(new[] { "policy-mixture-adapter-v1", policy.Id }
            .Concat(_operators.Select(value => value.Operator.VersionHash)));
    }
    public string Id => "declarative-policy-mixture";
    public string VersionHash { get; }
    public string CaptureState()
    {
        if (_pending.Count != 0) throw new InvalidOperationException("Policy state requires a settled proposal boundary.");
        var states = _operators.Select(value => value.Operator.CaptureState()).ToArray();
        if (states.Any(value => value.Length > 65536)) throw new InvalidDataException("Policy backend state exceeds 65536 characters.");
        return System.Text.Json.JsonSerializer.Serialize(new { VersionHash, States = states }, EvolutionJson.Compact);
    }
    public void RestoreState(string state)
    {
        if (_pending.Count != 0 || state is null || state.Length > 16 * 65536 * 6 + 1024)
            throw new InvalidDataException("Invalid policy state boundary or payload size.");
        using var document = System.Text.Json.JsonDocument.Parse(state, new System.Text.Json.JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (!root.TryGetProperty("VersionHash", out var version) || version.GetString() != VersionHash ||
            !root.TryGetProperty("States", out var states) || states.ValueKind != System.Text.Json.JsonValueKind.Array ||
            states.GetArrayLength() != _operators.Length)
            throw new InvalidDataException("Policy state protocol differs.");
        var values = states.EnumerateArray().Select(value => value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null).ToArray();
        if (values.Any(value => value is null || value.Length > 65536)) throw new InvalidDataException("Invalid policy backend state.");
        // As with the underlying metered adapters, discard factory products if any backend restore fails.
        for (int index = 0; index < values.Length; index++) _operators[index].Operator.RestoreState(values[index]!);
    }
    public ValueTask<TGenome> ProposeAsync(EvolutionVariationContext<TGenome> context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pending.Count != 0) throw new InvalidOperationException("Policy trials require serialized proposal/outcome dispatch.");
        double draw = context.Random.NextDouble() * _totalWeight;
        var selected = _operators[_operators.Length - 1].Operator;
        foreach (var item in _operators)
        {
            draw -= item.Weight;
            if (draw < 0) { selected = item.Operator; break; }
        }
        _pending.Add(context.Generation, selected);
        // Remove alternate artifact access through the archive entries as well as the explicit context fields.
        var inspirations = _policy.Context == EvolutionPolicyContext.ParentOnly
            ? Array.Empty<EvolutionArchiveEntry<TGenome>>() : context.Inspirations.Take(2).Select(WithoutFeedback).ToArray();
        var artifacts = _policy.Context == EvolutionPolicyContext.FeedbackAndInspirations
            ? context.ParentArtifacts.Where(value => value.SizeBytes <= 4096).Take(4).ToArray()
            : Array.Empty<EvolutionArtifact>();
        var bounded = new EvolutionVariationContext<TGenome>(WithoutFeedback(context.Parent), inspirations,
            context.Random, context.Generation, context.Island, artifacts, archive: null);
        return selected.ProposeAsync(bounded, cancellationToken);
    }
    public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
    {
        if (!_pending.TryGetValue(evaluation.Lineage.Generation, out var selected)) return; // Initial seeds have no proposal owner.
        _pending.Remove(evaluation.Lineage.Generation);
        selected.Observe(evaluation, insertionResult);
    }

    private static EvolutionArchiveEntry<TGenome> WithoutFeedback(EvolutionArchiveEntry<TGenome> entry)
    {
        var value = entry.Evaluation;
        var stripped = new EvolutionEvaluation(value.EvaluationId, value.GenomeId, value.Status, value.Quality, value.Direction,
            value.Descriptors, value.Objectives, value.ConstraintViolations, value.Cost, value.Lineage, value.CacheStatus,
            Array.Empty<EvolutionDiagnostic>(), value.TaskVersionHash, value.EvaluatorVersionHash, value.ConfigurationHash,
            value.Metrics, Array.Empty<EvolutionArtifact>());
        if (value.MeasurementOrigin is { } origin) stripped = stripped.WithMeasurementOrigin(origin);
        return new EvolutionArchiveEntry<TGenome>(entry.Cell, entry.Candidate, stripped);
    }
}

internal sealed class PolicyEngineSelection<TGenome> : ISelectionPolicy<TGenome>
{
    private readonly EvolutionSearchPolicy _policy;
    private readonly UniformEvolutionSelectionPolicy<TGenome> _uniform = new();
    private readonly int _switchAfter;
    private long _allocated;
    internal PolicyEngineSelection(EvolutionSearchPolicy policy, int maximumProposals, long alreadyAllocated)
    { _policy = policy; _switchAfter = maximumProposals / 2; _allocated = alreadyAllocated; }
    public string Id => "declarative-policy-selection";
    public string VersionHash => _policy.Id;
    public EvolutionSelection<TGenome>? Select(IEvolutionArchive<TGenome> archive, StableRandom random, int inspirationCount)
    {
        bool greedy = _policy.Selection == EvolutionPolicySelectionSchedule.Greedy ||
            _policy.Selection == EvolutionPolicySelectionSchedule.ExploreThenExploit && _allocated >= _switchAfter;
        _allocated++;
        if (!greedy) return _uniform.Select(archive, random, inspirationCount);
        var parent = archive.Best;
        if (parent is null) return null;
        var available = archive.Entries.Where(value => value.Candidate.CanonicalGenome.Id != parent.Candidate.CanonicalGenome.Id).ToList();
        var inspirations = new List<EvolutionArchiveEntry<TGenome>>();
        while (inspirations.Count < Math.Min(2, inspirationCount) && available.Count != 0)
        {
            int index = random.NextInt(available.Count);
            inspirations.Add(available[index]); available.RemoveAt(index);
        }
        return new EvolutionSelection<TGenome>(parent, inspirations);
    }
}

internal static class PolicyOwnedResources
{
    internal static async Task<T> RunAsync<T>(Func<List<IDisposable>, Task<T>> run)
    {
        var owned = new List<IDisposable>();
        T result = default!;
        Exception? failure = null;
        try { result = await run(owned).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        var disposalFailures = new List<Exception>();
        for (int i = owned.Count - 1; i >= 0; i--)
        {
            try { owned[i].Dispose(); }
            catch (Exception error) { disposalFailures.Add(error); }
        }
        if (disposalFailures.Count != 0)
        {
            if (failure is not null) disposalFailures.Insert(0, failure);
            throw new AggregateException(disposalFailures);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }
}
