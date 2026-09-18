namespace AiDotNet.Evolution;

/// <summary>Migrates diverse feasible front members, never scalar top-k, along an explicit island topology.</summary>
public sealed class ParetoEvolutionMigrationPolicy<TGenome> : IMigrationPolicy<TGenome>
{
    private readonly TopologyMigrationPolicy<TGenome> _settings;
    /// <summary>Creates a deterministic crowding-priority migration policy.</summary>
    public ParetoEvolutionMigrationPolicy(EvolutionMigrationTopology topology = EvolutionMigrationTopology.Ring,
        double migrationRate = 0, bool preventRepeatedMigration = false) =>
        _settings = new TopologyMigrationPolicy<TGenome>(topology, migrationRate, preventRepeatedMigration);
    /// <inheritdoc/>
    public string Id => "pareto-diverse-topology";
    /// <inheritdoc/>
    public string VersionHash => EvolutionHash.Combine(new[] { "pareto-crowding-migration-v1", _settings.VersionHash });
    /// <inheritdoc/>
    public IReadOnlyList<EvolutionMigration<TGenome>> CreateMigrations(IReadOnlyList<IEvolutionArchiveView<TGenome>> islands,
        int migrantsPerIsland, StableRandom random)
    {
        Guard.NotNull(islands); Guard.NotNull(random); Guard.Positive(migrantsPerIsland);
        var result = new List<EvolutionMigration<TGenome>>();
        for (int source = 0; source < islands.Count; source++)
        {
            var archive = islands[source] ?? throw new ArgumentException("Island cannot be null.", nameof(islands));
            var definition = (archive as IEvolutionParetoArchiveView<TGenome>)?.ParetoDefinition ??
                throw new ArgumentException("Front migration requires Pareto islands.", nameof(islands));
            if (islands.Count < 2) continue;
            var eligible = archive.Entries.Where(entry => !_settings.PreventsRepeatedMigration || !entry.Evaluation.Lineage.IsMigrant).ToArray();
            int count = TopologyMigrationPolicy<TGenome>.ResolveMigrantCount(eligible.Length, migrantsPerIsland, _settings.MigrationRate);
            foreach (var entry in EvolutionParetoFront<TGenome>.DiverseOrder(definition, eligible).Take(count))
                foreach (int destination in TopologyMigrationPolicy<TGenome>.DestinationsFor(_settings.Topology, source, islands.Count))
                    result.Add(new EvolutionMigration<TGenome>(source, destination, entry));
        }
        return result.AsReadOnly();
    }
}
