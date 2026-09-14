using System.Reflection;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// Ask/tell, the inverted way to drive <see cref="EvolutionEngine{TGenome}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The engine normally owns its loop and calls the task's evaluator. A session turns that inside out so the caller
/// asks for work and tells the results. Two properties make it worth having and both are load-bearing here: the
/// engine is not forked, so archives and determinism are unchanged, and nothing calls back across the boundary, so
/// a native or cross-language host needs no function pointers.
/// </para>
/// <para>
/// THE FAILURE MODE THESE TESTS EXIST FOR IS A HANG. Every path that could leave the engine awaiting a completion
/// nobody will supply -- disposal with work outstanding, a run that finished while a caller waited, a duplicate
/// tell -- is exercised with a timeout, because a deadlock in this design does not throw, it simply never returns.
/// </para>
/// </remarks>
public sealed class EvolutionSessionTests
{
    /// <summary>A hang here must fail the test rather than wedge the suite.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AskYieldsTheCandidatesTheEngineProposed()
    {
        using var session = NewSession(maxProposals: 4);

        IReadOnlyList<EvolutionAskItem<SessionGenome>> batch = await WithTimeout(session.AskAsync(8));

        Assert.NotEmpty(batch);
        // Each item carries what an evaluator needs: the genome and the id to report against.
        Assert.All(batch, item => Assert.True(item.EvaluationId >= 0));
        Assert.All(batch, item => Assert.NotNull(item.Candidate.CanonicalGenome.Genome));
        Assert.All(batch, item => Assert.NotNull(item.Context));
    }

    [Fact]
    public async Task AFullAskTellLoopDrivesTheRunToCompletion()
    {
        // The whole point: the caller owns the loop and evolution still finishes with a populated archive.
        using var session = NewSession(maxProposals: 24);

        int told = 0;
        while (!session.IsComplete)
        {
            IReadOnlyList<EvolutionAskItem<SessionGenome>> batch = await WithTimeout(session.AskAsync(4));
            if (batch.Count == 0) break;

            foreach (EvolutionAskItem<SessionGenome> item in batch)
            {
                Assert.True(session.Tell(item.EvaluationId, Score(item)));
                told += 1;
            }
        }

        EvolutionRunResult<SessionGenome> result = await WithTimeout(session.Completion);

        Assert.True(told > 0);
        Assert.NotNull(result);
        // The archive is the engine's, filled through the ordinary path.
        Assert.Contains(result.Islands, island => island.Best is not null);
    }

    [Fact]
    public async Task AskReturnsAnEmptyBatchOnceTheRunIsOver()
    {
        // The completion signal for a host that cannot await a Task -- which is every host reached over a C ABI.
        using var session = NewSession(maxProposals: 2);

        while (true)
        {
            IReadOnlyList<EvolutionAskItem<SessionGenome>> batch = await WithTimeout(session.AskAsync(8));
            if (batch.Count == 0) break;
            foreach (EvolutionAskItem<SessionGenome> item in batch)
                session.Tell(item.EvaluationId, Score(item));
        }

        await WithTimeout(session.Completion);
        Assert.True(session.IsComplete);
        Assert.Empty(await WithTimeout(session.AskAsync(8)));
    }

    [Fact]
    public async Task TellRejectsAnUnknownIdAndASecondTellOfTheSameId()
    {
        // A duplicate tell is a routine consequence of a host retry, not a programming error, so it reports
        // false rather than throwing across an ABI that cannot carry an exception.
        using var session = NewSession(maxProposals: 4);
        IReadOnlyList<EvolutionAskItem<SessionGenome>> batch = await WithTimeout(session.AskAsync(1));
        EvolutionAskItem<SessionGenome> item = Assert.Single(batch);

        Assert.False(session.Tell(long.MaxValue, Score(item)));
        Assert.True(session.Tell(item.EvaluationId, Score(item)));
        Assert.False(session.Tell(item.EvaluationId, Score(item)));
    }

    [Fact]
    public async Task DisposeReleasesWorkTheCallerNeverTold()
    {
        // THE DEADLOCK THIS DESIGN COULD HAVE HAD. An outstanding candidate is an engine awaiting a completion;
        // disposing without failing it would leave the run, and any awaiter of it, hanging forever.
        var session = NewSession(maxProposals: 8);
        IReadOnlyList<EvolutionAskItem<SessionGenome>> batch = await WithTimeout(session.AskAsync(4));
        Assert.NotEmpty(batch);

        session.Dispose();

        // Completes one way or another -- result or cancellation -- but it must not hang.
        await WithTimeout(Settled(session.Completion));
    }

    [Fact]
    public async Task DisposeIsIdempotent()
    {
        var session = NewSession(maxProposals: 4);
        await WithTimeout(session.AskAsync(1));
        session.Dispose();
        session.Dispose();
        Assert.True(true);
    }

    [Fact]
    public async Task AskNeverReturnsMoreThanAskedFor()
    {
        using var session = NewSession(maxProposals: 32, proposalBatchSize: 8);
        IReadOnlyList<EvolutionAskItem<SessionGenome>> batch = await WithTimeout(session.AskAsync(2));
        Assert.InRange(batch.Count, 1, 2);
    }

    [Fact]
    public async Task AskRejectsANonPositiveBatchSize()
    {
        using var session = NewSession(maxProposals: 4);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.AskAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.AskAsync(-1));
    }

    [Fact]
    public async Task AskObservesTheCallersCancellation()
    {
        // A caller waiting for work it will never get must be able to walk away, and must see its OWN
        // cancellation rather than an empty batch that would read as "the run finished".
        using var session = NewSession(maxProposals: 4);
        // Drain the first batch so the next ask has nothing immediately available.
        IReadOnlyList<EvolutionAskItem<SessionGenome>> first = await WithTimeout(session.AskAsync(64));
        Assert.NotEmpty(first);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.AskAsync(1, cancellation.Token));
    }

    [Fact]
    public async Task RequestStopKeepsTheResultsFoundSoFar()
    {
        // GRACEFUL, NOT CANCELLATION. The engine distinguishes the two deliberately:
        // a requested stop commits the current batch and returns a normal result whose
        // archives hold everything found, where cancelling throws and yields nothing.
        // The first version of RequestStop cancelled, so a caller who asked to stop and
        // read the best genome got an OperationCanceledException at the moment they
        // asked to see it.
        using var session = NewSession(maxProposals: 64);

        int told = 0;
        while (told < 4)
        {
            IReadOnlyList<EvolutionAskItem<SessionGenome>> batch =
                await WithTimeout(session.AskAsync(4));
            if (batch.Count == 0) break;
            foreach (EvolutionAskItem<SessionGenome> item in batch)
            {
                session.Tell(item.EvaluationId, Score(item));
                told += 1;
            }
        }

        session.RequestStop();

        EvolutionRunResult<SessionGenome> result = await WithTimeout(session.Completion);
        Assert.Equal(EvolutionStopReason.Canceled, result.StopReason);
        Assert.Contains(result.Islands, island => island.Best is not null);
    }

    [Fact]
    public async Task RequestStopDoesNotHangOnCandidatesNobodyAskedFor()
    {
        // THE DEADLOCK THIS FOUND. A candidate the engine proposed sits in the queue
        // until a caller asks for it, and only then becomes 'outstanding'. Failing just
        // the asked ones left the engine awaiting completions for everything it had
        // proposed and nobody collected, so the batch never committed and the stop flag
        // was never observed. Stopping without asking for anything at all is the
        // sharpest form of that.
        using var session = NewSession(maxProposals: 64);

        session.RequestStop();

        await WithTimeout(Settled(session.Completion));
        Assert.True(session.IsComplete);
    }

    [Fact]
    public async Task AbortEndsTheRunImmediatelyAndDiscardsIt()
    {
        // The counterpart: when the work must end now and the results do not matter,
        // Completion is canceled rather than returning a run result.
        using var session = NewSession(maxProposals: 64);
        await WithTimeout(session.AskAsync(2));

        session.Abort();

        await WithTimeout(Settled(session.Completion));
        // CANCELED, NOT MERELY SETTLED. Accepting RanToCompletion here would pass against
        // an Abort that returned a run result -- which is exactly the behaviour that
        // distinguishes it from RequestStop, and therefore the only thing worth asserting.
        // The unresolved evaluations hold the engine inside EvaluateBatchAsync, where
        // cancellation is observed before any normal result can be produced.
        Assert.Equal(TaskStatus.Canceled, session.Completion.Status);
    }

    [Fact]
    public void ConstructorRejectsNulls()
    {
        AssertNullConstructorArgument(null, Seeds(2), Identity, "engineFactory");
        AssertNullConstructorArgument(task => Engine(task, Options(4)), null, Identity, "initialGenomes");
        // Identity has no default on purpose: falling back to ToString would give
        // every genome of a type that does not override it the same id, and the
        // engine would silently deduplicate distinct candidates into one.
        AssertNullConstructorArgument(task => Engine(task, Options(4)), Seeds(2), null, "canonicalIdentity");
    }

    private static void AssertNullConstructorArgument(
        Func<IEvolutionTask<SessionGenome>, EvolutionEngine<SessionGenome>>? engineFactory,
        IEnumerable<SessionGenome>? initialGenomes,
        Func<SessionGenome, string>? canonicalIdentity,
        string parameterName)
    {
        // Exercise the runtime boundary without lying to nullable analysis or relaxing
        // the public constructor's non-null contract. Reflection wraps the actual guard.
        ConstructorInfo constructor = Assert.Single(typeof(EvolutionSession<SessionGenome>).GetConstructors());
        TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(() =>
            constructor.Invoke(new object?[] { engineFactory, initialGenomes, canonicalIdentity }));
        ArgumentNullException argument = Assert.IsType<ArgumentNullException>(invocation.InnerException);
        Assert.Equal(parameterName, argument.ParamName);
    }

    [Fact]
    public void ConstructionRefusesAnEndlessSeedSequence()
    {
        // `ToArray` on an infinite sequence never returns, so construction would
        // hang with nothing naming seeds as the cause. The limit turns it into an
        // argument error instead.
        static IEnumerable<SessionGenome> Endless()
        {
            for (int i = 0; ; i += 1) yield return new SessionGenome(i);
        }

        ArgumentException failure = Assert.Throws<ArgumentException>(() =>
            new EvolutionSession<SessionGenome>(
                task => Engine(task, Options(8)), Endless(), Identity));
        Assert.Contains("at most", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskCapsTheBatchItWillPreallocate()
    {
        // `new List(int.MaxValue)` exhausts the process before a single candidate
        // is handed over. Asking for more than the cap is not an error; it simply
        // yields at most the cap.
        using var session = NewSession(maxProposals: 8);
        IReadOnlyList<EvolutionAskItem<SessionGenome>> batch =
            await WithTimeout(session.AskAsync(int.MaxValue));

        Assert.NotEmpty(batch);
        Assert.InRange(batch.Count, 1, EvolutionSession<SessionGenome>.MaxBatchSize);
    }

    [Fact]
    public async Task DistinctGenomesGetDistinctIdentities()
    {
        // THE BUG THE REQUIRED IDENTITY EXISTS FOR. With ToString as the default,
        // a genome type that does not override it gives every instance the same
        // id and the engine folds distinct candidates into one -- a search that
        // evaluates one thing and reports it as many.
        using var session = NewSession(maxProposals: 8);

        // Collected across asks: AskAsync returns as soon as ANY work is ready
        // rather than waiting to fill the batch, so one call may yield one item.
        var ids = new List<string>();
        while (ids.Count < 2)
        {
            IReadOnlyList<EvolutionAskItem<SessionGenome>> batch =
                await WithTimeout(session.AskAsync(4));
            if (batch.Count == 0) break;
            foreach (EvolutionAskItem<SessionGenome> item in batch)
            {
                ids.Add(item.Candidate.CanonicalGenome.Id);
                session.Tell(item.EvaluationId, Score(item));
            }
        }

        Assert.True(ids.Count >= 2, $"expected at least two candidates, got {ids.Count}");
        // Distinctness alone is satisfied by any per-genome string; the prefix is what
        // proves the id came from the identity function this session was given.
        Assert.All(ids, id => Assert.StartsWith("canonical:", id, StringComparison.Ordinal));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task AnIdentityFunctionReturningNothingStopsTheRunRatherThanDeduplicating()
    {
        // AN EMPTY ID IS THE DEFECT THE REQUIRED IDENTITY EXISTS FOR, one step on: every
        // genome would share it, the engine would fold distinct candidates into one, and
        // a search that evaluated a single thing would report it as many. Canonicalize
        // throwing surfaces as a stop reason rather than an exception, which is why this
        // asserts on the run result and not on a throw.
        using var session = new EvolutionSession<SessionGenome>(
            task => Engine(task, Options(8)),
            Seeds(2),
            _ => string.Empty);

        IReadOnlyList<EvolutionAskItem<SessionGenome>> batch = await WithTimeout(session.AskAsync(4));

        Assert.Empty(batch);
    }

    [Fact]
    public async Task AnOperatorThatThrowsIsRecordedAsAFailureRatherThanStoppingTheRun()
    {
        // A RECOVERABLE OPERATOR FAILURE IS ABSORBED BY DESIGN. PrepareVariationAsync
        // catches anything EvolutionExceptionPolicy calls recoverable and turns it into
        // a pre-evaluation failure coded "variation_failure", so one bad proposal costs
        // one candidate and not the run. That is the behaviour worth pinning, and it is
        // the opposite of what this test used to allow: it accepted Faulted OR
        // RanToCompletion, which is every terminal state a run can reach, so it could
        // not have failed whatever the engine did.
        using var session = new EvolutionSession<SessionGenome>(
            task => new EvolutionEngine<SessionGenome>(
                task, new ThrowingVariation(), _ => Archive(), Options(8)),
            Seeds(2),
            Identity);

        // Drain whatever the seeds produce so the engine reaches a variation.
        for (int i = 0; i < 4; i += 1)
        {
            IReadOnlyList<EvolutionAskItem<SessionGenome>> batch =
                await WithTimeout(session.AskAsync(4));
            if (batch.Count == 0) break;
            foreach (EvolutionAskItem<SessionGenome> item in batch)
                session.Tell(item.EvaluationId, Score(item));
        }

        EvolutionRunResult<SessionGenome> result = await WithTimeout(session.Completion);

        Assert.Contains(
            result.RetainedFailures,
            diagnostic => diagnostic.Code == "variation_failure");
    }

    [Fact]
    public async Task AnUnrecoverableOperatorFailureIsHandedToTheCaller()
    {
        // THE RUN IS ON A DETACHED TASK, so an exception escaping it would reach only
        // TaskScheduler.UnobservedTaskException -- a process-level event no caller sees
        // -- while Completion never settled. RunEngineAsync's general catch exists to
        // turn that into something the caller can await.
        //
        // REACHED WITH AN UNRECOVERABLE EXCEPTION, because a recoverable one never gets
        // there: the test above shows the engine absorbing those on purpose. This is the
        // narrow class EvolutionExceptionPolicy refuses to contain, which is precisely
        // the class that must reach the caller rather than be swallowed into a
        // diagnostic and reported as a tidy run.
        using var session = new EvolutionSession<SessionGenome>(
            task => new EvolutionEngine<SessionGenome>(
                task, new UnrecoverableVariation(), _ => Archive(), Options(8)),
            Seeds(2),
            Identity);

        for (int i = 0; i < 4; i += 1)
        {
            IReadOnlyList<EvolutionAskItem<SessionGenome>> batch =
                await WithTimeout(session.AskAsync(4));
            if (batch.Count == 0) break;
            foreach (EvolutionAskItem<SessionGenome> item in batch)
                session.Tell(item.EvaluationId, Score(item));
        }

        await WithTimeout(Settled(session.Completion));

        OutOfMemoryException thrown =
            await Assert.ThrowsAsync<OutOfMemoryException>(() => session.Completion);
        Assert.Equal("the variation operator ran out of room", thrown.Message);
    }

    [Fact]
    public async Task OneAskCanReturnSeveralCandidatesAtOnce()
    {
        // AskAsync blocks for the FIRST item and then drains whatever is already queued.
        // The drain loop is a separate path from the first take, and with a proposal
        // batch larger than one it is the path that actually fills a batch.
        using var session = NewSession(maxProposals: 16, proposalBatchSize: 4);

        var seen = new List<EvolutionAskItem<SessionGenome>>();
        for (int attempt = 0; attempt < 6 && seen.Count < 2; attempt += 1)
        {
            IReadOnlyList<EvolutionAskItem<SessionGenome>> batch =
                await WithTimeout(session.AskAsync(4));
            if (batch.Count == 0) break;
            seen.AddRange(batch);
            foreach (EvolutionAskItem<SessionGenome> item in batch)
                session.Tell(item.EvaluationId, Score(item));
        }

        Assert.True(seen.Count >= 2, $"expected several candidates, saw {seen.Count}");
    }

    // ----------------------------------------------------------------- helpers

    private static EvolutionSession<SessionGenome> NewSession(int maxProposals, int proposalBatchSize = 4) =>
        new(task => Engine(task, Options(maxProposals, proposalBatchSize)), Seeds(2), Identity);

    /// <summary>A real canonical identity, not ToString: distinct values, distinct ids.</summary>
    /// <remarks>
    /// THE PREFIX IS THE POINT. Without it this returns exactly what a genome's
    /// <c>ToString</c> would, so a regression to the old <c>genome.ToString()</c> default
    /// would still produce distinct ids and every identity test would keep passing. The
    /// prefix is a string only the supplied function can produce, which is what lets
    /// <see cref="DistinctGenomesGetDistinctIdentities"/> prove the function was actually
    /// called rather than merely that the ids came out distinct.
    /// </remarks>
    private static string Identity(SessionGenome genome) =>
        "canonical:" + genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static EvolutionEngineOptions Options(int maxProposals, int proposalBatchSize = 4) => new()
    {
        RunId = "session-test",
        Seed = 7UL,
        MaxProposals = maxProposals,
        MaxGenerations = 1_000,
        ProposalBatchSize = proposalBatchSize,
        CheckpointInterval = 0
    };

    private static EvolutionEngine<SessionGenome> Engine(IEvolutionTask<SessionGenome> task, EvolutionEngineOptions options) =>
        new(task, new AddOneVariation(), _ => Archive(), options);

    private static MapElitesArchive<SessionGenome> Archive() => new(new[]
    {
        new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp)
    });

    private static SessionGenome[] Seeds(int count) =>
        Enumerable.Range(1, count).Select(value => new SessionGenome(value)).ToArray();

    private static EvolutionTaskResult Score(EvolutionAskItem<SessionGenome> item)
    {
        double value = item.Candidate.CanonicalGenome.Genome.Value;
        return EvolutionTaskResult.Completed(
            quality: value,
            direction: EvolutionOptimizationDirection.Maximize,
            descriptors: new Dictionary<string, double> { ["x"] = value % 100 });
    }

    /// <summary>Fails the test on a hang instead of wedging the run.</summary>
    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(ReferenceEquals(completed, task), "timed out waiting for the session");
        return await task;
    }

    private static async Task WithTimeout(Task task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(ReferenceEquals(completed, task), "timed out waiting for the session");
        await task;
    }

    /// <summary>Waits for a task to finish in any terminal state, since cancellation is a valid outcome here.</summary>
    private static Task Settled(Task task) =>
        task.ContinueWith(static _ => { }, TaskScheduler.Default);

    /// <summary>Immutable by construction, which the engine requires of any reference genome.</summary>
    private sealed class SessionGenome : IImmutableEvolutionGenome<SessionGenome>
    {
        public SessionGenome(int value) => Value = value;

        /// <summary>A NEW instance: the engine rejects a snapshot that returns itself,
        /// because retaining the caller's object would let a later mutation reach the archive.</summary>
        public SessionGenome CreateOwnedSnapshot() => new(Value);

        public int Value { get; }

        // NO ToString OVERRIDE, deliberately. It used to return the value, which made this
        // fixture one of the few genome types for which the old `genome.ToString()` default
        // happened to be a correct identity -- so the suite could not see the defect that
        // default causes for every type that does not override it.
    }

    /// <summary>
    /// A variation operator whose failure the engine is not allowed to absorb.
    /// </summary>
    /// <remarks>
    /// OutOfMemoryException is one of the few types EvolutionExceptionPolicy reports as
    /// unrecoverable, so it passes straight through PrepareVariationAsync's catch and
    /// out of the run -- which is the only route to RunEngineAsync's general handler.
    /// </remarks>
    private sealed class UnrecoverableVariation : IVariationOperator<SessionGenome>
    {
        public string Id => "unrecoverable";

        public string VersionHash => "unrecoverable-v1";

        public ValueTask<SessionGenome> ProposeAsync(
            EvolutionVariationContext<SessionGenome> context,
            CancellationToken cancellationToken = default) =>
            throw new OutOfMemoryException("the variation operator ran out of room");
    }

    /// <summary>A variation operator that fails, to exercise the run's error path.</summary>
    private sealed class ThrowingVariation : IVariationOperator<SessionGenome>
    {
        public string Id => "throws";

        public string VersionHash => "throws-v1";

        public ValueTask<SessionGenome> ProposeAsync(
            EvolutionVariationContext<SessionGenome> context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the variation operator exploded");
    }

    private sealed class AddOneVariation : IVariationOperator<SessionGenome>
    {
        public string Id => "add-one";

        public string VersionHash => "add-one-v1";

        public ValueTask<SessionGenome> ProposeAsync(
            EvolutionVariationContext<SessionGenome> context,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<SessionGenome>(
                new SessionGenome(context.Parent.Candidate.CanonicalGenome.Genome.Value + 1));
        }
    }
}
