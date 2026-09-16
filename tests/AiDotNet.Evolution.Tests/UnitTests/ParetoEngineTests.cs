using System.Globalization;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class ParetoEngineTests
{
    private static EvolutionEngineOptions Options(int budget = 32, int workers = 1, bool resume = false) => new()
    {
        RunId = "pareto-test",
        Seed = 27,
        MaxEvaluationAttempts = budget,
        MaxProposals = 2000,
        ProposalBatchSize = 4,
        MaxDegreeOfParallelism = workers,
        IslandCount = 2,
        MigrationInterval = 2,
        MigrantsPerIsland = 2,
        Resume = resume,
        CheckpointInterval = 4
    };

    private static EvolutionEngine<double> Engine(EvolutionEngineOptions options, IEvolutionCheckpointStore? store = null,
        EvolutionParetoDefinition? definition = null) => new(new TaskFixture(), new Variation(),
            _ => new ParetoArchive<double>(definition ?? ParetoArchiveTests.Definition()), options,
            selection: new ParetoEvolutionSelectionPolicy<double>(), migration: new ParetoMigrationPolicy<double>(),
            checkpointStore: store, genomeCodec: new Codec());

    [Fact]
    public async Task FullEnginePreservesFrontAcrossWorkersMigrationAndCheckpointResume()
    {
        var seeds = new[] { .1, .3, .6, .9 };
        var serial = await Engine(Options()).RunAsync(seeds);
        var parallel = await Engine(Options(workers: 4)).RunAsync(seeds);
        Assert.Equal(serial.StateHash, parallel.StateHash);
        Assert.Equal(32, serial.Counters.EvaluationAttempts);
        Assert.All(serial.Islands, island => Assert.NotNull(island.GetParetoDefinition()));
        Assert.True(serial.ParetoFront().Count > 1);
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(16), store).RunAsync(seeds);
        var resumed = await Engine(Options(resume: true), store).RunAsync(Array.Empty<double>());
        Assert.Equal(serial.StateHash, resumed.StateHash);
        Assert.Equal(serial.ParetoFront().Select(entry => entry.Evaluation.GenomeId), resumed.ParetoFront().Select(entry => entry.Evaluation.GenomeId));
        Assert.Equal(serial.Islands.Select(island => island.Hypervolume()), resumed.Islands.Select(island => island.Hypervolume()));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("global")]
    [InlineData("history")]
    [InlineData("stopping")]
    [InlineData("named")]
    public void ScalarOnlySettingsCannotSilentlyReplaceFront(string kind)
    {
        var options = Options();
        if (kind == "target") options.TargetQuality = 1;
        if (kind == "global") options.GlobalEliteCount = 1;
        if (kind == "history") options.HistorySize = 1;
        if (kind == "stopping") options.EarlyStopping.PatienceEvaluations = 2;
        if (kind == "named")
        {
            options.EarlyStopping.PatienceEvaluations = 2; options.EarlyStopping.Metric = EvolutionEarlyStoppingMetric.ParetoHypervolume;
            options.EarlyStopping.MetricName = "scalar";
        }
        Assert.Throws<ArgumentException>(() => Engine(options));
    }

    [Fact]
    public void WrongSelectionMigrationAndScalarFrontQueriesFailClosed()
    {
        Assert.Throws<ArgumentException>(() => new EvolutionEngine<double>(new TaskFixture(), new Variation(),
            _ => new ParetoArchive<double>(ParetoArchiveTests.Definition()), Options()));
        var scalar = new MapElitesArchive<double>(new[] { new EvolutionDescriptorDefinition("x", 0, 1, 1) });
        Assert.Throws<ArgumentException>(() => scalar.ParetoFront());
        Assert.Throws<ArgumentException>(() => scalar.Hypervolume());
        Assert.Throws<ArgumentException>(() => new ParetoEvolutionSelectionPolicy<double>().Select(scalar, StableRandom.CreateStream(1, 1), 0));
    }

    [Fact]
    public async Task HypervolumeStoppingDoesNotStopWhileConstantScalarFrontImproves()
    {
        var options = Options(8); options.IslandCount = 1; options.ProposalBatchSize = 1;
        options.EarlyStopping.PatienceEvaluations = 2; options.EarlyStopping.MinimumImprovement = .00001;
        options.EarlyStopping.Metric = EvolutionEarlyStoppingMetric.ParetoHypervolume;
        var result = await Engine(options).RunAsync(new[] { .1, .2, .3, .4, .5, .6, .7, .8 });
        Assert.Equal(EvolutionStopReason.EvaluationBudgetReached, result.StopReason);
        Assert.Equal(8, result.Counters.CompletedEvaluations);
        Assert.Equal(1, result.Best!.Evaluation.Quality);
    }

    [Fact]
    public async Task HypervolumePlateauAndResumeContinueSamePatience()
    {
        EvolutionEngineOptions Config(int budget, bool resume = false)
        {
            var options = Options(budget, resume: resume); options.IslandCount = 1; options.ProposalBatchSize = 1;
            options.EarlyStopping.PatienceEvaluations = 3; options.EarlyStopping.MinimumImprovement = .5;
            options.EarlyStopping.Metric = EvolutionEarlyStoppingMetric.ParetoHypervolume; return options;
        }
        var seeds = new[] { .1, .2, .3, .4, .5, .6, .7, .8 };
        var full = await Engine(Config(20)).RunAsync(seeds);
        Assert.Equal(EvolutionStopReason.EarlyStopped, full.StopReason);
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Config(2), store).RunAsync(seeds);
        var resumed = await Engine(Config(20, true), store).RunAsync(Array.Empty<double>());
        Assert.Equal(full.StateHash, resumed.StateHash);
    }

    [Fact]
    public async Task ChangedObjectiveContractCannotResume()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(4), store).RunAsync(new[] { .1, .2, .3, .4 });
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => Engine(Options(8, resume: true), store,
            ParetoArchiveTests.Definition(tolerance: .1)).RunAsync(Array.Empty<double>()));
    }

    [Fact]
    public async Task ZeroThresholdStillStopsOnUnchangedFrontVolume()
    {
        var options = Options(8); options.IslandCount = 1; options.ProposalBatchSize = 1;
        options.EarlyStopping.PatienceEvaluations = 1; options.EarlyStopping.MinimumImprovement = 0;
        options.EarlyStopping.Metric = EvolutionEarlyStoppingMetric.ParetoHypervolume;
        var result = await Engine(options).RunAsync(new[] { 0.0, 1.0, .5 });
        Assert.Equal(EvolutionStopReason.EarlyStopped, result.StopReason);
        Assert.Equal(2, result.Counters.EvaluationAttempts);
    }

    [Fact]
    public async Task MissingConstraintsAreReportedAndNeverBecomeWinners()
    {
        var result = await Engine(Options(4), definition: ParetoArchiveTests.Definition(constraints: 1)).RunAsync(new[] { .1, .2, .3, .4 });
        Assert.Empty(result.ParetoFront()); Assert.Null(result.Best);
        Assert.Contains(result.RetainedFailures, failure => failure.Code == "pareto_not_feasible");
    }

    private sealed class TaskFixture : IEvolutionTask<double>
    {
        public string Id => "pareto-test";
        public string VersionHash => "pareto-test-v1";
        public string EvaluatorVersionHash => "pareto-eval-v1";
        public ValueTask<EvolutionCanonicalGenome<double>> CanonicalizeAsync(double genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<double>(genome, EvolutionHash.EncodeDouble(genome)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<double> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            double x = candidate.CanonicalGenome.Genome;
            return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1, objectives: new[] { x, x }));
        }
    }
    private sealed class Variation : IVariationOperator<double>
    {
        public string Id => "pareto-variation";
        public string VersionHash => "pareto-variation-v1";
        public ValueTask<double> ProposeAsync(EvolutionVariationContext<double> context, CancellationToken cancellationToken = default) =>
            new(context.Random.NextDouble());
    }
    private sealed class Codec : IEvolutionGenomeCodec<double>
    {
        public string Id => "double-codec";
        public string VersionHash => "double-codec-v1";
        public string Serialize(double genome) => genome.ToString("R", CultureInfo.InvariantCulture);
        public double Deserialize(string payload) => double.Parse(payload, CultureInfo.InvariantCulture);
    }
}
