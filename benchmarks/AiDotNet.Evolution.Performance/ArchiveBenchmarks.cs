using BenchmarkDotNet.Attributes;

namespace AiDotNet.Evolution.Performance;

/// <summary>Read-path scaling with an exact, prepopulated occupied-cell count.</summary>
[MemoryDiagnoser]
public class ArchiveBenchmarks
{
    private MapElitesArchive<EvolutionSearchGenome> _archive = null!;
    private EvolutionCellKey _key = null!;
    private StableRandom _random = null!;
    [Params(100, 1000, 10000)] public int Cells { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var space = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Real("x", 0, Cells)).Build();
        _archive = new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, Cells, Cells) });
        _key = new EvolutionCellKey(new[] { Cells / 2 }); _random = StableRandom.CreateStream(42, 0);
        for (int i = 0; i < Cells; i++)
        {
            double value = i + 0.5;
            EvolutionSearchGenome genome = space.CreateGenome(new Dictionary<string, EvolutionParameterValue> { ["x"] = EvolutionParameterValue.Numeric(value) });
            var lineage = new EvolutionLineage(null, null, "seed", null, i, 0, (ulong)i);
            var candidate = new EvolutionCandidate<EvolutionSearchGenome>(i, new EvolutionCanonicalGenome<EvolutionSearchGenome>(genome, genome.Identity), lineage);
            var evaluation = new EvolutionEvaluation(i, genome.Identity, EvolutionEvaluationStatus.Completed, value,
                EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["x"] = value }, Array.Empty<double>(), Array.Empty<double>(),
                new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
            _archive.TryAdd(candidate, evaluation);
        }
        if (_archive.Count != Cells) throw new InvalidOperationException("Archive fixture did not fill every declared cell.");
    }

    [Benchmark] public EvolutionArchiveEntry<EvolutionSearchGenome>? Lookup() => _archive.Get(_key);
    [Benchmark] public EvolutionArchiveEntry<EvolutionSearchGenome>? Sample() => _archive.Sample(_random);
    [Benchmark] public EvolutionArchiveSnapshot<EvolutionSearchGenome> Snapshot() => new(_archive);
}
