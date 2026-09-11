namespace AiDotNet.Evolution;

/// <summary>Uniformly samples the feasible front, with an explicit optional probability of exploring the separate infeasible pool.</summary>
public sealed class ParetoEvolutionSelectionPolicy<TGenome> : IInfeasibleExplorationSelectionPolicy<TGenome>
{
    private readonly UniformEvolutionSelectionPolicy<TGenome> _uniform = new();
    /// <summary>Creates a policy; zero uses infeasible candidates only as a fallback while the feasible front is empty.</summary>
    public ParetoEvolutionSelectionPolicy(double infeasibleSelectionProbability = 0)
    {
        if (!EvolutionDescriptorDefinition.IsFinite(infeasibleSelectionProbability) || infeasibleSelectionProbability < 0 || infeasibleSelectionProbability > 1)
            throw new ArgumentOutOfRangeException(nameof(infeasibleSelectionProbability));
        InfeasibleSelectionProbability = infeasibleSelectionProbability;
    }
    /// <summary>Gets the probability of sampling a nonempty exploration pool while a feasible front also exists.</summary>
    public double InfeasibleSelectionProbability { get; }
    /// <inheritdoc/>
    public string Id => "pareto-uniform-front";
    /// <inheritdoc/>
    public string VersionHash => InfeasibleSelectionProbability == 0 ? "pareto-uniform-front-v1" :
        EvolutionHash.Combine(new[] { "pareto-uniform-infeasible-sampling-v1", EvolutionHash.EncodeDouble(InfeasibleSelectionProbability) });
    /// <inheritdoc/>
    public EvolutionSelection<TGenome>? Select(IEvolutionArchive<TGenome> archive, StableRandom random, int inspirationCount)
    {
        Guard.NotNull(archive);
        if ((archive as IEvolutionParetoArchiveView<TGenome>)?.ParetoDefinition is null)
            throw new ArgumentException("Front selection requires a Pareto archive.", nameof(archive));
        Guard.NotNull(random);
        if (inspirationCount < 0) throw new ArgumentOutOfRangeException(nameof(inspirationCount));
        var pool = ((IEvolutionParetoArchiveView<TGenome>)archive).InfeasibleEntries;
        if (archive.Count > 0 && (pool is null || pool.Count == 0 || InfeasibleSelectionProbability == 0 ||
            random.NextDouble() >= InfeasibleSelectionProbability)) return _uniform.Select(archive, random, inspirationCount);
        if (pool is null || pool.Count == 0) return null;
        var candidates = pool.ToArray();
        for (int i = 0; i < Math.Min(inspirationCount + 1L, candidates.Length); i++)
        {
            int selected = random.NextInt(i, candidates.Length);
            var temporary = candidates[i]; candidates[i] = candidates[selected]; candidates[selected] = temporary;
        }
        return new EvolutionSelection<TGenome>(candidates[0], Array.AsReadOnly(candidates.Skip(1).Take(inspirationCount).ToArray()));
    }
}
