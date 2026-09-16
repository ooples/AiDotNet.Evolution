namespace AiDotNet.Evolution;

/// <summary>Migrates crowded-front extremes and sparse tradeoffs around a ring without scalar-quality ranking.</summary>
public sealed class ParetoMigrationPolicy<TGenome> : IMigrationPolicy<TGenome>
{
    /// <inheritdoc/>
    public string Id => "pareto-crowding-ring";
    /// <inheritdoc/>
    public string VersionHash => "pareto-crowding-ring-v1";
    /// <inheritdoc/>
    public IReadOnlyList<EvolutionMigration<TGenome>> CreateMigrations(
        IReadOnlyList<IEvolutionArchiveView<TGenome>> islands, int migrantsPerIsland, StableRandom random)
    {
        Guard.NotNull(islands); Guard.NotNull(random); Guard.Positive(migrantsPerIsland);
        foreach (var island in islands)
            if (island.GetParetoDefinition() is null || island.DefinitionHash != islands[0].DefinitionHash)
                throw new ArgumentException("Compatible Pareto archives are required.", nameof(islands));
        var result = new List<EvolutionMigration<TGenome>>();
        if (islands.Count < 2) return result;
        for (int i = 0; i < islands.Count; i++)
            foreach (var entry in EvolutionParetoQuery.CrowdingOrder(islands[i].Entries, islands[i].GetParetoDefinition()!).Take(migrantsPerIsland))
                result.Add(new EvolutionMigration<TGenome>(i, (i + 1) % islands.Count, entry));
        return result;
    }
}
