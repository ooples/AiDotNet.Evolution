namespace AiDotNet.Evolution;

/// <summary>Uniformly samples the entire retained feasible front with distinct uniformly sampled inspirations.</summary>
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
        if (archive.GetParetoDefinition() is null) throw new ArgumentException("Pareto selection requires an objective archive.", nameof(archive));
        return _uniform.Select(archive, random, inspirationCount);
    }
}
