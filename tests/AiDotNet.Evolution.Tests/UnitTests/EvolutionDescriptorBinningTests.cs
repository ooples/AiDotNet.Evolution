using System.Globalization;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// Covers <see cref="EvolutionDescriptorBinning"/>: logarithmic and explicit-edge bins, and the two adaptive modes
/// that learn their bins from the values an archive is offered, re-key the elites when the bins move, and carry what
/// they learned through a checkpoint.
/// </summary>
public sealed class EvolutionDescriptorBinningTests
{
    [Fact]
    public void LogarithmicBinsAreEvenInLogValue()
    {
        EvolutionDescriptorDefinition axis = EvolutionDescriptorDefinition.Logarithmic("x", 1, 1000, 3);

        // Boundaries at 10 and 100; linear bins over the same range would put everything below 334 in bin 0.
        Assert.Equal(new[] { 0, 1, 1, 2, 2 }, new[] { 9.9, 10.1, 99, 101, 1000 }.Select(value => Bin(axis, value)));
        Assert.False(axis.TryGetBin(0.5, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionDescriptorDefinition.Logarithmic("x", 0, 10, 2));
    }

    [Fact]
    public void ALogarithmicAxisGrowsByWholeBinsOfTheSameLogWidth()
    {
        EvolutionDescriptorDefinition axis =
            EvolutionDescriptorDefinition.Logarithmic("x", 1, 100, 2, EvolutionOutOfRangePolicy.Grow);

        EvolutionDescriptorDefinition grown = axis.Widen(500);

        Assert.Equal(EvolutionDescriptorBinning.Logarithmic, grown.Binning);
        Assert.Equal(3, grown.BinCount);
        Assert.Equal(1, grown.Minimum);
        Assert.Equal(1000, grown.Maximum, 9);
        Assert.Equal(2, Bin(grown, 500));
        Assert.Same(axis, axis.Widen(-1));
    }

    [Fact]
    public void ExplicitEdgesBinByTheGivenBoundaries()
    {
        EvolutionDescriptorDefinition axis = EvolutionDescriptorDefinition.WithEdges("x", new double[] { 0, 1, 10, 100 });

        Assert.Equal(3, axis.BinCount);
        Assert.Equal(new double[] { 0, 1, 10, 100 }, axis.Edges);
        Assert.Equal(new[] { 0, 1, 1, 2, 2 }, new[] { 0.5, 1, 9.99, 10, 100 }.Select(value => Bin(axis, value)));
        Assert.False(axis.TryGetBin(101, out _));
        Assert.Throws<ArgumentException>(() => EvolutionDescriptorDefinition.WithEdges("x", new double[] { 0, 1, 1 }));
        Assert.Throws<ArgumentException>(() => EvolutionDescriptorDefinition.WithEdges("x", new double[] { 0 }));
    }

    [Fact]
    public void OnlyLinearAndLogarithmicAxesCanGrow()
    {
        Assert.Throws<ArgumentException>(() => EvolutionDescriptorDefinition.WithEdges(
            "x", new double[] { 0, 1, 2 }, EvolutionOutOfRangePolicy.Grow));
        Assert.Throws<ArgumentException>(() => EvolutionDescriptorDefinition.AdaptiveMinMax(
            "x", 0, 1, 2, EvolutionOutOfRangePolicy.Grow));
        Assert.Throws<ArgumentException>(() => EvolutionDescriptorDefinition.AdaptiveQuantile(
            "x", 0, 1, 2, EvolutionOutOfRangePolicy.Grow));
    }

    [Fact]
    public void AFixedRangeFarWiderThanTheValuesKeepsOneProgramWhereAnAdaptiveOneKeepsThemAll()
    {
        // E-4's third pilot: program lengths of 400-550 characters on a 0-10,000 axis of 10 bins all fell in cell 0.
        double[] lengths = { 400, 430, 460, 490, 520, 550 };
        var fixedRange = new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 10000, 10) });
        var adaptive = new MapElitesArchive<TestGenome>(new[] { EvolutionDescriptorDefinition.AdaptiveMinMax("x", 0, 10000, 10) });

        for (int i = 0; i < lengths.Length; i++)
        {
            Add(fixedRange, i + 1, "p" + i, i, lengths[i]);
            Add(adaptive, i + 1, "p" + i, i, lengths[i]);
        }

        Assert.Equal(1, fixedRange.Count);
        Assert.Equal(6, adaptive.Count);
        Assert.Equal(new[] { "0", "2", "4", "6", "8", "9" }, lengths.Select((_, i) => Cell(adaptive, "p" + i)));
        Assert.Equal(400, adaptive.Descriptors[0].Minimum);
        Assert.Equal(550, adaptive.Descriptors[0].Maximum);
        Assert.Equal(6, adaptive.Descriptors[0].ObservationCount);
    }

    [Fact]
    public void WhenTheObservedRangeMovesEveryEliteIsReKeyedOntoTheNewBins()
    {
        var archive = new MapElitesArchive<TestGenome>(new[] { EvolutionDescriptorDefinition.AdaptiveMinMax("x", 0, 10000, 10) });

        Add(archive, 1, "a", 1, 5000);
        // One distinct value is not yet a range, so the initial bounds still apply.
        Assert.Equal("5", Cell(archive, "a"));
        Assert.Equal(10000, archive.Descriptors[0].Maximum);

        Add(archive, 2, "b", 2, 400);

        Assert.Equal("9", Cell(archive, "a"));
        Assert.Equal("0", Cell(archive, "b"));
    }

    [Fact]
    public void AReKeyThatPutsTwoElitesInOneCellKeepsTheBetterOne()
    {
        var archive = new MapElitesArchive<TestGenome>(new[] { EvolutionDescriptorDefinition.AdaptiveMinMax("x", 0, 100, 2) });
        Add(archive, 1, "low", 1, 10);
        Add(archive, 2, "high", 5, 60);
        Assert.Equal(2, archive.Count);

        // The range becomes [10, 1000]: 10 and 60 now share bin 0, and the candidate takes bin 1.
        Add(archive, 3, "far", 2, 1000);

        Assert.Equal(new[] { "far", "high" }, archive.Entries.Select(entry => entry.Evaluation.GenomeId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal("0", Cell(archive, "high"));
    }

    [Fact]
    public void QuantileEdgesSitAtTheObservedQuantiles()
    {
        EvolutionDescriptorDefinition axis = EvolutionDescriptorDefinition.AdaptiveQuantile("x", 0, 1, 4);
        foreach (int value in Enumerable.Range(1, 100).Reverse()) axis = axis.Observe(value);

        Assert.Equal(new double[] { 1, 26, 51, 76, 100 }, axis.Edges);
        Assert.Equal(new[] { 0, 0, 1, 3, 3 }, new double[] { 1, 25.9, 26, 76, 100 }.Select(value => Bin(axis, value)));
    }

    [Fact]
    public void QuantileBinsGiveBunchedValuesTheirOwnCells()
    {
        // Nine values bunched near 0 and one outlier: even bins over [0, 100] would hold the nine in one cell.
        double[] values = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 100 };
        var minMax = new MapElitesArchive<TestGenome>(new[] { EvolutionDescriptorDefinition.AdaptiveMinMax("x", 0, 1, 5) });
        var quantile = new MapElitesArchive<TestGenome>(new[] { EvolutionDescriptorDefinition.AdaptiveQuantile("x", 0, 1, 5) });
        for (int i = 0; i < values.Length; i++)
        {
            Add(minMax, i + 1, "p" + i, i, values[i]);
            Add(quantile, i + 1, "p" + i, i, values[i]);
        }

        // Final quantile edges are {1, 3, 5, 7, 9, 100}, five bins with values in each, but only three elites remain:
        // while the edges settled, each re-key merged neighbouring cells and the weaker elite of each pair was dropped.
        Assert.Equal(2, minMax.Count);
        Assert.Equal(3, quantile.Count);
        Assert.Equal(new double[] { 1, 3, 5, 7, 9, 100 }, quantile.Descriptors[0].Edges);
    }

    [Fact]
    public void BeyondItsCapacityTheQuantileSampleIsCompactedDeterministically()
    {
        EvolutionDescriptorDefinition axis = EvolutionDescriptorDefinition.AdaptiveQuantile("x", 0, 1, 2, sampleCapacity: 4);
        foreach (int value in Enumerable.Range(1, 10)) axis = axis.Observe(value);

        // Halved to {1, 3, 5} on the fifth value, then only every second value is kept: 7 and 9, halved to {1, 5, 9}.
        Assert.Equal(new double[] { 1, 5, 9 }, axis.Sample);
        Assert.Equal(4, axis.SampleStride);
        Assert.Equal(10, axis.ObservationCount);
        Assert.Equal(new double[] { 1, 5, 10 }, axis.Edges);
    }

    [Fact]
    public void AnAxisIsObservedOnlyWhenTheCandidateCanBePlaced()
    {
        var archive = new MapElitesArchive<TestGenome>(new[]
        {
            EvolutionDescriptorDefinition.AdaptiveMinMax("x", 0, 1, 2),
            new EvolutionDescriptorDefinition("y", 0, 1, 2)
        });
        (EvolutionCandidate<TestGenome> candidate, EvolutionEvaluation evaluation) =
            MapElitesArchiveTests.Create(1, "a", 1, 50);

        Assert.Equal(EvolutionArchiveInsertionResult.Rejected, archive.TryAdd(candidate, evaluation));
        Assert.Equal(0, archive.Descriptors[0].ObservationCount);
    }

    [Fact]
    public void RemeasuredValuesAreObservedButTheReplayedElitesAreNot()
    {
        var archive = new MapElitesArchive<TestGenome>(new[] { EvolutionDescriptorDefinition.AdaptiveQuantile("x", 0, 1, 2) });
        Add(archive, 1, "a", 1, 1);
        Add(archive, 2, "b", 2, 2);

        archive.Remeasure(genome => new Dictionary<string, double> { ["x"] = genome.Value * 100.0 });

        Assert.Equal(4, archive.Descriptors[0].ObservationCount);
        Assert.Equal(new double[] { 1, 2, 100, 200 }, archive.Descriptors[0].Sample);
        Assert.Equal(200, archive.Descriptors[0].Maximum);
    }

    [Fact]
    public void ALinearAxisKeepsItsCanonicalFormSoExistingHashesStayValid()
    {
        Assert.Equal(
            string.Join("|", "1:x", EvolutionHash.EncodeDouble(0), EvolutionHash.EncodeDouble(1), "4", "0"),
            new EvolutionDescriptorDefinition("x", 0, 1, 4).ToCanonicalString());
        Assert.NotEqual(
            EvolutionDescriptorDefinition.AdaptiveMinMax("x", 0, 1, 4, EvolutionOutOfRangePolicy.Reject).ToCanonicalString(),
            new EvolutionDescriptorDefinition("x", 0, 1, 4).ToCanonicalString());
    }

    [Fact]
    public void ACheckpointCannotCarryBinsItsObservationsDoNotImply()
    {
        EvolutionDescriptorDefinition axis = EvolutionDescriptorDefinition.AdaptiveQuantile("x", 0, 1, 2);
        foreach (int value in new[] { 3, 1, 2 }) axis = axis.Observe(value);

        EvolutionDescriptorDefinition restored = EvolutionDescriptorDefinition.Restore(axis.Name, 0, 1, 2,
            axis.OutOfRangePolicy, axis.Binning, null, axis.ObservationCount, 1, 3, axis.Sample, axis.SampleCapacity, axis.SampleStride);
        Assert.Equal(axis.ToCanonicalString(), restored.ToCanonicalString());

        Assert.Throws<ArgumentException>(() => EvolutionDescriptorDefinition.Restore(axis.Name, 0, 1, 2,
            axis.OutOfRangePolicy, axis.Binning, null, 3, 1, 3, new double[] { 3, 1, 2 }, axis.SampleCapacity, 1));
        Assert.Throws<ArgumentException>(() => EvolutionDescriptorDefinition.Restore(axis.Name, 0, 1, 2,
            axis.OutOfRangePolicy, axis.Binning, null, 3, 1, 3, new double[] { 1, 2, 30 }, axis.SampleCapacity, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionDescriptorDefinition.Restore(axis.Name, 0, 1, 2,
            axis.OutOfRangePolicy, axis.Binning, null, 3, 1, 3, axis.Sample, axis.SampleCapacity, 3));
    }

    [Theory]
    [InlineData(EvolutionDescriptorBinning.AdaptiveMinMax)]
    [InlineData(EvolutionDescriptorBinning.AdaptiveQuantile)]
    public async Task AResumedRunReproducesTheStateHashOfAnUninterruptedAdaptiveRun(EvolutionDescriptorBinning binning)
    {
        TestGenome[] seeds = Enumerable.Range(1, 4).Select(value => new TestGenome(value)).ToArray();

        EvolutionRunResult<TestGenome> uninterrupted = await Engine(binning, Options(12), new InMemoryEvolutionCheckpointStore())
            .RunAsync(seeds);

        var store = new InMemoryEvolutionCheckpointStore();
        EvolutionRunResult<TestGenome> partial = await Engine(binning, Options(6), store).RunAsync(seeds);
        EvolutionEngineOptions resumedOptions = Options(12);
        resumedOptions.Resume = true;
        EvolutionRunResult<TestGenome> resumed = await Engine(binning, resumedOptions, store).RunAsync(seeds);

        EvolutionDescriptorDefinition expected = uninterrupted.Islands[0].Descriptors[0];
        EvolutionDescriptorDefinition actual = resumed.Islands[0].Descriptors[0];
        // The axis has to have learned something, otherwise the test would pass without exercising anything.
        Assert.True(expected.ObservationCount > partial.Islands[0].Descriptors[0].ObservationCount);
        Assert.True(partial.Islands[0].Descriptors[0].ObservationCount > 1);
        Assert.Equal(expected.ToCanonicalString(), actual.ToCanonicalString());
        Assert.Equal(
            uninterrupted.Islands[0].Entries.Select(entry => entry.Cell.StableKey + "=" + entry.Evaluation.GenomeId),
            resumed.Islands[0].Entries.Select(entry => entry.Cell.StableKey + "=" + entry.Evaluation.GenomeId));
        Assert.Equal(uninterrupted.Islands[0].Version, resumed.Islands[0].Version);
        Assert.Equal(uninterrupted.StateHash, resumed.StateHash);
    }

    [Fact]
    public async Task TheTraceRecordsTheCellTheArchiveUsedNotOneFromTheConfiguredBins()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "adaptive.jsonl");
        // The configured axis spans [0, 1], so recomputing a cell from it would put every value from 1.5 up in bin 3.
        var configured = new[] { EvolutionDescriptorDefinition.AdaptiveMinMax("x", 0, 1, 4) };
        var cells = new List<string?>();
        EvolutionRunResult<TestGenome> result;
        using (var tracer = new EvolutionTraceObserver<TestGenome>(
                   new EvolutionTraceOptions { Enabled = true, Path = path }, "adaptive-run", configured))
        {
            var observer = new CellRecorder(tracer, cells);
            result = await Engine(EvolutionDescriptorBinning.AdaptiveMinMax, Options(8), new InMemoryEvolutionCheckpointStore(),
                observer).RunAsync(Enumerable.Range(1, 4).Select(value => new TestGenome(value)).ToArray());
        }

        // The last evaluation is the run's largest value, so it sits in the last bin of the final grid, and the
        // archive filed it under the same key.
        EvolutionTraceRecord last = EvolutionTraceFile.ReadRecords(path).Last();
        Assert.Equal(cells.Last(), last.Cell);
        Assert.Equal(result.Islands[0].Entries.Single(entry => entry.Evaluation.GenomeId == last.GenomeId).Cell.StableKey, last.Cell);
        // Earlier evaluations were filed on earlier grids, so the cells the trace records are not all the last bin.
        Assert.Contains(EvolutionTraceFile.ReadRecords(path), record => record.Cell != "3");
        Assert.All(cells, Assert.NotNull);
    }

    private static int Bin(EvolutionDescriptorDefinition axis, double value)
    {
        Assert.True(axis.TryGetBin(value, out int bin));
        return bin;
    }

    private static EvolutionArchiveInsertionResult Add(MapElitesArchive<TestGenome> archive, long id,
        string genomeId, double quality, double descriptor) =>
        MapElitesArchiveTests.Add(archive, id, genomeId, quality, descriptor);

    private static string Cell(MapElitesArchive<TestGenome> archive, string genomeId) =>
        archive.Entries.Single(entry => entry.Evaluation.GenomeId == genomeId).Cell.StableKey;

    private static EvolutionEngineOptions Options(int maxAttempts) => new()
    {
        RunId = "adaptive-run",
        Seed = 91,
        MaxEvaluationAttempts = maxAttempts,
        MaxProposals = 100,
        MaxGenerations = 100,
        // One proposal per batch, so the first leg stops on a batch boundary; see EvolutionDescriptorGrowthTests.
        ProposalBatchSize = 1,
        MaxDegreeOfParallelism = 1,
        IslandCount = 1,
        MigrationInterval = 0,
        MigrantsPerIsland = 1,
        CheckpointInterval = 0
    };

    private static EvolutionEngine<TestGenome> Engine(EvolutionDescriptorBinning binning,
        EvolutionEngineOptions options, IEvolutionCheckpointStore checkpointStore,
        IEvolutionObserver<TestGenome>? observer = null) => new(
        new SpreadingEvolutionTask(), new IncrementVariation(), _ => new MapElitesArchive<TestGenome>(new[]
        {
            binning == EvolutionDescriptorBinning.AdaptiveMinMax
                ? EvolutionDescriptorDefinition.AdaptiveMinMax("x", 0, 1, 4)
                : EvolutionDescriptorDefinition.AdaptiveQuantile("x", 0, 1, 4, sampleCapacity: 4)
        }), options, checkpointStore: checkpointStore, genomeCodec: new TestGenomeCodec(), observer: observer);

    /// <summary>Records the cell on every Evaluated event, then hands the event to the trace.</summary>
    private sealed class CellRecorder : IEvolutionObserver<TestGenome>
    {
        private readonly IEvolutionObserver<TestGenome> _inner;
        private readonly List<string?> _cells;

        public CellRecorder(IEvolutionObserver<TestGenome> inner, List<string?> cells)
        {
            _inner = inner;
            _cells = cells;
        }

        public ValueTask OnEventAsync(EvolutionEvent<TestGenome> evolutionEvent, CancellationToken cancellationToken = default)
        {
            if (evolutionEvent.Kind == EvolutionEventKind.Evaluated) _cells.Add(evolutionEvent.Cell?.StableKey);
            return _inner.OnEventAsync(evolutionEvent, cancellationToken);
        }
    }

    /// <summary>Reports a descriptor whose range keeps widening as the search moves past its seeds.</summary>
    private sealed class SpreadingEvolutionTask : IEvolutionTask<TestGenome>
    {
        public string Id => "spreading";
        public string VersionHash => "spreading-task-v1";
        public string EvaluatorVersionHash => "spreading-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome,
            CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(new TestGenome(genome.Value),
                genome.Value.ToString(CultureInfo.InvariantCulture)));

        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(value,
                new Dictionary<string, double> { ["x"] = value * value * 1.5 }));
        }
    }
}
