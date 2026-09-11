namespace AiDotNet.Evolution;

/// <summary>Uniformly samples the retained feasible front, with distinct uniformly sampled inspirations.</summary>
public sealed class ParetoEvolutionSelectionPolicy<TGenome> : ISelectionPolicy<TGenome>
{
    private readonly UniformEvolutionSelectionPolicy<TGenome> _uniform = new();
    /// <inheritdoc/>
    public string Id => "pareto-uniform-front";
    /// <inheritdoc/>
    public string VersionHash => "pareto-uniform-front-v1";
    /// <inheritdoc/>
    public EvolutionSelection<TGenome>? Select(IEvolutionArchive<TGenome> archive, StableRandom random, int inspirationCount)
    {
        Guard.NotNull(archive);
        if ((archive as IEvolutionParetoArchiveView<TGenome>)?.ParetoDefinition is null)
            throw new ArgumentException("Front selection requires a Pareto archive.", nameof(archive));
        return _uniform.Select(archive, random, inspirationCount);
    }
}
