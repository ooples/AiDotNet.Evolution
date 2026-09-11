using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class ParetoArchiveTests
{
    private static EvolutionParetoDefinition Definition(int capacity = 8,
        EvolutionParetoRepresentative representative = EvolutionParetoRepresentative.ClosestToIdeal, double resolution = 0) =>
        new(new[] { new EvolutionObjectiveDefinition("latency", EvolutionOptimizationDirection.Minimize, 0, 1, resolution),
            new EvolutionObjectiveDefinition("memory", EvolutionOptimizationDirection.Minimize, 0, 1, resolution) }, capacity, representative);

    private static (EvolutionCandidate<TestGenome>, EvolutionEvaluation) Pair(long id, string name, double quality,
        double[] objectives, double violation = 0, EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        var pair = MapElitesArchiveTests.Create(id, name, quality, 0.5, direction);
        return (pair.Item1, new EvolutionEvaluation(id, name, EvolutionEvaluationStatus.Completed, quality, direction,
            pair.Item2.Descriptors, objectives, new[] { violation }, pair.Item2.Cost, pair.Item2.Lineage, EvolutionCacheStatus.Miss,
            Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config"));
    }

    private static EvolutionArchiveInsertionResult Add(ParetoArchive<TestGenome> archive, long id, string name,
        double quality, double x, double y, double violation = 0)
    {
        var pair = Pair(id, name, quality, new[] { x, y }, violation, archive.Direction);
        return archive.TryAdd(pair.Item1, pair.Item2);
    }

    [Fact]
    public void ConflictingObjectivesSurviveEvenWhenScalarQualityDisagrees()
    {
        var archive = new ParetoArchive<TestGenome>(Definition());
        Add(archive, 0, "fast", 100, .1, .9); Add(archive, 1, "balanced", 2, .4, .4); Add(archive, 2, "small", 1, .9, .1);
        Assert.Equal(3, archive.Count); Assert.Equal("balanced", archive.Best!.Evaluation.GenomeId);
        Assert.Equal(EvolutionArchiveInsertionResult.NotImproved, Add(archive, 3, "dominated", 1000, .6, .6));
        Assert.Equal(3, archive.Version);
        Assert.Equal("small", Assert.Single(archive.Front.Query("memory", 0, .2)).Evaluation.GenomeId);
    }

    [Theory]
    [InlineData(EvolutionParetoRepresentative.ClosestToIdeal, "balanced")]
    [InlineData(EvolutionParetoRepresentative.Lexicographic, "fast")]
    [InlineData(EvolutionParetoRepresentative.ScalarQuality, "small")]
    public void SnapshotAndNestedSnapshotPreserveExplicitRepresentative(EvolutionParetoRepresentative policy, string expected)
    {
        var archive = new ParetoArchive<TestGenome>(Definition(representative: policy));
        Add(archive, 0, "fast", 1, .1, .9); Add(archive, 1, "balanced", 2, .4, .4); Add(archive, 2, "small", 3, .9, .1);
        var snapshot = new EvolutionArchiveSnapshot<TestGenome>(archive);
        Add(archive, 3, "ideal", 10, 0, 0);
        Assert.Equal(3, snapshot.Count); Assert.Equal(expected, snapshot.Best!.Evaluation.GenomeId);
        Assert.Equal(expected, new EvolutionArchiveSnapshot<TestGenome>(snapshot).Best!.Evaluation.GenomeId);
        Assert.Equal(policy, snapshot.ParetoDefinition!.Representative);
        Assert.Equal(8, snapshot.TotalCells);
    }

    [Theory]
    [InlineData(.2, .2, .000000001)]
    [InlineData(-.01, .2, 0)]
    [InlineData(.2, 1.01, 0)]
    public void InfeasibleAndOutOfBoundsCandidatesAreRejectedWithoutMutation(double x, double y, double violation)
    {
        var archive = new ParetoArchive<TestGenome>(Definition());
        Assert.Equal(EvolutionArchiveInsertionResult.Rejected, Add(archive, 0, "invalid", 1000, x, y, violation));
        Assert.Empty(archive.Entries); Assert.Equal(0, archive.Version); Assert.Null(archive.Best);
    }

    [Fact]
    public void AdmissionValidatesIdentityVectorAndScalarDirection()
    {
        var archive = new ParetoArchive<TestGenome>(Definition());
        var original = Pair(0, "a", 1, new[] { .2, .2 });
        foreach (var pair in new[] { Pair(1, "a", 1, new[] { .2, .2 }), Pair(0, "b", 1, new[] { .2, .2 }),
            Pair(0, "a", 1, new[] { .2 }), Pair(0, "a", 1, new[] { .2, .2 }, direction: EvolutionOptimizationDirection.Minimize) })
            Assert.Equal(EvolutionArchiveInsertionResult.Rejected, archive.TryAdd(original.Item1, pair.Item2));
        archive.TryAdd(original.Item1, original.Item2);
        Assert.Equal(EvolutionArchiveInsertionResult.NotImproved, Add(archive, 4, "a", 99, .1, .1));
        Assert.Single(archive.Entries);
    }

    [Fact]
    public void IndependentDirectionsAndUnitsNormalizeBeforeDominance()
    {
        var definition = new EvolutionParetoDefinition(new[]
        {
            new EvolutionObjectiveDefinition("bytes", EvolutionOptimizationDirection.Minimize, 0, 1_000_000),
            new EvolutionObjectiveDefinition("accuracy", EvolutionOptimizationDirection.Maximize, .5, 1)
        });
        Assert.True(definition.Dominates(new[] { 100_000d, .9 }, new[] { 200_000d, .8 }));
        Assert.False(definition.Dominates(new[] { 100_000d, .7 }, new[] { 200_000d, .8 }));
        Assert.Equal(.2, definition.Objectives[1].Normalize(.9), 12);
    }

    [Fact]
    public void EpsilonBoxesAreTransitiveAndTiesAreInsertionOrderIndependent()
    {
        var definition = Definition(resolution: .1);
        var random = new StableRandom(3, 4);
        var vectors = Enumerable.Range(0, 30).Select(_ => new[] { random.NextDouble(), random.NextDouble() }).ToArray();
        foreach (var a in vectors) foreach (var b in vectors) foreach (var c in vectors)
            if (definition.Dominates(a, b) && definition.Dominates(b, c)) Assert.True(definition.Dominates(a, c));
        foreach (bool reverse in new[] { false, true })
        {
            var archive = new ParetoArchive<TestGenome>(definition);
            var pairs = new[] { Pair(0, "z", 99, new[] { .29, .21 }), Pair(1, "a", 1, new[] { .21, .29 }) };
            foreach (var pair in reverse ? Enumerable.Reverse(pairs) : pairs) archive.TryAdd(pair.Item1, pair.Item2);
            Assert.Equal("a", Assert.Single(archive.Entries).Evaluation.GenomeId);
        }
    }

    [Fact]
    public void CapacityRetainsExtremesAndEvictionIsDeterministic()
    {
        foreach (bool reverse in new[] { false, true })
        {
            var archive = new ParetoArchive<TestGenome>(Definition(2));
            var pairs = new[] { Pair(0, "a", 1, new[] { .1, .9 }), Pair(1, "b", 100, new[] { .5, .5 }), Pair(2, "c", 1, new[] { .9, .1 }) };
            foreach (var pair in reverse ? Enumerable.Reverse(pairs) : pairs) archive.TryAdd(pair.Item1, pair.Item2);
            Assert.Equal(new[] { "a", "c" }, archive.Entries.Select(entry => entry.Evaluation.GenomeId).OrderBy(id => id));
            Assert.Equal(2, archive.Count); Assert.Equal(2, archive.TotalCells);
        }
    }

    [Fact]
    public void DominatingInsertionReportsAllRemovalsAndOldViewsStayFrozen()
    {
        var archive = new ParetoArchive<TestGenome>(Definition());
        Add(archive, 0, "a", 1, .1, .9); Add(archive, 1, "b", 2, .9, .1);
        var old = archive.Entries; var pair = Pair(2, "ideal", 0, new[] { 0d, 0d });
        var mutation = ((IEvolutionArchiveMutationSource<TestGenome>)archive).TryAddWithMutation(pair.Item1, pair.Item2);
        Assert.Equal(2, mutation.Removed.Count); Assert.Equal("ideal", mutation.Added!.Evaluation.GenomeId);
        Assert.Equal(EvolutionArchiveInsertionResult.InsertedWithEviction, mutation.Result);
        Assert.Equal(2, old.Count); Assert.Single(archive.Entries);
        Assert.Same(archive.Entries, archive.Entries); Assert.Same(archive.Best, archive.Get(archive.Best!.Cell));
        Assert.Null(archive.Get(new EvolutionCellKey(new[] { 0, 0 })));
    }

    [Fact]
    public void HypervolumeHasKnownValuesAndUsesRawValuesNotBoxCorners()
    {
        var archive = new ParetoArchive<TestGenome>(Definition(resolution: .1));
        Assert.Equal(0, archive.Front.Hypervolume());
        Add(archive, 0, "a", 1, .2, .8); Add(archive, 1, "b", 1, .5, .4);
        Assert.Equal(.36, archive.Front.Hypervolume(), 12);
        var definition = new EvolutionParetoDefinition(Definition().Objectives.Concat(new[] {
            new EvolutionObjectiveDefinition("energy", EvolutionOptimizationDirection.Minimize, 0, 1) }));
        var three = new ParetoArchive<TestGenome>(definition);
        foreach (var pair in new[] { Pair(0, "a", 1, new[] { .2, .8, .5 }), Pair(1, "b", 1, new[] { .5, .4, .5 }) })
            three.TryAdd(pair.Item1, pair.Item2);
        Assert.Equal(.18, three.Front.Hypervolume(), 12);
        var raw = new ParetoArchive<TestGenome>(Definition(resolution: .1)); Add(raw, 0, "raw", 1, .29, .29);
        Assert.Equal(.71 * .71, raw.Front.Hypervolume(), 12);
    }

    [Fact]
    public void QueryAndHypervolumeRejectUndefinedReportingSemantics()
    {
        var archive = new ParetoArchive<TestGenome>(Definition());
        Assert.Throws<ArgumentException>(() => archive.Front.Query("missing", 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => archive.Front.Query("memory", 1, 0));
        var many = new EvolutionParetoDefinition(Enumerable.Range(0, 4).Select(i => new EvolutionObjectiveDefinition("d" + i,
            EvolutionOptimizationDirection.Minimize, 0, 1)));
        Assert.Throws<NotSupportedException>(() => new ParetoArchive<TestGenome>(many).Front.Hypervolume());
    }

    [Theory]
    [InlineData(double.NaN, 1, 0)]
    [InlineData(0, double.PositiveInfinity, 0)]
    [InlineData(-double.MaxValue, double.MaxValue, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(0, 1, -1)]
    [InlineData(0, 1, 1e-15)]
    [InlineData(0, 1, 2)]
    public void ObjectiveDefinitionRejectsUnstableOrUndefinedArithmetic(double low, double high, double resolution) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionObjectiveDefinition("a", EvolutionOptimizationDirection.Minimize, low, high, resolution));

    [Fact]
    public void DefinitionIsBoundedImmutableAndHashesAllSemantics()
    {
        var axes = Definition().Objectives.ToArray(); var original = new EvolutionParetoDefinition(axes);
        axes[0] = new EvolutionObjectiveDefinition("other", EvolutionOptimizationDirection.Maximize, 0, 2);
        Assert.Equal("latency", original.Objectives[0].Name);
        Assert.NotEqual(original.DefinitionHash, new EvolutionParetoDefinition(axes).DefinitionHash);
        Assert.NotEqual(Definition().DefinitionHash, Definition(9).DefinitionHash);
        Assert.NotEqual(Definition().DefinitionHash, Definition(representative: EvolutionParetoRepresentative.ScalarQuality).DefinitionHash);
        Assert.NotEqual(Definition().DefinitionHash, Definition(resolution: .1).DefinitionHash);
        Assert.Throws<ArgumentException>(() => new EvolutionParetoDefinition(new[] { axes[0], axes[0] }));
        Assert.Throws<ArgumentException>(() => new EvolutionParetoDefinition(Enumerable.Repeat(axes[0], 9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(257));
        Assert.Throws<ArgumentException>(() => Definition().Dominates(new[] { .5 }, new[] { .5, .5 }));
    }

    [Fact]
    public void RestoreRetainsSlotsAndRejectsCorruptionTransactionally()
    {
        var archive = new ParetoArchive<TestGenome>(Definition());
        Add(archive, 0, "a", 1, .1, .9); Add(archive, 1, "b", 1, .9, .1); Add(archive, 2, "c", 1, .5, .5);
        Add(archive, 3, "d", 1, .8, 0); // removes slot 1, leaves a non-key-sorted front
        var copy = new ParetoArchive<TestGenome>(Definition());
        var bad = new EvolutionArchiveEntry<TestGenome>(archive.Entries[0].Cell, archive.Entries[1].Candidate, archive.Entries[1].Evaluation);
        Assert.Throws<ArgumentException>(() => copy.Restore(new[] { archive.Entries[0], bad }, archive.Descriptors, 99));
        Assert.Empty(copy.Entries); Assert.Equal(0, copy.Version);
        copy.Restore(archive.Entries.Reverse().ToArray(), archive.Descriptors, archive.Version);
        Assert.Equal(archive.Entries.Select(e => e.Cell.StableKey + e.Evaluation.GenomeId), copy.Entries.Select(e => e.Cell.StableKey + e.Evaluation.GenomeId));
        Assert.Equal(archive.Best!.Evaluation.GenomeId, copy.Best!.Evaluation.GenomeId);
        Assert.Throws<InvalidOperationException>(() => copy.Restore(archive.Entries, archive.Descriptors, 99));
        var overflow = new ParetoArchive<TestGenome>(Definition()); overflow.Restore(Array.Empty<EvolutionArchiveEntry<TestGenome>>(), overflow.Descriptors, long.MaxValue);
        Assert.Throws<OverflowException>(() => Add(overflow, 0, "a", 1, .1, .1)); Assert.Empty(overflow.Entries);
    }

    [Fact]
    public void SamplingAndMigrationKeepDiverseFrontNotScalarTopK()
    {
        var archive = new ParetoArchive<TestGenome>(Definition());
        Assert.Null(archive.Sample(new StableRandom(1, 2)));
        Add(archive, 0, "a", 1, .1, .9); Add(archive, 1, "b", 100, .5, .5); Add(archive, 2, "c", 1, .9, .1);
        var one = new StableRandom(1, 2); var two = new StableRandom(1, 2); var policy = new ParetoEvolutionSelectionPolicy<TestGenome>();
        var parents = new HashSet<string>();
        for (int i = 0; i < 50; i++)
        {
            var selected = policy.Select(archive, one, 20)!; var repeated = policy.Select(archive, two, 20)!;
            parents.Add(selected.Parent.Evaluation.GenomeId);
            Assert.Equal(selected.Parent.Evaluation.GenomeId, repeated.Parent.Evaluation.GenomeId);
            Assert.Equal(2, selected.Inspirations.Count); Assert.DoesNotContain(selected.Parent, selected.Inspirations);
        }
        Assert.Equal(3, parents.Count);
        var migrations = new ParetoEvolutionMigrationPolicy<TestGenome>().CreateMigrations(new IEvolutionArchiveView<TestGenome>[] {
            new EvolutionArchiveSnapshot<TestGenome>(archive), new ParetoArchive<TestGenome>(Definition()) }, 2, new StableRandom(1, 2));
        Assert.Equal(new[] { "a", "c" }, migrations.Select(m => m.Entry.Evaluation.GenomeId));
        Assert.Throws<ArgumentException>(() => policy.Select(MapElitesArchiveTests.Archive(), one, 0));
    }

    private sealed class TradeoffTask : IEvolutionTask<TestGenome>
    {
        private readonly bool _plateau;
        public TradeoffTask(bool plateau = false) => _plateau = plateau;
        public string Id => "tradeoff-test";
        public string VersionHash => "tradeoff-test-v1";
        public string EvaluatorVersionHash => "tradeoff-test-eval-v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome, genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            double x = Math.Min(1, candidate.CanonicalGenome.Genome.Value / 100d);
            return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1 - x,
                objectives: _plateau ? new[] { .5, .5 } : new[] { x, 1 - x }));
        }
    }

    private static EvolutionEngineOptions Options(int budget = 12) => new()
    {
        RunId = "pareto-test",
        Seed = 17,
        MaxEvaluationAttempts = budget,
        MaxProposals = 200,
        MaxGenerations = 100,
        ProposalBatchSize = 2,
        MaxDegreeOfParallelism = 2,
        MigrationInterval = 1,
        IslandCount = 2
    };

    private static EvolutionEngine<TestGenome> Engine(EvolutionEngineOptions options, InMemoryEvolutionCheckpointStore? store = null,
        EvolutionParetoDefinition? definition = null) => new(new TradeoffTask(), new IncrementVariation(),
        _ => new ParetoArchive<TestGenome>(definition ?? Definition()), options, checkpointStore: store,
        genomeCodec: new TestGenomeCodec());

    [Fact]
    public async Task EngineResultCheckpointAndResumedRunKeepTheSameFrontAndState()
    {
        var seeds = new[] { new TestGenome(10), new TestGenome(50), new TestGenome(90) };
        var full = await Engine(Options()).RunAsync(seeds);
        var store = new InMemoryEvolutionCheckpointStore(); await Engine(Options(6), store).RunAsync(seeds);
        var options = Options(); options.Resume = true;
        var resumed = await Engine(options, store).RunAsync(seeds);
        Assert.Equal(full.StateHash, resumed.StateHash);
        Assert.NotNull(full.ParetoFront); Assert.True(full.ParetoFront!.Entries.Count > 1);
        Assert.Same(full.ParetoFront.Representative, full.Best);
        Assert.Empty(full.GlobalElites); Assert.DoesNotContain(full.RetainedFailures, failure => failure.Code == "descriptor_missing");
        var checkpoint = (await store.LoadLatestAsync(options.RunId))!;
        Assert.Contains("\"SchemaVersion\":8", checkpoint.Payload);
        var contents = EvolutionEngine<TestGenome>.ReadCheckpoint(checkpoint, new TestGenomeCodec());
        Assert.Equal(full.ParetoFront.Definition.DefinitionHash, contents.ParetoFront!.Definition.DefinitionHash);
        Assert.Equal(full.ParetoFront.Hypervolume(), contents.ParetoFront.Hypervolume());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Engine(options, store, Definition(resolution: .1)).RunAsync(seeds));
    }

    [Theory]
    [InlineData("history")]
    [InlineData("global")]
    [InlineData("selection")]
    [InlineData("target")]
    [InlineData("stopping")]
    public void EngineRejectsAccidentalScalarSemantics(string scenario)
    {
        var options = Options();
        switch (scenario)
        {
            case "history": options.HistorySize = 3; break;
            case "global": options.GlobalEliteCount = 3; break;
            case "selection": options.SelectionPolicy = EvolutionSelectionPolicyKind.Ratio; break;
            case "target": options.TargetQuality = .9; break;
            case "stopping": options.EarlyStopping.PatienceEvaluations = 3; break;
        }
        Assert.Throws<ArgumentException>(() => Engine(options));
    }

    [Fact]
    public async Task HypervolumePlateauStopsExplicitlyAndScalarArchiveRejectsThatMetric()
    {
        var options = Options(20); options.IslandCount = 1; options.ProposalBatchSize = 1;
        options.EarlyStopping = new EvolutionEarlyStoppingOptions { Metric = EvolutionEarlyStoppingMetric.ParetoHypervolume, PatienceEvaluations = 3 };
        var engine = new EvolutionEngine<TestGenome>(new TradeoffTask(true), new SequentialVariation(),
            _ => new ParetoArchive<TestGenome>(Definition()), options);
        var run = await engine.RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(EvolutionStopReason.EarlyStopped, run.StopReason); Assert.Equal(.25, run.ParetoFront!.Hypervolume());
        Assert.Throws<ArgumentException>(() => new EvolutionEngine<TestGenome>(new SyntheticEvolutionTask(), new IncrementVariation(),
            _ => MapElitesArchiveTests.Archive(), options));
    }

    [Fact]
    public void ScalarSnapshotJsonDoesNotAcquireParetoMetadata()
    {
        var scalar = new EvolutionArchiveSnapshot<TestGenome>(MapElitesArchiveTests.Archive());
        Assert.Null(scalar.ParetoDefinition); Assert.DoesNotContain("ParetoDefinition", JsonSerializer.Serialize(scalar));
    }

    [Theory]
    [InlineData("old-schema")]
    [InlineData("missing-definition")]
    [InlineData("changed-direction")]
    [InlineData("duplicate-name")]
    [InlineData("infeasible")]
    [InlineData("out-of-bounds")]
    [InlineData("duplicate-entry")]
    [InlineData("invalid-slot")]
    [InlineData("negative-version")]
    public async Task CorruptFrontCheckpointIsRejectedBeforeAnyGenomeCodec(string corruption)
    {
        var store = new InMemoryEvolutionCheckpointStore(); var options = Options(4);
        await Engine(options, store).RunAsync(new[] { new TestGenome(10), new TestGenome(50), new TestGenome(90) });
        var checkpoint = (await store.LoadLatestAsync(options.RunId))!;
        var payload = JsonNode.Parse(checkpoint.Payload)!;
        var island = payload["Islands"]![0]!; var entries = island["Entries"]!.AsArray(); var entry = entries[0]!;
        switch (corruption)
        {
            case "old-schema": payload["SchemaVersion"] = 7; break;
            case "missing-definition": island.AsObject().Remove("Pareto"); break;
            case "changed-direction": island["Pareto"]!["Objectives"]![0]!["Direction"] = (int)EvolutionOptimizationDirection.Maximize; break;
            case "duplicate-name": island["Pareto"]!["Objectives"]![0]!["Name"] = "memory"; break;
            case "infeasible": entry["Evaluation"]!["ConstraintViolations"] = new JsonArray(.1); break;
            case "out-of-bounds": entry["Evaluation"]!["Objectives"]![0] = 2; break;
            case "duplicate-entry": entries.Add(JsonNode.Parse(entry.ToJsonString())); break;
            case "invalid-slot": entry["CellBins"]![0] = 100; break;
            case "negative-version": island["Version"] = -1; break;
        }
        var corrupt = new EvolutionCheckpoint(checkpoint.RunId, checkpoint.Sequence, checkpoint.CompatibilityHash, payload.ToJsonString());
        var codec = new CountingCodec();
        Assert.Throws<InvalidDataException>(() => EvolutionEngine<TestGenome>.ReadCheckpoint(corrupt, codec));
        Assert.Equal(0, codec.Reads);
    }

    private sealed class CountingCodec : IEvolutionGenomeCodec<TestGenome>
    {
        public int Reads { get; private set; }
        public string Id => "counter";
        public string VersionHash => "counter-v1";
        public string Serialize(TestGenome genome) => "0";
        public TestGenome Deserialize(string payload) { Reads++; return new TestGenome(0); }
    }
}
