using System.Globalization;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class ParetoIntegrationGuardTests
{
    private static EvolutionParetoDefinition Definition(int? count = 1, int exploration = 0) => new(new[]
    {
        new EvolutionObjectiveDefinition("x", EvolutionOptimizationDirection.Minimize, 0, 1),
        new EvolutionObjectiveDefinition("y", EvolutionOptimizationDirection.Maximize, 0, 1)
    }, infeasibleCapacity: exploration, constraintCount: count);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void DeclaredConstraintsRejectMissingAndExtraViolationsInBothPools(int count)
    {
        var archive = new ParetoArchive<double>(Definition(exploration: 4));
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 1);
        var candidate = new EvolutionCandidate<double>(0, new EvolutionCanonicalGenome<double>(.5, "g"), lineage);
        foreach (double violation in new[] { 0.0, 1.0 })
        {
            var evaluation = new EvolutionEvaluation(0, "g", EvolutionEvaluationStatus.Completed, 1, EvolutionOptimizationDirection.Maximize,
                new Dictionary<string, double>(), new[] { .5, .5 }, Enumerable.Repeat(violation, count),
                new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "t", "e", "c");
            Assert.Equal(EvolutionArchiveInsertionResult.Rejected, archive.TryAdd(candidate, evaluation));
        }
        Assert.Empty(archive.Entries); Assert.Empty(archive.InfeasibleEntries!);
    }

    [Fact]
    public async Task ConstraintShapeIsCheckpointedAndChangingItRefusesResume()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        var first = await Engine(Definition(), 2, store).RunAsync(new[] { .2, .6 });
        var resumed = await Engine(Definition(), 2, store, true).RunAsync(Array.Empty<double>());
        Assert.Equal(first.StateHash, resumed.StateHash);
        Assert.Equal(1, resumed.ParetoFront!.Definition.ConstraintCount);
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => Engine(Definition(null), 4, store, true).RunAsync(Array.Empty<double>()));
        Assert.NotEqual(Definition().DefinitionHash, Definition(null).DefinitionHash);
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Definition(65));
    }

    [Fact]
    public async Task ZeroImprovementThresholdDoesNotResetHypervolumePatience()
    {
        var options = Options(8); options.EarlyStopping.PatienceEvaluations = 1;
        options.EarlyStopping.MinimumImprovement = 0; options.EarlyStopping.Metric = EvolutionEarlyStoppingMetric.ParetoHypervolume;
        var engine = new EvolutionEngine<double>(new TaskFixture(), new Variation(), _ => new ParetoArchive<double>(Definition()), options);
        var result = await engine.RunAsync(new[] { 0.0, 1.0, .5 });
        Assert.Equal(EvolutionStopReason.EarlyStopped, result.StopReason);
        Assert.Equal(2, result.Counters.EvaluationAttempts);
    }

    private static EvolutionEngineOptions Options(int budget, bool resume = false) => new()
    { RunId = "pareto-guards", MaxEvaluationAttempts = budget, MaxProposals = 100, ProposalBatchSize = 1, Resume = resume };
    private static EvolutionEngine<double> Engine(EvolutionParetoDefinition definition, int budget, IEvolutionCheckpointStore store, bool resume = false) =>
        new(new TaskFixture(), new Variation(), _ => new ParetoArchive<double>(definition), Options(budget, resume), checkpointStore: store, genomeCodec: new Codec());
    private sealed class TaskFixture : IEvolutionTask<double>
    {
        public string Id => "pareto-guard-task";
        public string VersionHash => "v1";
        public string EvaluatorVersionHash => "v1";
        public ValueTask<EvolutionCanonicalGenome<double>> CanonicalizeAsync(double genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<double>(genome, EvolutionHash.EncodeDouble(genome)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<double> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            double x = candidate.CanonicalGenome.Genome;
            return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1, objectives: new[] { x, x }, constraintViolations: new[] { 0.0 }));
        }
    }
    private sealed class Variation : IVariationOperator<double>
    {
        public string Id => "pareto-guard-variation";
        public string VersionHash => "v1";
        public ValueTask<double> ProposeAsync(EvolutionVariationContext<double> context, CancellationToken cancellationToken = default) => new(context.Random.NextDouble());
    }
    private sealed class Codec : IEvolutionGenomeCodec<double>
    {
        public string Id => "pareto-guard-codec";
        public string VersionHash => "v1";
        public string Serialize(double genome) => genome.ToString("R", CultureInfo.InvariantCulture);
        public double Deserialize(string payload) => double.Parse(payload, CultureInfo.InvariantCulture);
    }
}
