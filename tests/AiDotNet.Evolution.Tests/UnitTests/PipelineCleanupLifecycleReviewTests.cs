using System.Globalization;
using System.Reflection;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>Exercises exception cleanup through the real pipeline and resource adapters.</summary>
public sealed class PipelineCleanupLifecycleReviewTests
{
    [Fact]
    public async Task ThrowingCancellationCallbackCannotReplaceFatalProposalOrSkipDrainAndRollback()
    {
        var variation = new ThrowingCancellationVariation();
        var ledger = new EvolutionResourceLedger("callback-cleanup", EvolutionResources.Of("cost_units", 20));
        var metered = new ResourceMeteredEvolutionTask<TestGenome>(new SuccessfulTask(), ledger, new[] { 2m });
        var engine = Engine(metered, variation, Options(3));
        Task run = engine.RunAsync(new[] { new TestGenome(1) });
        int activeWhenRunFinished = -1;
        Task observeCompletion = run.ContinueWith(_ => activeWhenRunFinished = variation.Active,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Exception? failure = null;

        try
        {
            await CompleteWithinGuard(variation.CallbackRegistered.Task);
            Assert.Equal(2, variation.Active);
            variation.ReleaseFailure.TrySetResult(true);
            await CompleteWithinGuard(variation.CallbackInvoked.Task);
            Assert.False(variation.SiblingExited.Task.IsCompleted);
        }
        finally
        {
            // Always unblock both owned callbacks, including on a failing assertion or timeout.
            variation.ReleaseFailure.TrySetResult(true);
            variation.ReleaseSibling.TrySetResult(true);
            failure = await Record.ExceptionAsync(() => CompleteWithinGuard(run));
            await CompleteWithinGuard(observeCompletion);
            if (variation.CallbackRegistered.Task.IsCompleted)
                await CompleteWithinGuard(variation.SiblingExited.Task);
        }

        Assert.Same(variation.PrimaryFailure, failure);
        Assert.Equal(1, variation.CancellationCallbackCalls);
        Assert.Equal(0, activeWhenRunFinished);
        Assert.Equal(0, variation.Active);
        Assert.Equal(1, ReadCounter(engine, "_nextEvaluationId"));
        Assert.Equal(1, ReadCounter(engine, "_proposals"));
        Assert.Equal(1, ReadCounter(engine, "_evaluationAttempts"));
        Assert.Equal(0, ReadCounter(engine, "_generation"));
        var report = Assert.IsType<EvolutionPipelineReport>(engine.PipelineReport);
        Assert.Equal(1, report.AbortedWaves);
        Assert.Equal(1, report.FaultedWaveCancellations);

        var resources = ledger.Snapshot();
        Assert.Equal(3, resources.Admitted);
        Assert.Equal(resources.Admitted, resources.Settled);
        Assert.Equal(0m, resources.Reserved["cost_units"]);
        Assert.Equal(1m, resources.Spent["cost_units"]);
        Assert.Equal(0, resources.Unknown);
        metered.BeginPipelinePhase();
        metered.EndPipelinePhase();
    }

    [Fact]
    public async Task FatalRetryDrainsSiblingAndClosesTheActualResourcePhaseBeforeRollback()
    {
        var task = new FatalRetryTask();
        var ledger = new EvolutionResourceLedger("fatal-retry-cleanup", EvolutionResources.Of("cost_units", 20));
        var metered = new ResourceMeteredEvolutionTask<TestGenome>(task, ledger, new[] { 2m });
        var options = Options(2);
        options.MaxRetries = 1;
        var engine = Engine(metered, new IncrementVariation(), options);
        Task run = engine.RunAsync(new[] { new TestGenome(1), new TestGenome(2) });
        Exception? failure = null;

        try
        {
            try
            {
                await CompleteWithinGuard(task.SiblingStarted.Task);
                task.ReleaseFailure.TrySetResult(true);
                await CompleteWithinGuard(task.FailureThrown.Task);
                Assert.False(task.SiblingExited.Task.IsCompleted);
                Assert.False(run.IsCompleted);
            }
            finally
            {
                task.ReleaseFailure.TrySetResult(true);
                task.ReleaseSibling.TrySetResult(true);
                failure = await Record.ExceptionAsync(() => CompleteWithinGuard(run));
                if (task.SiblingStarted.Task.IsCompleted)
                    await CompleteWithinGuard(task.SiblingExited.Task);
            }

            Assert.Same(task.PrimaryFailure, failure);
            Assert.Equal(0, task.Active);
            Assert.Equal(0, ReadCounter(engine, "_nextEvaluationId"));
            Assert.Equal(0, ReadCounter(engine, "_proposals"));
            Assert.Equal(0, ReadCounter(engine, "_evaluationAttempts"));
            var report = Assert.IsType<EvolutionPipelineReport>(engine.PipelineReport);
            Assert.Equal(1, report.AbortedWaves);
            Assert.Equal(0, report.FaultedWaveCancellations);

            // Dispatched fatal work keeps its maximum charge, not an invented refund.
            var resources = ledger.Snapshot();
            Assert.Equal(4, resources.Admitted);
            Assert.Equal(resources.Admitted, resources.Settled);
            Assert.Equal(0m, resources.Reserved["cost_units"]);
            Assert.Equal(4m, resources.Spent["cost_units"]);
            Assert.Equal(1, resources.Unknown);
            Assert.Equal(2m, Assert.Single(resources.Receipts,
                receipt => receipt.OperationId == "evaluation/0/attempt/2/stage/-1").Charged["cost_units"]);

            // The real adapter rejects this call if the retry phase was left active.
            metered.BeginPipelinePhase();
            metered.EndPipelinePhase();
        }
        finally
        {
            task.ReleaseFailure.TrySetResult(true);
            task.ReleaseSibling.TrySetResult(true);
            // Also reconcile an old implementation's leaked phase after the negative control.
            metered.EndPipelinePhase();
        }
    }

    private static EvolutionEngineOptions Options(int proposals) => new()
    {
        RunId = "cleanup-lifecycle",
        Seed = 37,
        Dispatch = EvolutionDispatchMode.Pipeline,
        MaxProposals = proposals,
        MaxEvaluationAttempts = 10,
        MaxGenerations = 10,
        MaxDegreeOfParallelism = 2,
        CheckpointInterval = 0,
        MigrationInterval = 0,
        Pipeline = new EvolutionPipelineOptions
        {
            WaveSize = 2,
            MaxProposalConcurrency = 2,
            ProposalQueueCapacity = 1,
            EvaluationQueueCapacity = 1
        }
    };

    private static EvolutionEngine<TestGenome> Engine(IEvolutionTask<TestGenome> task,
        IVariationOperator<TestGenome> variation, EvolutionEngineOptions options) => new(task, variation,
        _ => new MapElitesArchive<TestGenome>(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 10, 10, EvolutionOutOfRangePolicy.Clamp)
        }), options, genomeCodec: new TestGenomeCodec());

    private static long ReadCounter(EvolutionEngine<TestGenome> engine, string fieldName)
    {
        FieldInfo field = typeof(EvolutionEngine<TestGenome>).GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing actual transaction counter: " + fieldName);
        return Assert.IsType<long>(field.GetValue(engine));
    }

    private static async Task CompleteWithinGuard(Task task)
    {
        // A timeout can only fail this test; completion ordering is established by the gates.
        using var guard = new CancellationTokenSource();
        Task timeout = Task.Delay(TimeSpan.FromSeconds(15), guard.Token);
        Task completed = await Task.WhenAny(task, timeout);
        guard.Cancel();
        Assert.Same(task, completed);
        await task;
    }

    private sealed class ThrowingCancellationVariation : IDeterministicConcurrentVariationOperator<TestGenome>
    {
        public string Id => "throwing-cancellation-proposal";
        public string VersionHash => "v1";
        public bool SupportsDeterministicConcurrency => true;
        public OutOfMemoryException PrimaryFailure { get; } = new("synthetic fatal proposal failure");
        public TaskCompletionSource<bool> CallbackRegistered { get; } = NewGate();
        public TaskCompletionSource<bool> CallbackInvoked { get; } = NewGate();
        public TaskCompletionSource<bool> ReleaseFailure { get; } = NewGate();
        public TaskCompletionSource<bool> ReleaseSibling { get; } = NewGate();
        public TaskCompletionSource<bool> SiblingExited { get; } = NewGate();
        private int _active;
        private int _cancellationCallbackCalls;
        public int Active => Volatile.Read(ref _active);
        public int CancellationCallbackCalls => Volatile.Read(ref _cancellationCallbackCalls);

        public async ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _active);
            try
            {
                if (context.Generation == 1)
                {
                    await CallbackRegistered.Task;
                    await ReleaseFailure.Task;
                    throw PrimaryFailure;
                }

                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                {
                    Interlocked.Increment(ref _cancellationCallbackCalls);
                    CallbackInvoked.TrySetResult(true);
                    throw new InvalidOperationException("synthetic cancellation callback failure");
                });
                CallbackRegistered.TrySetResult(true);
                await ReleaseSibling.Task;
                return new TestGenome(3);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                if (context.Generation != 1) SiblingExited.TrySetResult(true);
            }
        }
    }

    private class SuccessfulTask : IEvolutionTask<TestGenome>
    {
        public string Id => "cleanup-lifecycle-task";
        public string VersionHash => "v1";
        public string EvaluatorVersionHash => "v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome,
            CancellationToken cancellationToken = default) => new(new EvolutionCanonicalGenome<TestGenome>(
                genome, genome.Value.ToString(CultureInfo.InvariantCulture)));
        public virtual ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default) => new(
                EvolutionTaskResult.Completed(candidate.CanonicalGenome.Genome.Value,
                    new Dictionary<string, double> { ["x"] = candidate.CanonicalGenome.Genome.Value }, costUnits: 1));
    }

    private sealed class FatalRetryTask : SuccessfulTask
    {
        public OutOfMemoryException PrimaryFailure { get; } = new("synthetic fatal retry failure");
        public TaskCompletionSource<bool> SiblingStarted { get; } = NewGate();
        public TaskCompletionSource<bool> ReleaseFailure { get; } = NewGate();
        public TaskCompletionSource<bool> FailureThrown { get; } = NewGate();
        public TaskCompletionSource<bool> ReleaseSibling { get; } = NewGate();
        public TaskCompletionSource<bool> SiblingExited { get; } = NewGate();
        private int _active;
        public int Active => Volatile.Read(ref _active);

        public override async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            if (context.AttemptCount == 1)
                return new EvolutionTaskResult(EvolutionEvaluationStatus.Failed, costUnits: 0.5);
            Interlocked.Increment(ref _active);
            try
            {
                if (candidate.CanonicalGenome.Genome.Value == 1)
                {
                    await SiblingStarted.Task;
                    await ReleaseFailure.Task;
                    FailureThrown.TrySetResult(true);
                    throw PrimaryFailure;
                }
                SiblingStarted.TrySetResult(true);
                await ReleaseSibling.Task;
                return await base.EvaluateAsync(candidate, context, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                if (candidate.CanonicalGenome.Genome.Value != 1) SiblingExited.TrySetResult(true);
            }
        }
    }

    private static TaskCompletionSource<bool> NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
