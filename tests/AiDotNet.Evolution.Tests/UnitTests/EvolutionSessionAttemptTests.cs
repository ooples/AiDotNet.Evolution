using System.Globalization;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionSessionAttemptTests
{
    [Fact]
    public async Task ExpiredQueuedWorkCannotPretendThatTheSessionHasFinished()
    {
        var options = Options(); options.EvaluationTimeout = TimeSpan.FromMilliseconds(250);
        options.MaxEvaluationAttempts = 2; options.MaxRetries = 1; options.RetryBaseDelay = TimeSpan.FromSeconds(5);
        using var session = Session(options);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // Observe the real queued attempt without consuming it or mutating session state. Its engine timeout
        // leaves an obsolete queue permit while the next attempt is still in the five-second backoff.
        var queue = (IEnumerable)typeof(EvolutionSession<TestGenome>).GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        object? pending;
        while ((pending = queue.Cast<object>().FirstOrDefault()) is null) await Task.Delay(1, guard.Token);
        Task settled = ((TaskCompletionSource<EvolutionTaskResult>)pending.GetType().GetProperty("Completion")!.GetValue(pending)!).Task;
        Assert.Same(settled, await Task.WhenAny(settled, Task.Delay(TimeSpan.FromSeconds(10), guard.Token)));
        Assert.False(session.IsComplete);
        Task<IReadOnlyList<EvolutionAskItem<TestGenome>>> waiting = session.AskAsync(1, guard.Token);
        Assert.False(waiting.IsCompleted, "An expired queued attempt is not an end-of-run signal.");
        guard.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task FingerprintedSessionsFenceCrossSessionResultsAndRoundTrippedTickets()
    {
        var identity = new EvolutionExternalTaskIdentity("task", "canonical-and-task-v1", "evaluator-v1");
        using var first = Session(Options(), identity);
        using var second = Session(Options(), identity);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var a = Assert.Single(await first.AskAsync(1, guard.Token));
        var b = Assert.Single(await second.AskAsync(1, guard.Token));
        Assert.Equal(a.WorkIdentity!.RunId, b.WorkIdentity!.RunId);
        Assert.Equal(a.EvaluationId, b.EvaluationId); Assert.Equal(a.Context.AttemptCount, b.Context.AttemptCount);
        Assert.NotEqual(a.WorkIdentity.LeaseId, b.WorkIdentity.LeaseId);
        var result = EvolutionTaskResult.Completed(1, new Dictionary<string, double> { ["x"] = 1 });
        Assert.False(first.Tell(a.EvaluationId, result)); Assert.False(second.TellAttempt(a.WorkIdentity, result));
        foreach (var wrong in new[]
        {
            new EvolutionWorkIdentity("wrong-run", a.EvaluationId, 1, a.WorkIdentity.LeaseId),
            new EvolutionWorkIdentity(a.WorkIdentity.RunId, a.EvaluationId + 1, 1, a.WorkIdentity.LeaseId),
            new EvolutionWorkIdentity(a.WorkIdentity.RunId, a.EvaluationId, 2, a.WorkIdentity.LeaseId)
        }) Assert.False(first.TellAttempt(wrong, result));
        var restored = JsonSerializer.Deserialize<EvolutionWorkIdentity>(JsonSerializer.Serialize(a.WorkIdentity))!;
        Assert.True(first.TellAttempt(restored, result)); Assert.False(first.TellAttempt(restored, result));
        Assert.True(second.TellAttempt(b.WorkIdentity, result));
        Assert.Equal((await first.Completion).StateHash, (await second.Completion).StateHash);
    }

    [Fact]
    public async Task RacingDuplicateResultsCommitExactlyOneOutcome()
    {
        using var session = Session(Options());
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var work = Assert.Single(await session.AskAsync(1, guard.Token));
        bool[] accepted = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() => session.TellAttempt(work.WorkIdentity!,
            EvolutionTaskResult.Completed(index, new Dictionary<string, double> { ["x"] = 1 })))));
        Assert.Single(accepted, value => value);
        Assert.Equal(Array.IndexOf(accepted, true), (await session.Completion).Best!.Evaluation.Quality);
    }

    [Fact]
    public void CallerTaskAndEvaluatorVersionsGuardCompatibility()
    {
        var hashes = new List<string>();
        foreach (var identity in new[]
        {
            new EvolutionExternalTaskIdentity("task", "task-v1", "eval-v1"),
            new EvolutionExternalTaskIdentity("other-task", "task-v1", "eval-v1"),
            new EvolutionExternalTaskIdentity("task", "task-v2", "eval-v1"),
            new EvolutionExternalTaskIdentity("task", "task-v1", "eval-v2")
        })
        {
            var options = Options(); options.MaxEvaluationAttempts = 0;
            using var session = Session(options, identity); hashes.Add(session.CompatibilityHash);
        }
        Assert.Equal(4, hashes.Distinct().Count());
    }

    [Fact]
    public void TransportIdentitiesRejectUnboundedOrInvalidInput()
    {
        foreach (Action invalid in new Action[]
        {
            () => new EvolutionWorkIdentity("run", -1, 1, new string('a', 32)),
            () => new EvolutionWorkIdentity("run", 0, 0, new string('a', 32)),
            () => new EvolutionWorkIdentity("run", 0, 1, "missing-token"),
            () => new EvolutionWorkIdentity(new string('a', 1025), 0, 1, new string('a', 32)),
            () => new EvolutionExternalTaskIdentity("", "v1", "v1"),
            () => new EvolutionExternalTaskIdentity("task", new string('a', 1025), "v1")
        }) Assert.ThrowsAny<ArgumentException>(invalid);
    }

    [Fact]
    public void InjectedCodecVersionParticipatesInSessionCompatibility()
    {
        var options = Options(); options.MaxEvaluationAttempts = 0;
        var taskIdentity = new EvolutionExternalTaskIdentity("task", "canonical-v1", "eval-v1");
        using var first = Session(options, taskIdentity, new VersionedCodec("codec-v1"));
        using var second = Session(options, taskIdentity, new VersionedCodec("codec-v2"));
        Assert.NotEqual(first.CompatibilityHash, second.CompatibilityHash);
    }

    [Theory]
    [InlineData(EvolutionEvaluationStatus.Failed)]
    [InlineData(EvolutionEvaluationStatus.TimedOut)]
    public async Task LegacyEvaluationIdCannotCompleteAReplacementAttempt(EvolutionEvaluationStatus firstOutcome)
    {
        var options = new EvolutionEngineOptions
        {
            RunId = "session-attempt-red",
            MaxProposals = 1,
            MaxEvaluationAttempts = 2,
            MaxRetries = 1,
            MaxDegreeOfParallelism = 1,
            EvaluationTimeout = TimeSpan.FromSeconds(20),
            CheckpointInterval = 0
        };
        using var session = new EvolutionSession<TestGenome>(task => new EvolutionEngine<TestGenome>(task, new UnusedVariation(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 10, 10) }), options),
            new[] { new TestGenome(1) }, genome => genome.Value.ToString(CultureInfo.InvariantCulture));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = Assert.Single(await session.AskAsync(1, guard.Token));
        // Trigger the same real engine retry transition through its result contract. A 250 ms
        // wall-clock timeout could expire BOTH attempts before the test thread was scheduled
        // under coverage/load, testing scheduler speed instead of stale-result fencing.
        // ExpiredQueuedWorkCannotPretendThatTheSessionHasFinished separately covers real timeout.
        Assert.True(session.TellAttempt(first.WorkIdentity!, new EvolutionTaskResult(firstOutcome)));
        var replacement = Assert.Single(await session.AskAsync(1, guard.Token));
        Assert.Equal(first.EvaluationId, replacement.EvaluationId);
        Assert.Equal(1, first.Context.AttemptCount); Assert.Equal(2, replacement.Context.AttemptCount);
        Assert.False(session.Tell(first.EvaluationId, EvolutionTaskResult.Completed(9, new Dictionary<string, double> { ["x"] = 1 })));
        Assert.False(session.TellAttempt(first.WorkIdentity!, EvolutionTaskResult.Completed(9, new Dictionary<string, double> { ["x"] = 1 })));
        Assert.True(session.TellAttempt(replacement.WorkIdentity!, EvolutionTaskResult.Completed(1, new Dictionary<string, double> { ["x"] = 1 })));
        Assert.False(session.TellAttempt(replacement.WorkIdentity!, EvolutionTaskResult.Completed(9, new Dictionary<string, double> { ["x"] = 1 })));
        var run = await session.Completion;
        Assert.Equal(1d, run.Best!.Evaluation.Quality);
    }

    private static EvolutionEngineOptions Options() => new()
    {
        RunId = "session-work",
        MaxProposals = 1,
        MaxEvaluationAttempts = 1,
        MaxDegreeOfParallelism = 1,
        CheckpointInterval = 0,
        EvaluationTimeout = TimeSpan.FromSeconds(20)
    };

    private static EvolutionSession<TestGenome> Session(EvolutionEngineOptions options, EvolutionExternalTaskIdentity? identity = null,
        IEvolutionGenomeCodec<TestGenome>? codec = null)
    {
        Func<IEvolutionTask<TestGenome>, EvolutionEngine<TestGenome>> factory = task => new(task, new UnusedVariation(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 10, 10) }), options, genomeCodec: codec);
        return identity is null
            ? new EvolutionSession<TestGenome>(factory, new[] { new TestGenome(1) }, genome => genome.Value.ToString(CultureInfo.InvariantCulture))
            : new EvolutionSession<TestGenome>(factory, new[] { new TestGenome(1) }, genome => genome.Value.ToString(CultureInfo.InvariantCulture), identity);
    }

    private sealed class VersionedCodec(string version) : IEvolutionGenomeCodec<TestGenome>
    {
        public string Id => "session-test-codec";
        public string VersionHash => version;
        public string Serialize(TestGenome genome) => genome.Value.ToString(CultureInfo.InvariantCulture);
        public TestGenome Deserialize(string payload) => new(int.Parse(payload, CultureInfo.InvariantCulture));
    }

    private sealed class UnusedVariation : IVariationOperator<TestGenome>
    {
        public string Id => "unused";
        public string VersionHash => "unused-v1";
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Only the seed is admitted.");
    }
}
