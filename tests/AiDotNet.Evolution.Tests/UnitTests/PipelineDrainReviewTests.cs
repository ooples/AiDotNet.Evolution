using System.Reflection;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class PipelineDrainReviewTests
{
    public enum Completion { Completed, Canceled, Faulted }

    [Theory]
    [InlineData(Completion.Completed)]
    [InlineData(Completion.Canceled)]
    [InlineData(Completion.Faulted)]
    public async Task DrainReturnsAnObservedOutcomeInsteadOfSilentlyDiscardingIt(Completion completion)
    {
        Task callback = completion switch
        {
            Completion.Completed => Task.CompletedTask,
            Completion.Canceled => Task.FromCanceled(new CancellationToken(true)),
            Completion.Faulted => Task.FromException(new InvalidOperationException("callback failure")),
            _ => throw new ArgumentOutOfRangeException(nameof(completion))
        };
        Task drain = InvokeDrain(new[] { callback });
        await drain;
        // Reflection keeps this failure-first fixture compilable against the original private
        // Task-returning helper; no substitute callback-drain implementation is compiled here.
        object? outcome = drain.GetType().GetProperty("Result")?.GetValue(drain);
        Assert.NotNull(outcome);
        Assert.Equal(completion.ToString(), outcome.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryFaultIsCheckedForFatalRuntimeFailures(bool fatalFirst)
    {
        // An exception instance, not a real allocation failure or process-level fault.
        var fatal = new OutOfMemoryException("synthetic fatal callback");
        Task fatalTask = Task.FromException(fatal);
        Task ordinaryTask = Task.FromException(new InvalidOperationException("ordinary callback"));
        Task drain = InvokeDrain(fatalFirst ? new[] { fatalTask, ordinaryTask } : new[] { ordinaryTask, fatalTask });
        Exception actual = await Assert.ThrowsAnyAsync<Exception>(() => drain);
        Assert.False(EvolutionExceptionPolicy.IsRecoverable(actual));
        if (actual is AggregateException aggregate) Assert.Contains(fatal, aggregate.Flatten().InnerExceptions);
        else Assert.Same(fatal, actual);
    }

    [Fact]
    public async Task DrainWaitsForEveryOwnedTaskAndPreservesTheOriginalFailure()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new InvalidOperationException("original wave failure");
        Task drain = InvokeDrain(new[] { Task.FromException(new ArgumentException("secondary failure")), pending.Task });
        Assert.False(drain.IsCompleted);
        pending.SetResult(true);
        Assert.Same(drain, await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5))));
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            try { throw primary; }
            catch (InvalidOperationException) { await drain; throw; }
        });
        Assert.Same(primary, actual);
        Assert.True(pending.Task.IsCompleted);
    }

    private static Task InvokeDrain(IEnumerable<Task> tasks)
    {
        MethodInfo method = typeof(EvolutionEngine<int>).GetMethod("DrainPipelineTasksAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing actual pipeline drain helper.");
        return Assert.IsAssignableFrom<Task>(method.Invoke(null, new object[] { tasks }));
    }

    [Fact]
    public async Task FaultsTakePrecedenceOverCancellationAndAllOrdinaryFaultsAreObserved()
    {
        Task drain = InvokeDrain(new[]
        {
            Task.FromCanceled(new CancellationToken(true)),
            Task.FromException(new InvalidOperationException("first failure")),
            Task.FromException(new ArgumentException("second failure"))
        });
        await drain;
        object? outcome = drain.GetType().GetProperty("Result")?.GetValue(drain);
        Assert.NotNull(outcome);
        Assert.Equal(Completion.Faulted.ToString(), outcome.ToString());
    }

    [Fact]
    public void DiagnosticCountersAreBoundedSnapshotsAndDoNotConsumeScheduleCapacity()
    {
        var statistics = new EvolutionPipelineStatistics(EvolutionExecutionMode.Deterministic, 1, 1, 0,
            "review", "config", 0, new EvolutionPipelineOptions());
        EvolutionPipelineReport original = statistics.Snapshot();
        statistics.RecordDrain(EvolutionPipelineDrainStatus.Completed);
        statistics.RecordDrain(EvolutionPipelineDrainStatus.Faulted);
        statistics.RecordDrain(EvolutionPipelineDrainStatus.Canceled);
        statistics.RecordDrain(EvolutionPipelineDrainStatus.Faulted);
        EvolutionPipelineReport current = statistics.Snapshot();
        Assert.Equal(0, original.FaultedTaskDrains);
        Assert.Equal(0, original.CanceledTaskDrains);
        Assert.Equal(2, current.FaultedTaskDrains);
        Assert.Equal(1, current.CanceledTaskDrains);
        Assert.Equal(0, current.DroppedScheduleRecords);
        Assert.Empty(current.Schedule);
        Assert.Equal(original.CompatibilityHash, current.CompatibilityHash);
        Assert.Throws<ArgumentOutOfRangeException>(() => statistics.RecordDrain((EvolutionPipelineDrainStatus)int.MaxValue));
    }
}
