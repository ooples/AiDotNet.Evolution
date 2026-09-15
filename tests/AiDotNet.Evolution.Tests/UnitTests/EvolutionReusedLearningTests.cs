using System.Globalization;
using System.Text.Json;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionReusedLearningTests
{
    public static IEnumerable<object?[]> PortfolioCases()
    {
        foreach (EvolutionMeasurementOriginKind? kind in new EvolutionMeasurementOriginKind?[]
        { null, EvolutionMeasurementOriginKind.Measured, EvolutionMeasurementOriginKind.PersistentReuse,
            EvolutionMeasurementOriginKind.RunLocalReuse, EvolutionMeasurementOriginKind.MigrationCopy })
            for (int policy = 0; policy < 3; policy++) yield return new object?[] { kind, policy };
    }

    [Theory]
    [MemberData(nameof(PortfolioCases))]
    public async Task Reused_measurements_never_earn_fresh_operator_reward(EvolutionMeasurementOriginKind? kind, int policy)
    {
        var operators = new IVariationOperator<TestGenome>[] { new IncrementVariation() };
        var portfolio = policy == 0 ? new AdaptiveVariationPortfolio<TestGenome>(operators) :
            new AdaptiveVariationPortfolio<TestGenome>(operators, new EvolutionOperatorRewardPolicy(
                policy == 1 ? EvolutionOperatorRewardKind.ArchiveSuccess : EvolutionOperatorRewardKind.ParentImprovement,
                EvolutionOperatorCostBasis.Evaluation, "test-cost-v1"));
        EvolutionEvaluation parent = Evaluation(0, "parent", 0);
        var entry = new EvolutionArchiveEntry<TestGenome>(new EvolutionCellKey(new[] { 0 }),
            new EvolutionCandidate<TestGenome>(0, new EvolutionCanonicalGenome<TestGenome>(new TestGenome(0), "parent"), parent.Lineage), parent);
        await portfolio.ProposeAsync(new EvolutionVariationContext<TestGenome>(entry, Array.Empty<EvolutionArchiveEntry<TestGenome>>(),
            StableRandom.CreateStream(42, 1), 1, 0));
        var evaluation = WithOrigin(Evaluation(1, "child", 1), kind);
        portfolio.Observe(evaluation, EvolutionArchiveInsertionResult.Inserted);
        Assert.Equal(IsReused(kind) ? 0 : 1, portfolio.Statistics[0].RewardSum);
        Assert.Equal(1, portfolio.Statistics[0].Outcomes);
        var credit = Assert.IsType<EvolutionOperatorCredit>(portfolio.LastCredit);
        Assert.Same(evaluation.MeasurementOrigin, credit.MeasurementOrigin);
        Assert.Equal(evaluation.Cost.CostUnits, credit.EvaluationCostUnits);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(credit));
        Assert.Equal(kind.HasValue, json.RootElement.TryGetProperty("MeasurementOrigin", out _));
        Assert.Throws<InvalidOperationException>(() => portfolio.Observe(evaluation, EvolutionArchiveInsertionResult.Inserted));
        Assert.Equal(1, portfolio.Statistics[0].Outcomes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(EvolutionMeasurementOriginKind.Measured)]
    [InlineData(EvolutionMeasurementOriginKind.PersistentReuse)]
    [InlineData(EvolutionMeasurementOriginKind.RunLocalReuse)]
    [InlineData(EvolutionMeasurementOriginKind.MigrationCopy)]
    public void Reused_measurements_are_not_new_surrogate_training_observations(EvolutionMeasurementOriginKind? kind)
    {
        var evaluation = WithOrigin(Evaluation(1, "child", 1), kind);
        var candidate = new EvolutionCandidate<TestGenome>(1, new EvolutionCanonicalGenome<TestGenome>(new TestGenome(1), "child"), evaluation.Lineage);
        if (IsReused(kind)) Assert.Throws<ArgumentException>(() => new EvolutionSurrogateObservation<TestGenome>(candidate, evaluation));
        else Assert.Same(evaluation, new EvolutionSurrogateObservation<TestGenome>(candidate, evaluation).Evaluation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(EvolutionMeasurementOriginKind.Measured)]
    [InlineData(EvolutionMeasurementOriginKind.PersistentReuse)]
    [InlineData(EvolutionMeasurementOriginKind.RunLocalReuse)]
    [InlineData(EvolutionMeasurementOriginKind.MigrationCopy)]
    public async Task Reused_population_cannot_update_the_cma_distribution(EvolutionMeasurementOriginKind? kind)
    {
        var space = EvolutionSearchSpaceTests.Continuous();
        var seed = space.Sample(StableRandom.CreateStream(1, 0));
        var emitter = new DiagonalCmaEmitter(space, populationSize: 4);
        for (long generation = 1; generation <= 4; generation++)
        {
            var genome = await emitter.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, (ulong)generation, generation));
            emitter.Observe(WithOrigin(Evaluation(generation, genome.Identity, generation), kind), EvolutionArchiveInsertionResult.Inserted);
        }
        Assert.Equal(IsReused(kind) ? 0 : 1, emitter.Updates);
        Assert.Equal(IsReused(kind) ? 1 : 0, emitter.InvalidPopulations);
        Assert.Equal(0, emitter.PendingCount);
        if (IsReused(kind)) { Assert.Equal(0.2, emitter.StepSize); Assert.All(emitter.Variances, variance => Assert.Equal(1, variance)); }
    }

    [Theory]
    [InlineData(EvolutionMeasurementOriginKind.PersistentReuse)]
    [InlineData(EvolutionMeasurementOriginKind.RunLocalReuse)]
    [InlineData(EvolutionMeasurementOriginKind.MigrationCopy)]
    public async Task Mixed_population_excludes_reused_winner_but_learns_from_fresh_members(EvolutionMeasurementOriginKind kind)
    {
        var space = EvolutionSearchSpaceTests.Continuous();
        var seed = space.Sample(StableRandom.CreateStream(1, 0));
        var producerReuse = new DiagonalCmaEmitter(space, populationSize: 4);
        var engineReuse = new DiagonalCmaEmitter(space, populationSize: 4);
        for (long generation = 1; generation <= 4; generation++)
        {
            var first = await producerReuse.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, (ulong)generation, generation));
            var second = await engineReuse.ProposeAsync(EvolutionSearchSpaceTests.Context(seed, null, (ulong)generation, generation));
            Assert.Equal(first.Identity, second.Identity);
            var producerResult = Evaluation(generation, first.Identity, generation == 1 ? 1000 : generation);
            if (generation == 1) producerResult = WithOrigin(producerResult, kind);
            producerReuse.Observe(producerResult, EvolutionArchiveInsertionResult.Inserted);
            engineReuse.Observe(Evaluation(generation, second.Identity, generation == 1 ? 1000 : generation,
                generation == 1 ? EvolutionCacheStatus.Hit : EvolutionCacheStatus.Miss), EvolutionArchiveInsertionResult.Inserted);
        }
        Assert.Equal(1, producerReuse.Updates);
        Assert.Equal(0, producerReuse.InvalidPopulations);
        Assert.Equal(engineReuse.CaptureState(), producerReuse.CaptureState());
    }

    public static IEnumerable<object?[]> ReuseFlagCases()
    {
        foreach (var row in PortfolioCases().Where(row => (int)row[1]! == 0))
            foreach (var cache in new[] { EvolutionCacheStatus.NotChecked, EvolutionCacheStatus.Miss, EvolutionCacheStatus.Hit })
                yield return new[] { row[0], (object)cache };
    }

    [Theory]
    [MemberData(nameof(ReuseFlagCases))]
    public void Reuse_flag_combines_both_provenance_channels_without_changing_JSON(
        EvolutionMeasurementOriginKind? kind, EvolutionCacheStatus cache)
    {
        var evaluation = WithOrigin(Evaluation(1, "child", 1, cache), kind);
        Assert.Equal(cache == EvolutionCacheStatus.Hit || IsReused(kind), evaluation.IsMeasurementReuse);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(evaluation));
        Assert.False(json.RootElement.TryGetProperty("IsMeasurementReuse", out _));
        Assert.Equal(kind.HasValue, json.RootElement.TryGetProperty("MeasurementOrigin", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Portfolio_rejects_pre_origin_learning_checkpoints(int policyKind)
    {
        var child = new IncrementVariation();
        var policy = new EvolutionOperatorRewardPolicy(policyKind == 1
            ? EvolutionOperatorRewardKind.ArchiveSuccess : EvolutionOperatorRewardKind.ParentImprovement,
            EvolutionOperatorCostBasis.Evaluation, "test-cost-v1");
        var portfolio = policyKind == 0 ? new AdaptiveVariationPortfolio<TestGenome>(new[] { child }) :
            new AdaptiveVariationPortfolio<TestGenome>(new[] { child }, policy);
        string previous = EvolutionHash.Combine(new[] { "adaptive-variation-v1", EvolutionHash.EncodeDouble(0.1), child.Id, child.VersionHash });
        if (policyKind != 0)
        {
            string previousPolicy = EvolutionHash.Combine(new[] { "operator-reward-v1", policy.Kind.ToString(),
                policy.CostBasis.ToString(), policy.CostUnitVersionHash, EvolutionHash.EncodeDouble(1), EvolutionHash.EncodeDouble(1) });
            Assert.NotEqual(previousPolicy, policy.VersionHash);
            previous = EvolutionHash.Combine(new[] { "adaptive-variation-credit-v2", previous, previousPolicy });
        }
        string current = portfolio.CaptureState();
        Assert.NotEqual(previous, portfolio.VersionHash);
        Assert.Throws<InvalidDataException>(() => portfolio.RestoreState(current.Replace(portfolio.VersionHash, previous)));
        Assert.Equal(current, portfolio.CaptureState());
        portfolio.RestoreState(current);
    }

    [Fact]
    public void Cma_rejects_pre_origin_learning_checkpoints()
    {
        var space = EvolutionSearchSpaceTests.Continuous();
        var emitter = new DiagonalCmaEmitter(space, populationSize: 4);
        string previous = EvolutionHash.Combine(new[] { "diagonal-cma-v1", space.VersionHash, "4",
            EvolutionParameterValue.Numeric(0.2).Canonical, EvolutionOptimizationDirection.Maximize.ToString() });
        string current = emitter.CaptureState();
        Assert.NotEqual(previous, emitter.VersionHash);
        Assert.Throws<ArgumentException>(() => emitter.RestoreState(current.Replace(emitter.VersionHash, previous)));
        Assert.Equal(current, emitter.CaptureState());
        emitter.RestoreState(current);
    }

    [Theory]
    [InlineData(EvolutionDispatchMode.Batch, EvolutionMeasurementOriginKind.PersistentReuse)]
    [InlineData(EvolutionDispatchMode.Batch, EvolutionMeasurementOriginKind.RunLocalReuse)]
    [InlineData(EvolutionDispatchMode.Batch, EvolutionMeasurementOriginKind.MigrationCopy)]
    [InlineData(EvolutionDispatchMode.Continuous, EvolutionMeasurementOriginKind.PersistentReuse)]
    [InlineData(EvolutionDispatchMode.Continuous, EvolutionMeasurementOriginKind.RunLocalReuse)]
    [InlineData(EvolutionDispatchMode.Continuous, EvolutionMeasurementOriginKind.MigrationCopy)]
    public async Task Engine_delivers_reuse_and_current_cost_once_without_reward(
        EvolutionDispatchMode dispatch, EvolutionMeasurementOriginKind kind)
    {
        var task = new ReuseTask(kind);
        var portfolio = new AdaptiveVariationPortfolio<TestGenome>(new[] { new UniqueVariation() });
        var observer = new EvaluationRecordingObserver();
        var options = new EvolutionEngineOptions
        {
            RunId = "reuse-learning-run",
            MaxEvaluationAttempts = 5,
            MaxProposals = 5,
            MaxGenerations = 20,
            ProposalBatchSize = 1,
            MaxDegreeOfParallelism = 1,
            MaxInFlight = 1,
            Dispatch = dispatch,
            EnableEvaluationCache = false,
            IslandCount = 1,
            MigrationInterval = 0
        };
        var engine = new EvolutionEngine<TestGenome>(task, portfolio,
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 100) }),
            options, observer: observer);
        await engine.RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(5, task.Calls);
        Assert.Equal(4, portfolio.Statistics[0].Outcomes);
        Assert.Equal(0, portfolio.Statistics[0].RewardSum);
        Assert.Equal(10, observer.Evaluations.Sum(evaluation => evaluation.Cost.CostUnits));
        Assert.All(observer.Evaluations, evaluation =>
        {
            Assert.NotEqual(EvolutionCacheStatus.Hit, evaluation.CacheStatus);
            Assert.True(evaluation.IsMeasurementReuse);
            Assert.Equal(kind, evaluation.MeasurementOrigin!.Kind);
            Assert.Equal(7, evaluation.MeasurementOrigin.OriginalCostUnits);
            Assert.Equal(1, evaluation.Cost.AttemptCount);
        });
        Assert.Equal(2, portfolio.LastCredit!.EvaluationCostUnits);
    }

    private sealed class UniqueVariation : IVariationOperator<TestGenome>
    {
        public string Id => "unique";
        public string VersionHash => "unique-v1";
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            new(new TestGenome(checked((int)context.Generation + 1)));
    }

    private sealed class ReuseTask(EvolutionMeasurementOriginKind kind) : IEvolutionTask<TestGenome>
    {
        private readonly SyntheticEvolutionTask _inner = new();
        public string Id => "reuse-learning";
        public string VersionHash => "reuse-learning-v1";
        public string EvaluatorVersionHash => _inner.EvaluatorVersionHash;
        public int Calls => _inner.Calls;
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            _inner.CanonicalizeAsync(genome, cancellationToken);
        public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            var measured = await _inner.EvaluateAsync(candidate, context, cancellationToken);
            var origin = Origin(context.EvaluationId.ToString(CultureInfo.InvariantCulture)).AsReused(kind);
            return EvolutionTaskResult.Completed(measured.Quality!.Value, measured.Descriptors, costUnits: 2).WithMeasurementOrigin(origin);
        }
    }

    private static bool IsReused(EvolutionMeasurementOriginKind? kind) => kind.HasValue && kind != EvolutionMeasurementOriginKind.Measured;
    private static EvolutionEvaluation WithOrigin(EvolutionEvaluation evaluation, EvolutionMeasurementOriginKind? kind)
    {
        if (!kind.HasValue) return evaluation;
        var origin = Origin();
        return evaluation.WithMeasurementOrigin(kind == EvolutionMeasurementOriginKind.Measured ? origin : origin.AsReused(kind.Value));
    }

    private static EvolutionMeasurementOrigin Origin(string sample = "sample") => new(EvolutionHash.Compute("learning-scope"),
        "acquisition-run", "measurement", new[] { sample }, new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero), 7, "test-cost-v1", "stats-v1");

    private static EvolutionEvaluation Evaluation(long generation, string genomeId, double quality,
        EvolutionCacheStatus cache = EvolutionCacheStatus.Miss) => new(generation, genomeId,
        EvolutionEvaluationStatus.Completed, quality, EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["x"] = 1 },
        Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 0),
        new EvolutionLineage(null, null, "test", null, generation, 0, (ulong)generation), cache,
        Array.Empty<EvolutionDiagnostic>(), "task", "evaluator", "config");
}
