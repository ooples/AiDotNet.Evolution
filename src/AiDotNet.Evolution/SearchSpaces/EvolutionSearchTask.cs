namespace AiDotNet.Evolution;

/// <summary>Adapts a parameter-space objective without requiring a custom genome, canonicalizer or codec.</summary>
public sealed class EvolutionSearchTask : IEvolutionTask<EvolutionSearchGenome>
{
    private readonly EvolutionSearchSpace _space;
    private readonly Func<EvolutionSearchGenome, EvolutionEvaluationContext, CancellationToken, ValueTask<EvolutionTaskResult>> _evaluate;
    /// <summary>Creates a task with explicit domain/data versions and a domain-specific objective.</summary>
    public EvolutionSearchTask(EvolutionSearchSpace space, string id, string taskVersionHash, string evaluatorVersionHash,
        Func<EvolutionSearchGenome, EvolutionEvaluationContext, CancellationToken, ValueTask<EvolutionTaskResult>> evaluate)
    {
        Guard.NotNull(space); Guard.NotNull(evaluate); Guard.NotNullOrWhiteSpace(id);
        Guard.NotNullOrWhiteSpace(taskVersionHash); Guard.NotNullOrWhiteSpace(evaluatorVersionHash);
        _space = space; _evaluate = evaluate; Id = id;
        VersionHash = EvolutionHash.Combine(new[] { "typed-search-task-v1", space.VersionHash, taskVersionHash });
        EvaluatorVersionHash = evaluatorVersionHash;
    }
    /// <inheritdoc/>
    public string Id { get; }
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <inheritdoc/>
    public string EvaluatorVersionHash { get; }
    /// <inheritdoc/>
    public ValueTask<EvolutionCanonicalGenome<EvolutionSearchGenome>> CanonicalizeAsync(EvolutionSearchGenome genome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EvolutionSearchGenome owned = _space.Validate(genome);
        return new(new EvolutionCanonicalGenome<EvolutionSearchGenome>(owned, owned.Identity));
    }
    /// <inheritdoc/>
    public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<EvolutionSearchGenome> candidate, EvolutionEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(candidate); Guard.NotNull(context); cancellationToken.ThrowIfCancellationRequested();
        return _evaluate(_space.Validate(candidate.CanonicalGenome.Genome), context, cancellationToken);
    }
}
