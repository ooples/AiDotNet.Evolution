using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class DiagonalCmaEmitterTests
{
    [Fact]
    public async Task LearnsStepSizeAndDiagonalCovarianceOnAContinuousFixture()
    {
        var space = EvolutionSearchSpaceTests.Continuous();
        var seed = space.CreateGenome(new Dictionary<string, EvolutionParameterValue>
        { ["x0"] = EvolutionParameterValue.Numeric(4), ["x1"] = EvolutionParameterValue.Numeric(4) });
        var emitter = new DiagonalCmaEmitter(space, populationSize: 8);
        double best = 32;
        for (long generation = 1; generation <= 256; generation++)
        {
            var genome = await emitter.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, (ulong)generation + 100, generation));
            double loss = Loss(genome); best = Math.Min(best, loss);
            emitter.Observe(Evaluation(genome, generation, -loss), EvolutionArchiveInsertionResult.Inserted);
        }
        Assert.True(best < 0.1, "The deterministic development fixture did not improve sufficiently: " + best);
        Assert.Equal(32, emitter.Updates); Assert.Equal(0, emitter.PendingCount);
        Assert.NotEqual(0.2, emitter.StepSize); Assert.Contains(emitter.Variances, value => value != 1);
        Assert.All(emitter.Variances, value => Assert.InRange(value, 1e-14, 1e8));
    }

    [Fact]
    public async Task PendingAndPartiallyObservedPopulationResumeExactly()
    {
        var space = EvolutionSearchSpaceTests.Continuous(); var seed = space.Sample(StableRandom.CreateStream(10, 0));
        var original = new DiagonalCmaEmitter(space, populationSize: 4);
        var first = await original.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, 1, 1));
        var pending = await original.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, 2, 2));
        original.Observe(Evaluation(first, 1, -Loss(first)), null);
        var restored = new DiagonalCmaEmitter(space, populationSize: 4); restored.RestoreState(original.CaptureState());
        Assert.Equal(original.CaptureState(), restored.CaptureState()); Assert.Equal(1, restored.PendingCount);
        original.Observe(Evaluation(pending, 2, -Loss(pending)), null); restored.Observe(Evaluation(pending, 2, -Loss(pending)), null);
        for (long generation = 3; generation <= 40; generation++)
        {
            var a = await original.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, (ulong)generation, generation));
            var b = await restored.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, (ulong)generation, generation));
            Assert.Equal(a.Identity, b.Identity);
            var evaluation = Evaluation(a, generation, -Loss(a)); original.Observe(evaluation, null); restored.Observe(evaluation, null);
        }
        Assert.Equal(original.CaptureState(), restored.CaptureState()); Assert.Equal(10, restored.Updates);
    }

    [Fact]
    public async Task StalePopulationsDoNotOverwriteNewerDistributionLearning()
    {
        var space = EvolutionSearchSpaceTests.Continuous(); var seed = space.Sample(StableRandom.CreateStream(1, 0));
        var emitter = new DiagonalCmaEmitter(space, populationSize: 4);
        var genomes = new List<EvolutionSearchGenome>();
        for (long generation = 1; generation <= 8; generation++)
            genomes.Add(await emitter.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, (ulong)generation, generation)));
        for (int i = 0; i < 4; i++) emitter.Observe(Evaluation(genomes[i], i + 1, -Loss(genomes[i])), null);
        Assert.Equal(1, emitter.Updates);
        double step = emitter.StepSize; double[] variances = emitter.Variances.ToArray();
        for (int i = 4; i < 8; i++) emitter.Observe(Evaluation(genomes[i], i + 1, 1e6), null);
        Assert.Equal(1, emitter.Updates); Assert.Equal(1, emitter.StalePopulations);
        Assert.Equal(step, emitter.StepSize); Assert.Equal(variances, emitter.Variances);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cached")]
    [InlineData("infeasible")]
    [InlineData("direction")]
    [InlineData("unmeasured")]
    public async Task OnlyFreshFeasibleMeasurementsTrainTheEmitter(string kind)
    {
        var space = EvolutionSearchSpaceTests.Continuous(); var seed = space.Sample(StableRandom.CreateStream(1, 0));
        var emitter = new DiagonalCmaEmitter(space, populationSize: 4);
        for (long generation = 1; generation <= 4; generation++)
        {
            var genome = await emitter.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, (ulong)generation, generation));
            EvolutionEvaluation evaluation = Evaluation(genome, generation, 1e6,
                status: kind == "failed" ? EvolutionEvaluationStatus.Failed : EvolutionEvaluationStatus.Completed,
                cached: kind == "cached", infeasible: kind == "infeasible", attempts: kind == "unmeasured" ? 0 : 1,
                direction: kind == "direction" ? EvolutionOptimizationDirection.Minimize : EvolutionOptimizationDirection.Maximize);
            emitter.Observe(evaluation, EvolutionArchiveInsertionResult.Inserted);
            emitter.Observe(evaluation, EvolutionArchiveInsertionResult.Inserted); // duplicate feedback cannot count twice
        }
        Assert.Equal(0, emitter.Updates); Assert.Equal(1, emitter.InvalidPopulations); Assert.Equal(0, emitter.PendingCount);
        Assert.Equal(0.2, emitter.StepSize);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task EngineWorkerCountsAndCheckpointContinuationPreserveLearning(int batchSize)
    {
        var space = EvolutionSearchSpaceTests.Continuous();
        var store = new InMemoryEvolutionCheckpointStore();
        int boundary = 1 + batchSize * 4;
        await Run(space, new DiagonalCmaEmitter(space, populationSize: 6), boundary, 1, batchSize, store, false);
        var resumedEmitter = new DiagonalCmaEmitter(space, populationSize: 6);
        var resumed = await Run(space, resumedEmitter, 1 + batchSize * 8, 4, batchSize, store, true);
        var completeEmitter = new DiagonalCmaEmitter(space, populationSize: 6);
        var complete = await Run(space, completeEmitter, 1 + batchSize * 8, 1, batchSize, new InMemoryEvolutionCheckpointStore(), false);
        Assert.Equal(completeEmitter.CaptureState(), resumedEmitter.CaptureState());
        Assert.Equal(complete.StateHash, resumed.StateHash);
    }

    [Theory]
    [InlineData("variance")]
    [InlineData("mean")]
    [InlineData("step")]
    [InlineData("identity")]
    [InlineData("duplicate")]
    [InlineData("score")]
    [InlineData("population")]
    [InlineData("conflictingEpoch")]
    [InlineData("unreachableInitialState")]
    public async Task InvalidRestoreDoesNotPartiallyMutateLearning(string fault)
    {
        var space = EvolutionSearchSpaceTests.Continuous(); var seed = space.Sample(StableRandom.CreateStream(1, 0));
        var emitter = new DiagonalCmaEmitter(space, populationSize: 4);
        await emitter.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, 1, 1));
        string before = emitter.CaptureState(); JsonNode state = JsonNode.Parse(before)!;
        if (fault == "variance") state["Current"]!["Variances"]![0] = -1;
        if (fault == "mean") state["Current"]!["Mean"]![0] = 2;
        if (fault == "step") state["Current"]!["Step"] = 0;
        if (fault == "identity") state["VersionHash"] = "other";
        if (fault == "duplicate") state["Cohorts"]![0]!["Samples"]!.AsArray().Add(state["Cohorts"]![0]!["Samples"]![0]!.DeepClone());
        if (fault == "score") state["Cohorts"]![0]!["Samples"]![0]!["Score"] = 1;
        if (fault == "population") state["OpenCohort"] = 99;
        if (fault == "conflictingEpoch") state["Cohorts"]![0]!["Distribution"]!["Step"] = 0.5;
        if (fault == "unreachableInitialState")
        {
            state["Cohorts"]!.AsArray().Clear(); state["OpenCohort"] = null;
            state["NextCohort"] = 0; state["LastGeneration"] = 0;
        }
        Assert.ThrowsAny<ArgumentException>(() => emitter.RestoreState(state.ToJsonString()));
        Assert.Equal(before, emitter.CaptureState());
    }

    [Fact]
    public async Task PendingWorkIsBoundedAndGenerationsCannotBeReused()
    {
        var space = EvolutionSearchSpaceTests.Continuous(1); var seed = space.Sample(StableRandom.CreateStream(1, 0));
        var emitter = new DiagonalCmaEmitter(space, populationSize: 4);
        for (long generation = 1; generation <= 1024; generation++)
            await emitter.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, (ulong)generation, generation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => emitter.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, 1025, 1025)).AsTask());
        Assert.Equal(1024, emitter.PendingCount);
        await Assert.ThrowsAsync<ArgumentException>(() => emitter.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, 1, 1)).AsTask());
    }

    [Fact]
    public void RejectsUnsupportedSpacesAndIncompatibleConfigurations()
    {
        var space = EvolutionSearchSpaceTests.Continuous();
        Assert.Throws<ArgumentException>(() => new DiagonalCmaEmitter(EvolutionSearchSpaceTests.Mixed()));
        Assert.Throws<ArgumentException>(() => new DiagonalCmaEmitter(EvolutionSearchSpaceTests.Continuous(33)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagonalCmaEmitter(space, initialStepSize: 1e-20));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagonalCmaEmitter(space, populationSize: 3));
        var emitter = new DiagonalCmaEmitter(space);
        Assert.Throws<ArgumentException>(() => new DiagonalCmaEmitter(space, initialStepSize: 0.3).RestoreState(emitter.CaptureState()));
        emitter.RestoreState(emitter.CaptureState()); Assert.Equal(0, emitter.Updates);
        Assert.Equal(new[] { 1d, 1 }, emitter.Variances);
    }

    private static double Loss(EvolutionSearchGenome genome) => genome.Values.Values.Sum(value => value.Number * value.Number);
    private static EvolutionEvaluation Evaluation(EvolutionSearchGenome genome, long generation, double score,
        EvolutionEvaluationStatus status = EvolutionEvaluationStatus.Completed, bool cached = false, bool infeasible = false, int attempts = 1,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize) => new(
            generation, genome.Identity, status, status == EvolutionEvaluationStatus.Completed ? score : null, direction,
            new Dictionary<string, double> { ["x"] = 0 }, Array.Empty<double>(), infeasible ? new[] { 1d } : Array.Empty<double>(),
            new EvolutionEvaluationCost(TimeSpan.Zero, attempts, attempts), new EvolutionLineage(null, null, "cma", null, generation, 0, (ulong)generation),
            cached ? EvolutionCacheStatus.Hit : EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
    private static Task<EvolutionRunResult<EvolutionSearchGenome>> Run(EvolutionSearchSpace space, DiagonalCmaEmitter emitter, int budget,
        int workers, int batch, IEvolutionCheckpointStore store, bool resume)
    {
        var task = new EvolutionSearchTask(space, "square", "v1", "v1", (genome, _, _) => new ValueTask<EvolutionTaskResult>(
            EvolutionTaskResult.Completed(-Loss(genome), new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1)));
        var engine = new EvolutionEngine<EvolutionSearchGenome>(task, emitter,
            _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", -5, 5, 10) }),
            new EvolutionEngineOptions
            {
                RunId = "cma",
                Seed = 10,
                MaxEvaluationAttempts = budget,
                MaxProposals = budget * 4,
                ProposalBatchSize = batch,
                MaxDegreeOfParallelism = workers,
                Resume = resume,
                MigrationInterval = 0
            },
            checkpointStore: store, genomeCodec: space);
        return engine.RunAsync(new[] { space.Sample(StableRandom.CreateStream(7, 0)) });
    }
}
