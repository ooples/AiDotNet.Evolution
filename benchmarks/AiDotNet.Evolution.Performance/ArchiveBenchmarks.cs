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
    [Params(1, 8, 32)] public int Dimensions { get; set; } = 1;

    [GlobalSetup]
    public void Setup()
    {
        var builder = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Real("x", 0, Cells));
        var axes = new List<EvolutionDescriptorDefinition> { new("x", 0, Cells, Cells) };
        for (int dimension = 1; dimension < Dimensions; dimension++)
        {
            builder.Add(EvolutionParameter.Real("d" + dimension, 0, 1));
            axes.Add(new EvolutionDescriptorDefinition("d" + dimension, 0, 1, 1));
        }
        var space = builder.Build();
        _archive = new MapElitesArchive<EvolutionSearchGenome>(axes);
        _key = new EvolutionCellKey(new[] { Cells / 2 }.Concat(Enumerable.Repeat(0, Dimensions - 1))); _random = StableRandom.CreateStream(42, 0);
        for (int i = 0; i < Cells; i++)
        {
            double value = i + 0.5;
            var values = new Dictionary<string, EvolutionParameterValue> { ["x"] = EvolutionParameterValue.Numeric(value) };
            var descriptors = new Dictionary<string, double> { ["x"] = value };
            for (int dimension = 1; dimension < Dimensions; dimension++)
            {
                values.Add("d" + dimension, EvolutionParameterValue.Numeric(.5));
                descriptors.Add("d" + dimension, .5);
            }
            EvolutionSearchGenome genome = space.CreateGenome(values);
            var lineage = new EvolutionLineage(null, null, "seed", null, i, 0, (ulong)i);
            var candidate = new EvolutionCandidate<EvolutionSearchGenome>(i, new EvolutionCanonicalGenome<EvolutionSearchGenome>(genome, genome.Identity), lineage);
            var evaluation = new EvolutionEvaluation(i, genome.Identity, EvolutionEvaluationStatus.Completed, value,
                EvolutionOptimizationDirection.Maximize, descriptors, Array.Empty<double>(), Array.Empty<double>(),
                new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
            _archive.TryAdd(candidate, evaluation);
        }
        if (_archive.Count != Cells) throw new InvalidOperationException("Archive fixture did not fill every declared cell.");
    }

    [Benchmark] public EvolutionArchiveEntry<EvolutionSearchGenome>? Lookup() => _archive.Get(_key);
    [Benchmark] public EvolutionArchiveEntry<EvolutionSearchGenome>? Sample() => _archive.Sample(_random);
    [Benchmark] public EvolutionArchiveSnapshot<EvolutionSearchGenome> Snapshot() => new(_archive);
}
