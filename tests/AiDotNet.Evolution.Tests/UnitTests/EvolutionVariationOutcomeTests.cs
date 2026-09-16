using System.Globalization;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionVariationOutcomeTests
{
    [Theory]
    [InlineData(EvolutionDispatchMode.Batch)]
    [InlineData(EvolutionDispatchMode.Continuous)]
    public async Task EveryNonSeedProposalReceivesExactlyOneOrderedOutcome(EvolutionDispatchMode dispatch)
    {
        var variation = new RecordingVariation();
        EvolutionRunResult<TestGenome> result = await Run(variation, Options(20, dispatch));
        Assert.Equal(result.Counters.Proposals - 1, variation.Outcomes.Count);
        Assert.Equal(variation.Outcomes.Count, variation.Outcomes.Select(e => e.Lineage.Generation).Distinct().Count());
        Assert.All(variation.Outcomes, e => Assert.True(e.Lineage.Generation > 0));
        Assert.Equal(variation.Outcomes.OrderBy(e => e.EvaluationId), variation.Outcomes);
    }

    [Fact]
    public async Task RetriesProduceOneTerminalFeedbackWithAllAttemptCosts()
    {
        var variation = new RecordingVariation();
        EvolutionEngineOptions options = Options(8);
        options.MaxRetries = 1;
        await Run(variation, options, task: new FailOnceEvolutionTask());
        Assert.NotEmpty(variation.Outcomes);
        Assert.All(variation.Outcomes, outcome =>
        {
            Assert.Equal(EvolutionEvaluationStatus.Completed, outcome.Status);
            Assert.Equal(2, outcome.Cost.AttemptCount);
            Assert.Equal(3, outcome.Cost.CostUnits);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CacheHitsAndDuplicateRejectionsStillReachFeedback(bool cache)
    {
        var variation = new RecordingVariation(repeatParent: true);
        EvolutionEngineOptions options = Options(5);
        options.MaxProposals = 5;
        options.EnableEvaluationCache = cache;
        await Run(variation, options);
        Assert.Equal(4, variation.Outcomes.Count);
        Assert.All(variation.Outcomes, e =>
        {
            Assert.Equal(0, e.Cost.AttemptCount);
            Assert.Equal(cache ? EvolutionCacheStatus.Hit : EvolutionCacheStatus.NotChecked, e.CacheStatus);
        });
    }

    [Fact]
    public async Task ProposalFailuresAreAttributedButSeedsAndMigrationAreNot()
    {
        var variation = new RecordingVariation(failProposal: true);
        EvolutionEngineOptions options = Options(8);
        options.IslandCount = 2;
        options.MigrationInterval = 1;
        options.MaxProposals = 8;
        await Run(variation, options);
        Assert.NotEmpty(variation.Outcomes);
        Assert.All(variation.Outcomes, e => Assert.Equal(EvolutionEvaluationStatus.Failed, e.Status));
        Assert.Equal(variation.Proposals, variation.Outcomes.Count);
    }

    [Fact]
    public async Task FeedbackExceptionsFailTheRunInsteadOfBeingSwallowedAsObserverErrors()
    {
        var variation = new RecordingVariation(failFeedback: true);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(variation, Options(4)));
        Assert.Equal("feedback failed", error.Message);
    }

    [Theory]
    [InlineData(EvolutionDispatchMode.Batch)]
    [InlineData(EvolutionDispatchMode.Continuous)]
    public async Task LearnedStateIsDeterministicAcrossWorkerCounts(EvolutionDispatchMode dispatch)
    {
        var first = new RecordingVariation();
        var second = new RecordingVariation();
        EvolutionEngineOptions parallel = Options(30, dispatch);
        parallel.MaxDegreeOfParallelism = 4;
        EvolutionRunResult<TestGenome> serialResult = await Run(first, Options(30, dispatch));
        EvolutionRunResult<TestGenome> parallelResult = await Run(second, parallel);
        Assert.Equal(first.CaptureState(), second.CaptureState());
        Assert.Equal(serialResult.StateHash, parallelResult.StateHash);
    }

    [Fact]
    public async Task LearnedStateResumesTheSameTrajectoryAtACompleteBatchBoundary()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        var first = new RecordingVariation();
        await Run(first, Options(8), store);
        var resumed = new RecordingVariation();
        EvolutionEngineOptions resumeOptions = Options(16);
        resumeOptions.Resume = true;
        EvolutionRunResult<TestGenome> actual = await Run(resumed, resumeOptions, store);
        var full = new RecordingVariation();
        EvolutionRunResult<TestGenome> expected = await Run(full, Options(16), new InMemoryEvolutionCheckpointStore());
        Assert.Equal(full.CaptureState(), resumed.CaptureState());
        Assert.Equal(expected.StateHash, actual.StateHash);
        Assert.True(resumed.LearnedOutcomes > resumed.Outcomes.Count);
    }

    internal static EvolutionEngineOptions Options(int attempts, EvolutionDispatchMode dispatch = EvolutionDispatchMode.Batch) => new()
    {
        RunId = "variation-feedback",
        Seed = 91,
        MaxEvaluationAttempts = attempts,
        MaxProposals = 100,
        MaxGenerations = 100,
        ProposalBatchSize = 1,
        MaxDegreeOfParallelism = 1,
        IslandCount = 1,
        MigrationInterval = 0,
        Dispatch = dispatch,
        MaxInFlight = 3
    };

    internal static Task<EvolutionRunResult<TestGenome>> Run(IVariationOperator<TestGenome> variation,
        EvolutionEngineOptions options, IEvolutionCheckpointStore? store = null, IEvolutionTask<TestGenome>? task = null) =>
        new EvolutionEngine<TestGenome>(task ?? new SyntheticEvolutionTask(delayScale: 1), variation,
            _ => new MapElitesArchive<TestGenome>(new[]
            {
                new EvolutionDescriptorDefinition("x", 0, 1000, 100, EvolutionOutOfRangePolicy.Clamp)
            }), options, checkpointStore: store, genomeCodec: store is null ? null : new TestGenomeCodec())
            .RunAsync(new[] { new TestGenome(1) });

    private sealed class RecordingVariation(bool repeatParent = false, bool failProposal = false, bool failFeedback = false)
        : IOutcomeAwareVariationOperator<TestGenome>
    {
        public string Id => "recording";
        public string VersionHash => "recording-v1";
        public List<EvolutionEvaluation> Outcomes { get; } = new();
        public int Proposals { get; private set; }
        public int LearnedOutcomes { get; private set; }
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default)
        {
            Proposals++;
            if (failProposal) throw new InvalidOperationException("proposal failed");
            return new ValueTask<TestGenome>(new TestGenome(repeatParent ? context.Parent.Candidate.CanonicalGenome.Genome.Value
                : checked((int)context.Generation + LearnedOutcomes + 2)));
        }
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
        {
            if (failFeedback) throw new InvalidOperationException("feedback failed");
            Outcomes.Add(evaluation);
            LearnedOutcomes++;
        }
        public string CaptureState() => LearnedOutcomes.ToString(CultureInfo.InvariantCulture);
        public void RestoreState(string state) => LearnedOutcomes = int.Parse(state, CultureInfo.InvariantCulture);
    }
}
