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

    private sealed class InvalidObjectiveTask : IEvolutionTask<TestGenome>
    {
        private readonly string _scenario;
        public InvalidObjectiveTask(string scenario) => _scenario = scenario;
        public string Id => "invalid-objectives";
        public string VersionHash => "invalid-objectives-v1";
        public string EvaluatorVersionHash => "invalid-objectives-eval-v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome, genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default) => new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1,
                _scenario == "direction" ? EvolutionOptimizationDirection.Minimize : EvolutionOptimizationDirection.Maximize,
                objectives: _scenario == "wrong-count" ? new[] { .5 }
                    : new[] { .5, _scenario == "out-of-bounds" ? 1.0000001 : .5 }));
    }

    [Theory]
    [InlineData("out-of-bounds", "out_of_bounds")]
    [InlineData("wrong-count", "objective_count")]
    [InlineData("direction", "direction_mismatch")]
    public async Task RefusedObjectiveVectorsAreDiagnosedInsteadOfSilentlyDropped(string scenario, string expectedReason)
    {
        var options = Options(4); options.IslandCount = 1; options.ProposalBatchSize = 1;
        options.MaxDegreeOfParallelism = 1; options.MigrationInterval = 0;
        var run = await new EvolutionEngine<TestGenome>(new InvalidObjectiveTask(scenario), new IncrementVariation(),
            _ => new ParetoArchive<TestGenome>(Definition()), options).RunAsync(new[] { new TestGenome(1) });

        Assert.Empty(run.ParetoFront!.Entries);
        Assert.Null(run.Best);
        var failure = Assert.Single(run.RetainedFailures, diagnostic => diagnostic.Code == "objective_invalid");
        Assert.Equal(expectedReason, failure.Data["reason"]);
        Assert.Equal("2", failure.Data["expected_objectives"]);
        if (scenario == "out-of-bounds")
        {
            Assert.Equal("1", failure.Data["objective_index"]);
            Assert.Equal("memory", failure.Data["objective"]);
            Assert.Equal("1.0000001", failure.Data["value"]);
            Assert.Equal("1", failure.Data["maximum"]);
        }
        if (scenario == "wrong-count") Assert.Equal("1", failure.Data["objectives"]);
        if (scenario == "direction") Assert.Equal("Maximize", failure.Data["archive_direction"]);
    }

    private sealed class LateFeasibilityTask : IEvolutionTask<TestGenome>
    {
        public string Id => "late-feasibility";
        public string VersionHash => "late-feasibility-v1";
        public string EvaluatorVersionHash => "late-feasibility-eval-v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome, genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            double x = Math.Min(1, value / 20d);
            return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1 - x, objectives: new[] { x, 1 - x },
                constraintViolations: new[] { (double)Math.Max(0, 6 - value) }));
        }
    }

    [Fact]
    public async Task HypervolumeStoppingWaitsForFeasibilityAndDoesNotStopWhileVolumeKeepsRising()
    {
        var options = new EvolutionEngineOptions
        {
            RunId = "exploration-stopping",
            Seed = 7,
            MaxEvaluationAttempts = 12,
            MaxProposals = 100,
            MaxGenerations = 100,
            ProposalBatchSize = 1,
            MaxDegreeOfParallelism = 1,
            MigrationInterval = 0,
            EarlyStopping = new EvolutionEarlyStoppingOptions
            {
                Metric = EvolutionEarlyStoppingMetric.ParetoHypervolume,
                PatienceEvaluations = 3
            }
        };
        var run = await new EvolutionEngine<TestGenome>(new LateFeasibilityTask(), new SequentialVariation(),
            _ => new ParetoArchive<TestGenome>(Exploring(4)), options).RunAsync(new[] { new TestGenome(1) });

        // Five infeasible evaluations precede the first feasible one, and the front's volume rises on every feasible
        // insertion afterwards. Reporting volume zero for the empty front would exhaust patience before feasibility.
        Assert.Equal(EvolutionStopReason.EvaluationBudgetReached, run.StopReason);
        Assert.Equal(12, run.Counters.EvaluationAttempts);
        Assert.True(run.ParetoFront!.Entries.Count > 1);
        Assert.True(run.ParetoFront.Hypervolume() > 0);

        // The infeasible phase is reported as unmeasurable rather than silently skipped.
        EvolutionEarlyStoppingReport report = run.EarlyStopping;
        Assert.Equal("ParetoHypervolume", report.Criterion);
        Assert.Equal(5, report.UnmeasurableReadings);
        Assert.Equal(5, report.UnmeasurableEvaluations);
        Assert.Equal(5, report.UnmeasurableReasons[EvolutionEarlyStoppingUnmeasurableReason.EmptyFeasibleFront]);
        Assert.True(report.WasEverMeasurable);
    }

    [Fact]
    public async Task HypervolumeStoppingFailsARunWhoseFrontIsNeverFeasible()
    {
        var options = new EvolutionEngineOptions
        {
            RunId = "never-feasible",
            Seed = 7,
            MaxEvaluationAttempts = 4,
            MaxProposals = 40,
            MaxGenerations = 40,
            ProposalBatchSize = 1,
            MaxDegreeOfParallelism = 1,
            MigrationInterval = 0,
            EarlyStopping = new EvolutionEarlyStoppingOptions
            {
                Metric = EvolutionEarlyStoppingMetric.ParetoHypervolume,
                PatienceEvaluations = 3
            }
        };
        var engine = new EvolutionEngine<TestGenome>(new LateFeasibilityTask(), new SequentialVariation(),
            _ => new ParetoArchive<TestGenome>(Exploring(4)), options);

        // Asking for hypervolume stopping on a search that never produces a feasible point is a configuration
        // mistake: patience is never charged, so the run would quietly behave as though stopping were switched off.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(new[] { new TestGenome(1) }));
        Assert.Contains("ParetoHypervolume", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(EvolutionEarlyStoppingUnmeasurableReason.EmptyFeasibleFront), failure.Message, StringComparison.Ordinal);
    }

    private sealed class AlternatingFeasibilityTask : IEvolutionTask<TestGenome>
    {
        public string Id => "alternating-feasibility";
        public string VersionHash => "alternating-feasibility-v1";
        public string EvaluatorVersionHash => "alternating-feasibility-eval-v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome, genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            double x = Math.Min(1, value / 100d);
            return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1 - x, objectives: new[] { x, 1 - x },
                constraintViolations: new[] { value % 2 == 1 ? 1d : 0d }));
        }
    }

    [Fact]
    public async Task ExplorationPoolDoesNotStallAnExplicitNonParetoSelectionPolicy()
    {
        static Task<EvolutionRunResult<TestGenome>> RunAsync(int infeasibleCapacity)
        {
            var definition = new EvolutionParetoDefinition(Definition().Objectives, 8,
                EvolutionParetoRepresentative.ClosestToIdeal, infeasibleCapacity);
            var options = new EvolutionEngineOptions
            {
                RunId = "custom-front-selection",
                Seed = 1,
                IslandCount = 2,
                MaxEvaluationAttempts = 20,
                MaxProposals = 60,
                MaxGenerations = 100,
                ProposalBatchSize = 1,
                MaxDegreeOfParallelism = 1,
                MigrationInterval = 0
            };
            return new EvolutionEngine<TestGenome>(new AlternatingFeasibilityTask(), new IncrementVariation(),
                _ => new ParetoArchive<TestGenome>(definition), options,
                selection: new UniformEvolutionSelectionPolicy<TestGenome>())
                .RunAsync(new[] { new TestGenome(2), new TestGenome(1) });
        }

        // The island holding only pool members is not selectable material for a policy that samples the feasible
        // front alone: enabling the pool must not shorten the run or empty its front relative to the disabled control.
        var control = await RunAsync(0);
        var exploring = await RunAsync(4);

        Assert.NotEqual(EvolutionStopReason.NoCandidates, exploring.StopReason);
        Assert.Equal(control.StopReason, exploring.StopReason);
        Assert.Equal(control.Counters.CompletedEvaluations, exploring.Counters.CompletedEvaluations);
        Assert.NotEmpty(exploring.ParetoFront!.Entries);
        Assert.NotEmpty(exploring.InfeasibleExploration!);
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

    private static EvolutionParetoDefinition Exploring(int capacity = 2) => new(Definition().Objectives, 8,
        EvolutionParetoRepresentative.ClosestToIdeal, capacity);

    private sealed class FrontView : IEvolutionParetoArchiveView<TestGenome>
    {
        private readonly EvolutionParetoDefinition _definition;
        private readonly IReadOnlyList<EvolutionArchiveEntry<TestGenome>> _entries;
        public FrontView(EvolutionParetoDefinition definition, IReadOnlyList<EvolutionArchiveEntry<TestGenome>> entries)
        { _definition = definition; _entries = entries; }
        public EvolutionParetoDefinition? ParetoDefinition => _definition;
        public IReadOnlyList<EvolutionArchiveEntry<TestGenome>>? InfeasibleEntries => null;
        public IReadOnlyList<EvolutionDescriptorDefinition> Descriptors =>
            Array.AsReadOnly(new[] { new EvolutionDescriptorDefinition("__pareto_slot", 0, _definition.Capacity, _definition.Capacity) });
        public string DefinitionHash => "front-view-v1";
        public EvolutionOptimizationDirection Direction => EvolutionOptimizationDirection.Maximize;
        public int Count => _entries.Count;
        public long Version => 100;
        public IReadOnlyList<EvolutionArchiveEntry<TestGenome>> Entries => _entries;
        public EvolutionArchiveEntry<TestGenome>? Best => _entries.Count == 0 ? null : _entries[0];
        public EvolutionArchiveEntry<TestGenome>? Get(EvolutionCellKey cell) => null;
    }

    private static EvolutionArchiveEntry<TestGenome> Member(int slot, long id, string name, double x, double y)
    {
        var pair = Pair(id, name, 1, new[] { x, y });
        return new EvolutionArchiveEntry<TestGenome>(new EvolutionCellKey(new[] { slot }), pair.Item1, pair.Item2);
    }

    [Fact]
    public void SnapshotRejectsDominatedAndOverCapacityFrontViews()
    {
        var dominated = new FrontView(Definition(), new[] { Member(0, 0, "ideal", .1, .1), Member(1, 1, "dominated", .5, .5) });
        Assert.Throws<ArgumentException>(() => new EvolutionArchiveSnapshot<TestGenome>(dominated));
        var overCapacity = new FrontView(Definition(2),
            new[] { Member(0, 0, "a", .1, .9), Member(1, 1, "b", .5, .5), Member(2, 2, "c", .9, .1) });
        Assert.Throws<ArgumentException>(() => new EvolutionArchiveSnapshot<TestGenome>(overCapacity));
        var valid = new FrontView(Definition(), new[] { Member(0, 0, "a", .1, .9), Member(1, 1, "c", .9, .1) });
        Assert.Equal(2, new EvolutionArchiveSnapshot<TestGenome>(valid).Entries.Count);
    }

    [Fact]
    public void ExplorationRankingPrefersTheSmallestLargestViolationNotTheFewestViolations()
    {
        static (EvolutionCandidate<TestGenome>, EvolutionEvaluation) Multi(long id, string name, double[] violations)
        {
            var pair = MapElitesArchiveTests.Create(id, name, 1, 0.5, EvolutionOptimizationDirection.Maximize);
            return (pair.Item1, new EvolutionEvaluation(id, name, EvolutionEvaluationStatus.Completed, 1,
                EvolutionOptimizationDirection.Maximize, pair.Item2.Descriptors, new[] { .5, .5 }, violations,
                pair.Item2.Cost, pair.Item2.Lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(),
                "task", "eval", "config"));
        }

        // One large violation against three small ones: retention minimizes the largest violation first, so the
        // candidate that is closest to feasible on every constraint wins even though it violates more of them.
        foreach (bool reverse in new[] { false, true })
        {
            var archive = new ParetoArchive<TestGenome>(Exploring(1));
            var pairs = new[] { Multi(0, "one-far", new[] { 5d, 0, 0 }), Multi(1, "three-near", new[] { 1d, 1, 1 }) };
            var results = (reverse ? Enumerable.Reverse(pairs) : pairs)
                .Select(pair => archive.TryAdd(pair.Item1, pair.Item2)).ToArray();
            Assert.Equal("three-near", Assert.Single(archive.InfeasibleEntries!).Evaluation.GenomeId);
            Assert.Contains(EvolutionArchiveInsertionResult.RetainedForExploration, results);
        }
    }

    private sealed class ScaledViolationTask : IEvolutionTask<TestGenome>
    {
        private readonly double _scale;
        public ScaledViolationTask(double scale) => _scale = scale;

        // One identity for both scales on purpose: the runs must differ in nothing but the retained pool contents.
        public string Id => "scaled-violation";
        public string VersionHash => "scaled-violation-v1";
        public string EvaluatorVersionHash => "scaled-violation-eval-v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome, genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            double x = Math.Min(1, value / 20d);
            return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1 - x, objectives: new[] { x, 1 - x },
                constraintViolations: new[] { value < 4 ? _scale * (4 - value) : 0 }));
        }
    }

    [Fact]
    public async Task StateHashCoversTheRetainedExplorationPool()
    {
        static Task<EvolutionRunResult<TestGenome>> RunAsync(double scale) =>
            new EvolutionEngine<TestGenome>(new ScaledViolationTask(scale), new SequentialVariation(),
                _ => new ParetoArchive<TestGenome>(Exploring()), new EvolutionEngineOptions
                {
                    RunId = "pool-state-hash",
                    Seed = 5,
                    MaxEvaluationAttempts = 8,
                    MaxProposals = 40,
                    MaxGenerations = 40,
                    ProposalBatchSize = 1,
                    MaxDegreeOfParallelism = 1,
                    MigrationInterval = 0,
                    EnableEvaluationCache = false
                }).RunAsync(new[] { new TestGenome(1) });

        var narrow = await RunAsync(1);
        var wide = await RunAsync(7);

        // Identical feasible fronts, counters and identities; only the violation magnitudes of the separately
        // retained exploration entries differ, so the state hash must still separate the two runs.
        Assert.Equal(narrow.ParetoFront!.Entries.Select(entry => entry.Evaluation.GenomeId),
            wide.ParetoFront!.Entries.Select(entry => entry.Evaluation.GenomeId));
        Assert.Equal(narrow.Counters.CompletedEvaluations, wide.Counters.CompletedEvaluations);
        Assert.Equal(narrow.InfeasibleExploration!.Select(entry => entry.Entry.Evaluation.GenomeId),
            wide.InfeasibleExploration!.Select(entry => entry.Entry.Evaluation.GenomeId));
        Assert.NotEmpty(narrow.InfeasibleExploration!);
        Assert.NotEqual(narrow.StateHash, wide.StateHash);
    }

    [Fact]
    public void DefaultExplorationProbabilityStaysTheDocumentedTenPercent()
    {
        static string Hash(int infeasibleCapacity, ISelectionPolicy<TestGenome>? selection)
        {
            var definition = new EvolutionParetoDefinition(Definition().Objectives, 8,
                EvolutionParetoRepresentative.ClosestToIdeal, infeasibleCapacity);
            return new EvolutionEngine<TestGenome>(new ReachFeasibilityTask(), new IncrementVariation(),
                _ => new ParetoArchive<TestGenome>(definition), new EvolutionEngineOptions
                {
                    RunId = "exploration-probability",
                    Seed = 7,
                    MaxEvaluationAttempts = 4,
                    MaxProposals = 10,
                    MaxGenerations = 10,
                    ProposalBatchSize = 1,
                    MaxDegreeOfParallelism = 1,
                    MigrationInterval = 0
                }, selection: selection).CompatibilityHash;
        }

        // The selection policy's version hash is part of checkpoint compatibility, so the engine's default
        // probability is observable: an enabled pool must default to the documented 0.1, not to fallback-only.
        Assert.Equal(Hash(2, new ParetoEvolutionSelectionPolicy<TestGenome>(.1)), Hash(2, null));
        Assert.NotEqual(Hash(2, new ParetoEvolutionSelectionPolicy<TestGenome>(0)), Hash(2, null));
        Assert.Equal(Hash(0, new ParetoEvolutionSelectionPolicy<TestGenome>(0)), Hash(0, null));
    }

    [Fact]
    public void DominanceIsStrictSoAnIdenticalVectorNeverDominatesItself()
    {
        var definition = Definition();
        var vector = new[] { .3, .7 };
        Assert.False(definition.Dominates(vector, new[] { .3, .7 }));
        Assert.False(definition.Dominates(vector, vector));
        Assert.True(definition.Dominates(new[] { .3, .6 }, vector));
        var boxed = Definition(resolution: .25);
        Assert.False(boxed.Dominates(new[] { .3, .7 }, new[] { .2, .6 }));
    }

    [Fact]
    public void SnapshotRejectsExplorationMetadataWhenThePoolIsNotEnabled()
    {
        var archive = new ParetoArchive<TestGenome>(Exploring()); Add(archive, 0, "invalid", 1, .1, .1, 1);
        Assert.Throws<ArgumentException>(() => new EvolutionArchiveSnapshot<TestGenome>(new MisreportedPoolView(archive)));
    }

    private sealed class MisreportedPoolView(ParetoArchive<TestGenome> source) : IEvolutionParetoArchiveView<TestGenome>
    {
        public EvolutionParetoDefinition? ParetoDefinition => Definition();
        public IReadOnlyList<EvolutionArchiveEntry<TestGenome>>? InfeasibleEntries => source.InfeasibleEntries;
        public IReadOnlyList<EvolutionDescriptorDefinition> Descriptors => source.Descriptors;
        public string DefinitionHash => source.DefinitionHash;
        public EvolutionOptimizationDirection Direction => source.Direction;
        public int Count => source.Count;
        public long Version => source.Version;
        public IReadOnlyList<EvolutionArchiveEntry<TestGenome>> Entries => source.Entries;
        public EvolutionArchiveEntry<TestGenome>? Best => source.Best;
        public EvolutionArchiveEntry<TestGenome>? Get(EvolutionCellKey cell) => source.Get(cell);
    }

    [Fact]
    public void ExplorationIsBoundedSeparateAndNeverADeployableWinner()
    {
        var archive = new ParetoArchive<TestGenome>(Exploring());
        Assert.Equal(EvolutionArchiveInsertionResult.RetainedForExploration, Add(archive, 0, "far", 1000, .1, .1, 3));
        var old = archive.InfeasibleEntries!;
        Add(archive, 1, "close", 10, .2, .2, 1); Add(archive, 2, "middle", 9999, .1, .1, 2);
        Assert.Equal(new[] { "close", "middle" }, archive.InfeasibleEntries!.Select(entry => entry.Evaluation.GenomeId).OrderBy(id => id));
        Assert.Single(old); Assert.Equal(3, archive.Version); Assert.Equal(0, archive.Count);
        Assert.Empty(archive.Entries); Assert.Empty(archive.Front.Entries); Assert.Equal(0, archive.Front.Hypervolume());
        Assert.Null(archive.Best); Assert.Null(archive.Sample(new StableRandom(1, 2)));
        Assert.Null(archive.Get(archive.InfeasibleEntries![0].Cell));
        Add(archive, 3, "feasible", 0, .9, .9);
        Assert.Equal("feasible", archive.Best!.Evaluation.GenomeId);
        Assert.Equal(2, archive.InfeasibleEntries.Count); Assert.Single(archive.Front.Entries);
    }

    [Fact]
    public void ExplorationRankingUsesViolationsNotScalarQualityAndRejectsDuplicateEvaluationIds()
    {
        var archive = new ParetoArchive<TestGenome>(Exploring(1));
        Add(archive, 0, "b", 1, .5, .5, 1);
        Assert.Equal(EvolutionArchiveInsertionResult.NotImproved, Add(archive, 1, "worse", 1e20, 0, 0, 2));
        Assert.Equal(EvolutionArchiveInsertionResult.Rejected, Add(archive, 0, "same-id", 1, .3, .3, .5));
        Assert.Equal(EvolutionArchiveInsertionResult.RetainedForExploration, Add(archive, 2, "a", -1e20, .9, .9, 1));
        Assert.Equal("a", Assert.Single(archive.InfeasibleEntries!).Evaluation.GenomeId);
        Assert.Equal(EvolutionArchiveInsertionResult.NotImproved, Add(archive, 3, "a", 999, 0, 0));
        Assert.Empty(archive.Entries);
        Assert.Throws<ArgumentOutOfRangeException>(() => Exploring(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Exploring(257));
        Assert.NotEqual(Exploring(0).DefinitionHash, Exploring(1).DefinitionHash);
    }

    [Fact]
    public void ExplorationSelectionIsOnlyAnExplicitFallbackAndMigrationNeverCopiesIt()
    {
        var archive = new ParetoArchive<TestGenome>(Exploring());
        Add(archive, 0, "a", 99, .1, .1, 1); Add(archive, 1, "b", 99, .1, .1, 2);
        var policy = new ParetoEvolutionSelectionPolicy<TestGenome>();
        var first = new StableRandom(2, 3); var second = new StableRandom(2, 3);
        for (int i = 0; i < 20; i++)
        {
            var selection = policy.Select(archive, first, int.MaxValue)!;
            Assert.Equal(selection.Parent.Evaluation.GenomeId, policy.Select(archive, second, int.MaxValue)!.Parent.Evaluation.GenomeId);
            Assert.Single(selection.Inspirations); Assert.DoesNotContain(selection.Parent, selection.Inspirations);
        }
        var snapshot = new EvolutionArchiveSnapshot<TestGenome>(archive);
        var nested = new EvolutionArchiveSnapshot<TestGenome>(snapshot);
        Assert.Empty(nested.Entries); Assert.Equal(2, nested.InfeasibleEntries!.Count); Assert.Null(nested.Best);
        Assert.Empty(new ParetoEvolutionMigrationPolicy<TestGenome>().CreateMigrations(new IEvolutionArchiveView<TestGenome>[]
            { snapshot, new ParetoArchive<TestGenome>(Exploring()) }, 2, first));
        Add(archive, 2, "valid", 0, .9, .9);
        Assert.Equal("valid", policy.Select(archive, first, 10)!.Parent.Evaluation.GenomeId);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public void ExplorationRestoreIsAtomicAndInvalidatesEmptyCachedViews()
    {
        var source = new ParetoArchive<TestGenome>(Exploring()); Add(source, 0, "a", 1, .2, .2, 1);
        Add(source, 1, "valid", 1, .5, .5);
        var copy = new ParetoArchive<TestGenome>(Exploring()); Assert.Empty(copy.InfeasibleEntries!); Assert.Empty(copy.Entries);
        var invalid = new EvolutionArchiveEntry<TestGenome>(source.InfeasibleEntries![0].Cell, source.Entries[0].Candidate, source.Entries[0].Evaluation);
        Assert.Throws<ArgumentException>(() => copy.RestoreWithExploration(source.Entries, new[] { invalid }, source.Descriptors, 5));
        Assert.Empty(copy.Entries); Assert.Empty(copy.InfeasibleEntries!); Assert.Equal(0, copy.Version);
        copy.RestoreWithExploration(source.Entries, source.InfeasibleEntries, source.Descriptors, source.Version);
        Assert.Single(copy.Entries); Assert.Single(copy.InfeasibleEntries!); Assert.Equal(source.Version, copy.Version);
        var overflow = new ParetoArchive<TestGenome>(Exploring()); overflow.Restore(Array.Empty<EvolutionArchiveEntry<TestGenome>>(), overflow.Descriptors, long.MaxValue);
        Assert.Throws<OverflowException>(() => Add(overflow, 0, "a", 1, .2, .2, 1)); Assert.Empty(overflow.InfeasibleEntries!);
    }

    [Fact]
    public void ExplorationProbabilityIsExplicitReproducibleAndCheckpointIdentified()
    {
        var archive = new ParetoArchive<TestGenome>(Exploring()); Add(archive, 0, "invalid", 99, 0, 0, 1); Add(archive, 1, "valid", 1, .5, .5);
        var policy = new ParetoEvolutionSelectionPolicy<TestGenome>(.5); var first = new StableRandom(1, 7); var second = new StableRandom(1, 7);
        var parents = new HashSet<string>();
        for (int i = 0; i < 50; i++)
        {
            string parent = policy.Select(archive, first, 0)!.Parent.Evaluation.GenomeId; parents.Add(parent);
            Assert.Equal(parent, policy.Select(archive, second, 0)!.Parent.Evaluation.GenomeId);
        }
        Assert.Equal(2, parents.Count);
        Assert.Equal("invalid", new ParetoEvolutionSelectionPolicy<TestGenome>(1).Select(archive, first, 1)!.Parent.Evaluation.GenomeId);
        Assert.NotEqual(policy.VersionHash, new ParetoEvolutionSelectionPolicy<TestGenome>().VersionHash);
        Assert.NotEqual(policy.VersionHash, new ParetoEvolutionSelectionPolicy<TestGenome>(.1).VersionHash);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParetoEvolutionSelectionPolicy<TestGenome>(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParetoEvolutionSelectionPolicy<TestGenome>(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParetoEvolutionSelectionPolicy<TestGenome>(1.1));
    }

    private sealed class ReachFeasibilityTask : IEvolutionTask<TestGenome>
    {
        public string Id => "reach-feasibility";
        public string VersionHash => "reach-feasibility-v1";
        public string EvaluatorVersionHash => "reach-feasibility-eval-v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome, genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            double x = Math.Min(1, candidate.CanonicalGenome.Genome.Value / 10d);
            return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1 - x, objectives: new[] { x, 1 - x },
                constraintViolations: new[] { (double)Math.Max(0, 3 - candidate.CanonicalGenome.Genome.Value) }));
        }
    }

    private static EvolutionEngine<TestGenome> ExploringEngine(int budget, InMemoryEvolutionCheckpointStore? store = null, bool resume = false,
        int capacity = 2) => new(new ReachFeasibilityTask(), new IncrementVariation(), _ => new ParetoArchive<TestGenome>(Exploring(capacity)),
            new EvolutionEngineOptions
            {
                RunId = "exploration",
                Seed = 7,
                MaxEvaluationAttempts = budget,
                MaxProposals = 100,
                MaxGenerations = 100,
                ProposalBatchSize = 1,
                MaxDegreeOfParallelism = 1,
                MigrationInterval = 0,
                Resume = resume
            },
            checkpointStore: store, genomeCodec: new TestGenomeCodec());

    [Fact]
    public async Task InfeasibleOnlyStartCanReachFeasibilityAndResumeWithoutLeakingWinners()
    {
        var seeds = new[] { new TestGenome(1) }; var store = new InMemoryEvolutionCheckpointStore();
        var partial = await ExploringEngine(2, store).RunAsync(seeds);
        Assert.Null(partial.Best); Assert.Empty(partial.ParetoFront!.Entries);
        Assert.Equal(2, partial.InfeasibleExploration!.Count);
        Assert.All(partial.InfeasibleExploration, entry => Assert.Equal(0, entry.Island));
        Assert.Contains("InfeasibleExploration", JsonSerializer.Serialize(partial));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionInfeasibleEntry<TestGenome>(-1, partial.InfeasibleExploration[0].Entry));
        Assert.Equal(2, Assert.IsAssignableFrom<IEvolutionParetoArchiveView<TestGenome>>(partial.Islands[0]).InfeasibleEntries!.Count);
        var saved = (await store.LoadLatestAsync("exploration"))!;
        var offline = EvolutionEngine<TestGenome>.ReadCheckpoint(saved, new TestGenomeCodec());
        Assert.Empty(offline.ParetoFront!.Entries);
        Assert.All(offline.Entries, entry => Assert.Equal(EvolutionCheckpointEntrySource.InfeasibleExploration, entry.Source));
        var resumed = await ExploringEngine(5, store, true).RunAsync(seeds);
        var full = await ExploringEngine(5).RunAsync(seeds);
        Assert.Equal(full.StateHash, resumed.StateHash);
        Assert.NotNull(full.Best); Assert.All(full.ParetoFront!.Entries, entry => Assert.All(entry.Evaluation.ConstraintViolations, value => Assert.Equal(0, value)));
        Assert.Throws<ArgumentException>(() => new EvolutionInfeasibleEntry<TestGenome>(0, full.Best!));
        var disabled = await ExploringEngine(5, capacity: 0).RunAsync(seeds);
        Assert.Null(disabled.Best); Assert.Equal(1, disabled.Counters.EvaluationAttempts);
    }

    [Theory]
    [InlineData("missing-pool")]
    [InlineData("disabled-pool")]
    [InlineData("feasible-in-pool")]
    [InlineData("pool-in-front")]
    [InlineData("duplicate-pool")]
    [InlineData("slot")]
    [InlineData("count")]
    public async Task ExplorationCheckpointCorruptionIsRejectedBeforeCodecs(string corruption)
    {
        var store = new InMemoryEvolutionCheckpointStore(); await ExploringEngine(2, store).RunAsync(new[] { new TestGenome(1) });
        var saved = (await store.LoadLatestAsync("exploration"))!; var node = JsonNode.Parse(saved.Payload)!;
        var island = node["Islands"]![0]!; var pool = island["InfeasibleEntries"]!.AsArray();
        switch (corruption)
        {
            case "missing-pool": island.AsObject().Remove("InfeasibleEntries"); break;
            case "disabled-pool": island["Pareto"]!["InfeasibleCapacity"] = 0; break;
            case "feasible-in-pool": pool[0]!["Evaluation"]!["ConstraintViolations"] = new JsonArray(0d); break;
            case "pool-in-front": island["Entries"]!.AsArray().Add(JsonNode.Parse(pool[0]!.ToJsonString())); break;
            case "duplicate-pool": pool.Add(JsonNode.Parse(pool[0]!.ToJsonString())); break;
            case "slot": pool[0]!["CellBins"]![0] = 0; break;
            case "count": island["Pareto"]!["InfeasibleCapacity"] = 257; break;
        }
        var corrupt = new EvolutionCheckpoint(saved.RunId, saved.Sequence, saved.CompatibilityHash, node.ToJsonString()); var codec = new CountingCodec();
        Assert.Throws<InvalidDataException>(() => EvolutionEngine<TestGenome>.ReadCheckpoint(corrupt, codec)); Assert.Equal(0, codec.Reads);
    }
}
