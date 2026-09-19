using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class CentroidArchiveTests
{
    private static CentroidArchiveDefinition Definition(EvolutionOutOfRangePolicy policy = EvolutionOutOfRangePolicy.Reject) =>
        new(new[] { new EvolutionDescriptorDefinition("x", 0, 1, 100, policy) }, new[] { new[] { 0d }, new[] { 1d } });
    private static Dictionary<string, double> X(double value) => new() { ["x"] = value };
    private static EvolutionArchiveInsertionResult Add(CentroidArchive<TestGenome> archive, long id, string genome, double quality, double x)
    {
        var pair = MapElitesArchiveTests.Create(id, genome, quality, x, archive.Direction);
        return archive.TryAdd(pair.Item1, pair.Item2);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, 0)]
    [InlineData(0.500001, 1)]
    [InlineData(1, 1)]
    public void NearestSiteAndDistanceTiesAreDeterministic(double value, int expected) =>
        Assert.Equal(expected, Definition().FindCell(X(value)));

    [Fact]
    public void NormalizationPreventsLargeUnitAxesFromDominating()
    {
        var definition = new CentroidArchiveDefinition(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 10000, 100),
            new EvolutionDescriptorDefinition("y", 0, 1, 100)
        }, new[] { new[] { 0d, 1d }, new[] { 1d, 0d } });
        Assert.Equal(0, definition.FindCell(new Dictionary<string, double> { ["x"] = 6000, ["y"] = 0.9 }));
    }

    [Fact]
    public void MissingNonfiniteAndRejectedValuesCannotRoute()
    {
        Assert.Null(Definition().FindCell(new Dictionary<string, double>()));
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 2 })
            Assert.Null(Definition().FindCell(X(value)));
        Assert.Equal(0, Definition(EvolutionOutOfRangePolicy.Clamp).FindCell(X(-double.MaxValue)));
        Assert.Equal(1, Definition(EvolutionOutOfRangePolicy.Clamp).FindCell(X(double.MaxValue)));
    }

    [Fact]
    public void GeometryIsOwnedAndVersionedIncludingSiteOrderAndNormalization()
    {
        var axes = new[] { new EvolutionDescriptorDefinition("x", 0, 1, 100) };
        var points = new[] { new[] { 0d }, new[] { 1d } };
        var definition = new CentroidArchiveDefinition(axes, points);
        points[0][0] = 0.4; axes[0] = new EvolutionDescriptorDefinition("changed", 0, 1, 100);
        Assert.Equal(Definition().DefinitionHash, definition.DefinitionHash);
        Assert.Equal(0, definition.Centroids[0][0]); Assert.Equal("x", definition.Descriptors[0].Name);
        Assert.Throws<NotSupportedException>(() => ((IList<double>)definition.Centroids[0])[0] = 0.4);
        Assert.NotEqual(definition.DefinitionHash, new CentroidArchiveDefinition(definition.Descriptors,
            definition.Centroids.Reverse()).DefinitionHash);
        Assert.NotEqual(definition.DefinitionHash, Definition(EvolutionOutOfRangePolicy.Clamp).DefinitionHash);
        Assert.NotEqual(new CentroidArchive<TestGenome>(definition).DefinitionHash,
            new CentroidArchive<TestGenome>(definition, EvolutionOptimizationDirection.Minimize).DefinitionHash);
    }

    [Fact]
    public void MalformedAndUnboundedGeometryIsRejected()
    {
        var axes = Definition().Descriptors;
        Assert.Throws<ArgumentException>(() => new CentroidArchiveDefinition(Array.Empty<EvolutionDescriptorDefinition>(), new[] { new[] { 0d } }));
        Assert.Throws<ArgumentException>(() => new CentroidArchiveDefinition(new[] { axes[0], axes[0] }, new[] { new[] { 0d, 0d } }));
        Assert.Throws<ArgumentException>(() => Definition(EvolutionOutOfRangePolicy.Grow));
        Assert.Throws<ArgumentException>(() => Definition(EvolutionOutOfRangePolicy.OverflowBins));
        foreach (var sites in new[] { Array.Empty<double[]>(), new[] { new[] { 0d }, new[] { -0d } },
                     new[] { new[] { double.NaN } }, new[] { new[] { 1.1 } }, new[] { new[] { 0d, 1d } } })
            Assert.Throws<ArgumentException>(() => new CentroidArchiveDefinition(axes, sites));
        Assert.Throws<ArgumentException>(() => new CentroidArchiveDefinition(axes,
            Enumerable.Range(0, CentroidArchiveDefinition.MaximumCentroids + 1).Select(i => new[] { i / 10001d })));
    }

    [Fact]
    public void HighDimensionalPartitionDoesNotMultiplyGridBins()
    {
        var axes = Enumerable.Range(0, 32).Select(i => new EvolutionDescriptorDefinition("d" + i, 0, 1, 100));
        var definition = new CentroidArchiveDefinition(axes, new[] { new double[32], Enumerable.Repeat(1d, 32).ToArray() });
        var snapshot = new EvolutionArchiveSnapshot<TestGenome>(new CentroidArchive<TestGenome>(definition));
        Assert.Equal(2, snapshot.TotalCells); Assert.Equal(32, snapshot.Descriptors.Count);
        Assert.Equal(2, new EvolutionArchiveSnapshot<TestGenome>(snapshot).TotalCells);
    }

    [Theory]
    [InlineData(EvolutionOptimizationDirection.Maximize, "b")]
    [InlineData(EvolutionOptimizationDirection.Minimize, "a")]
    public void AdmissionRetainsOnlyKElitesWithStableBest(EvolutionOptimizationDirection direction, string best)
    {
        var archive = new CentroidArchive<TestGenome>(Definition(), direction);
        Add(archive, 0, "a", 1, 0); Add(archive, 1, "b", 2, 1);
        Assert.Equal(best, archive.Best?.Evaluation.GenomeId); Assert.Equal(2, archive.Count);
        var old = archive.Entries;
        Assert.Same(old, archive.Entries);
        Add(archive, 2, "aa", direction == EvolutionOptimizationDirection.Maximize ? 3 : 0, 0);
        Assert.Equal(2, archive.Count); Assert.Equal(3, archive.Version);
        Assert.Equal("a", old[0].Evaluation.GenomeId); Assert.NotSame(old, archive.Entries);
    }

    [Fact]
    public void EqualQualityUsesOrdinalIdentityNotInsertionOrder()
    {
        foreach (string[] order in new[] { new[] { "z", "a" }, new[] { "a", "z" } })
        {
            var archive = new CentroidArchive<TestGenome>(Definition());
            for (int i = 0; i < order.Length; i++) Add(archive, i, order[i], 1, 0.2);
            Assert.Equal("a", archive.Best?.Evaluation.GenomeId);
        }
    }

    [Fact]
    public void OneGenomeCannotOccupyTwoCellsAndSamplingUsesOnlyCallerRandom()
    {
        var archive = new CentroidArchive<TestGenome>(Definition());
        Assert.Null(archive.Sample(new StableRandom(1, 2)));
        Add(archive, 1, "a", 1, 0);
        Assert.Equal(EvolutionArchiveInsertionResult.Rejected, Add(archive, 2, "a", 2, 1));
        Add(archive, 3, "b", 3, 1);
        var first = new StableRandom(1, 2); var second = new StableRandom(1, 2);
        for (int i = 0; i < 30; i++) Assert.Same(archive.Sample(first), archive.Sample(second));
        Assert.Null(archive.Get(new EvolutionCellKey(new[] { 999 })));
    }

    [Fact]
    public void InvalidIdentityStatusDirectionAndConstraintsCannotEnterArchive()
    {
        var archive = new CentroidArchive<TestGenome>(Definition());
        var pair = MapElitesArchiveTests.Create(1, "a", 1, 0.2);
        foreach (var invalid in new[]
        {
            Evaluation(EvolutionEvaluationStatus.Failed, null, EvolutionOptimizationDirection.Maximize, 1, "a"),
            Evaluation(EvolutionEvaluationStatus.Completed, 1, EvolutionOptimizationDirection.Minimize, 1, "a"),
            Evaluation(EvolutionEvaluationStatus.Completed, 1, EvolutionOptimizationDirection.Maximize, 2, "a"),
            Evaluation(EvolutionEvaluationStatus.Completed, 1, EvolutionOptimizationDirection.Maximize, 1, "other"),
            Evaluation(EvolutionEvaluationStatus.Completed, 1, EvolutionOptimizationDirection.Maximize, 1, "a", new[] { 0.1 })
        }) Assert.Equal(EvolutionArchiveInsertionResult.Rejected, archive.TryAdd(pair.Item1, invalid));
        Assert.Empty(archive.Entries); Assert.Equal(0, archive.Version);

        EvolutionEvaluation Evaluation(EvolutionEvaluationStatus status, double? quality, EvolutionOptimizationDirection direction,
            long id, string genome, double[]? constraints = null) => new(id, genome, status, quality, direction, X(0.2),
                Array.Empty<double>(), constraints ?? Array.Empty<double>(), pair.Item2.Cost, pair.Item2.Lineage,
                EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
    }

    [Fact]
    public void RestoreIsExactAndRejectsCorruptionWithoutPartialMutation()
    {
        var archive = new CentroidArchive<TestGenome>(Definition());
        Add(archive, 0, "a", 1, 0); Add(archive, 1, "b", 2, 1);
        var copy = new CentroidArchive<TestGenome>(Definition());
        var bad = new EvolutionArchiveEntry<TestGenome>(new EvolutionCellKey(new[] { 0 }), archive.Entries[1].Candidate, archive.Entries[1].Evaluation);
        Assert.Throws<ArgumentException>(() => copy.Restore(new[] { archive.Entries[0], bad }, archive.Descriptors, 7));
        Assert.Empty(copy.Entries); Assert.Null(copy.Best); Assert.Equal(0, copy.Version);
        Assert.Throws<ArgumentException>(() => copy.Restore(archive.Entries, Definition(EvolutionOutOfRangePolicy.Clamp).Descriptors, 7));
        Assert.Throws<ArgumentException>(() => copy.Restore(archive.Entries, archive.Descriptors, 1));
        copy.Restore(archive.Entries, archive.Descriptors, 7);
        Assert.Equal(7, copy.Version); Assert.Equal(archive.Entries.Select(e => e.Evaluation.GenomeId), copy.Entries.Select(e => e.Evaluation.GenomeId));
        Assert.Throws<InvalidOperationException>(() => copy.Restore(archive.Entries, archive.Descriptors, 7));
    }

    [Fact]
    public void VersionOverflowDoesNotMutateTheArchive()
    {
        var archive = new CentroidArchive<TestGenome>(Definition());
        archive.Restore(Array.Empty<EvolutionArchiveEntry<TestGenome>>(), archive.Descriptors, long.MaxValue);
        Assert.Throws<OverflowException>(() => Add(archive, 0, "a", 1, 0));
        Assert.Empty(archive.Entries); Assert.Null(archive.Best); Assert.Equal(long.MaxValue, archive.Version);
    }

    [Fact]
    public void ProjectionIsTransactionalAndUsesACommonReportingPartition()
    {
        var source = MapElitesArchiveTests.Archive();
        MapElitesArchiveTests.Add(source, 0, "a", 1, 0.1); MapElitesArchiveTests.Add(source, 1, "b", 2, 0.3);
        var original = source.Entries;
        var projected = CentroidArchive<TestGenome>.Project(source, Definition());
        Assert.Single(projected.Entries); Assert.Equal("b", projected.Best?.Evaluation.GenomeId);
        Assert.Equal(source.Version + 1, projected.Version); Assert.Equal(2, projected.TotalCells);
        Assert.Same(original, source.Entries); Assert.Equal(2, source.Count);
        var changed = new CentroidArchiveDefinition(new[] { new EvolutionDescriptorDefinition("x", 0.2, 1, 100) }, new[] { new[] { 0d } });
        Assert.Throws<ArgumentException>(() => CentroidArchive<TestGenome>.Project(source, changed));
        Assert.Same(original, source.Entries); Assert.NotEqual(source.DefinitionHash, projected.DefinitionHash);
    }

    [Fact]
    public async Task EngineStatusAndCheckpointResumeUseTheCentroidGeometry()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        var definition = new CentroidArchiveDefinition(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 100) },
            new[] { new[] { 0d }, new[] { 1d } });
        EvolutionEngine<TestGenome> Engine(CentroidArchiveDefinition geometry) => new(
            new SyntheticEvolutionTask(), new IncrementVariation(), _ => new CentroidArchive<TestGenome>(geometry),
            new EvolutionEngineOptions
            {
                RunId = "centroids",
                Resume = true,
                Seed = 17,
                MaxEvaluationAttempts = 16,
                MaxProposals = 100,
                MaxGenerations = 100,
                ProposalBatchSize = 2,
                MaxDegreeOfParallelism = 2,
                MigrationInterval = 0
            },
            checkpointStore: store, genomeCodec: new TestGenomeCodec());
        var seeds = new[] { new TestGenome(1), new TestGenome(99) };
        var run = await Engine(definition).RunAsync(seeds);
        var resumed = await Engine(definition).RunAsync(seeds);
        Assert.Equal(run.StateHash, resumed.StateHash);
        Assert.All(run.IslandStatuses, status =>
        {
            Assert.Equal(2, status.TotalCells);
            Assert.Equal(status.EliteCount / 2d, status.Coverage);
        });
        Assert.All(run.Islands, island => Assert.Equal(2, Assert.IsAssignableFrom<IEvolutionArchiveCellCount>(island).TotalCells));
        var changed = new CentroidArchiveDefinition(definition.Descriptors, new[] { new[] { 0.1 }, new[] { 0.9 } });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Engine(changed).RunAsync(seeds));
    }

    [Fact]
    public void ProjectionReportRetainsTransactionVersionsAfterTargetChanges()
    {
        var source = MapElitesArchiveTests.Archive();
        MapElitesArchiveTests.Add(source, 0, "a", 1, 0.1);
        MapElitesArchiveTests.Add(source, 1, "b", 2, 0.3);
        var projection = CentroidArchive<TestGenome>.ProjectWithReport(source, Definition());
        var report = projection.Report;
        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal(source.DefinitionHash, report.SourceDefinitionHash);
        Assert.Equal(source.Version, report.SourceVersion);
        Assert.Equal(projection.Archive.DefinitionHash, report.TargetDefinitionHash);
        Assert.Equal(source.Version + 1, report.TargetVersion);
        Assert.Equal(2, report.SourceEliteCount);
        Assert.Equal(1, report.RetainedEliteCount);
        Assert.Equal(1, report.CollisionDiscardedEliteCount);
        Assert.True(report.DefinitionChanged);
        Add(projection.Archive, 2, "c", 3, 1);
        Assert.Equal(2, projection.Archive.Count);
        Assert.Equal(1, report.RetainedEliteCount);
        Assert.Equal(report.TargetVersion + 1, projection.Archive.Version);
        Assert.Equal(2, source.Count);
    }

    [Fact]
    public void UnchangedGeometryProjectionReportsNoDefinitionChange()
    {
        var source = new CentroidArchive<TestGenome>(Definition());
        Add(source, 0, "a", 1, 0);
        Add(source, 1, "b", 2, 1);
        var projection = CentroidArchive<TestGenome>.ProjectWithReport(source, Definition());
        Assert.False(projection.Report.DefinitionChanged);
        Assert.Equal(0, projection.Report.CollisionDiscardedEliteCount);
        Assert.Equal(2, projection.Report.RetainedEliteCount);
        Assert.Equal(source.Version + 1, projection.Report.TargetVersion);
    }

    [Fact]
    public void ProjectionVersionOverflowLeavesTheSourceUntouched()
    {
        var source = new CentroidArchive<TestGenome>(Definition());
        source.Restore(Array.Empty<EvolutionArchiveEntry<TestGenome>>(), source.Descriptors, long.MaxValue);
        Assert.Throws<OverflowException>(() => CentroidArchive<TestGenome>.ProjectWithReport(source, Definition()));
        Assert.Empty(source.Entries);
        Assert.Equal(long.MaxValue, source.Version);
    }
}
