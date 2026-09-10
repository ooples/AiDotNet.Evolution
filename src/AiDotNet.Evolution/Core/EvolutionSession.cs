using System.Collections.Concurrent;

namespace AiDotNet.Evolution;

/// <summary>Drives an <see cref="EvolutionEngine{TGenome}"/> from the outside, one batch at a time.</summary>
/// <typeparam name="TGenome">The task-specific genome type.</typeparam>
/// <remarks>
/// <para>
/// <see cref="EvolutionEngine{TGenome}"/> owns its loop: you hand it an <see cref="IEvolutionTask{TGenome}"/> and
/// it calls <see cref="IEvolutionTask{TGenome}.EvaluateAsync"/> whenever it wants a candidate scored. That is the
/// right shape for a .NET caller who can simply implement the interface. It is the wrong shape for three callers
/// who cannot:
/// </para>
/// <list type="bullet">
/// <item><description>A native or cross-language host. Inverting the call means function pointers, a foreign
/// runtime's thread rules, and re-entrancy across an ABI. A host that instead ASKS for work and TELLS the result
/// needs none of that, which is what makes a NativeAOT surface over this library tractable at all.</description></item>
/// <item><description>An evaluator that is not a function. Scoring may mean dispatching to a queue, waiting for a
/// build, or asking a person. Blocking inside <c>EvaluateAsync</c> until a human answers works, but it ties up the
/// engine's concurrency slots and makes the wait invisible to the caller.</description></item>
/// <item><description>A caller who owns the scheduler. Notebooks, job runners and UIs already have a loop; they
/// want to step evolution from it rather than surrender control to <c>RunAsync</c>.</description></item>
/// </list>
/// <para>
/// THE ENGINE IS NOT FORKED OR REIMPLEMENTED, which is the point of doing it this way. This is an ordinary
/// <see cref="IEvolutionTask{TGenome}"/> whose <c>EvaluateAsync</c> parks the candidate on a queue and awaits a
/// completion that <see cref="Tell"/> supplies. The engine runs on a background task exactly as it always does, so
/// archives, checkpointing, migration, islands, retry policy and determinism all behave identically. Ask/tell is a
/// different way to reach the same engine, not a second engine.
/// </para>
/// <para>
/// <b>Determinism is preserved but not free.</b> The engine's own ordering is unchanged, and results are applied
/// through the same code path as a direct evaluation. What the caller controls is when a result arrives: telling
/// two outstanding candidates in a different order across two runs produces the same archive content, because
/// placement is keyed on canonical identity rather than arrival, but observer event ORDER will differ. Reproduce a
/// run by replaying the same asks and tells, not by assuming any interleaving is equivalent.
/// </para>
/// <para><b>For Beginners:</b> Normally you give the engine a judge and it runs the whole contest. Here you take
/// the organizer's clipboard instead: you call <see cref="AskAsync"/> to get the next few entries that need
/// scoring, score them however you like and whenever you like, then call <see cref="Tell"/> with the scores. The
/// contest advances only when you say so, which means it can be driven from another language, another process, or
/// a person clicking a button.</para>
/// </remarks>
public sealed class EvolutionSession<TGenome> : IDisposable
{
    // A QUEUE AND A SEMAPHORE RATHER THAN A CHANNEL, because this library targets
    // net471 as well as net8/net10 and System.Threading.Channels does not exist
    // there. Adding the package for one TFM would put a dependency in front of
    // every consumer to buy behaviour these two primitives already give: wait for
    // at least one item, then drain whatever else is ready.
    private readonly ConcurrentQueue<PendingEvaluation> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly ConcurrentDictionary<long, PendingEvaluation> _outstanding = new();
    private readonly CancellationTokenSource _cancellation = new();
    // Fires when no further candidates can arrive, so a caller blocked in AskAsync
    // is released rather than waiting on a queue nothing will write to again. A
    // cancellation source is used instead of releasing the semaphore N times
    // because N -- the number of waiters -- is not knowable without tracking it.
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource<EvolutionRunResult<TGenome>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Func<TGenome, string> _identity;
    private readonly Task _engineRun;
    private int _disposed;

    /// <summary>Initializes a session over an engine the caller has already configured.</summary>
    /// <param name="engineFactory">
    /// Builds the engine from the queueing task this session supplies. The factory shape exists because the engine
    /// requires its task at construction, and the task cannot exist before the session that owns its queue.
    /// </param>
    /// <param name="initialGenomes">Finite seed genomes, as <see cref="EvolutionEngine{TGenome}.RunAsync"/> takes.</param>
    /// <param name="canonicalIdentity">
    /// Returns a stable, collision-resistant identifier for a genome.
    /// <para>
    /// REQUIRED, WITH NO DEFAULT, and the first version of this class was wrong to
    /// have one. It fell back to <c>genome.ToString()</c>, which for any type that
    /// does not override it returns the type name -- so every distinct genome of
    /// that type shares one identity and the engine silently deduplicates them into
    /// a single candidate. A search that quietly evaluates one thing and reports it
    /// as many is worse than one that refuses to start, and a default that is right
    /// only for types that happen to override <c>ToString</c> is a trap rather than
    /// a convenience.
    /// </para>
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="initialGenomes"/> yields more than <see cref="MaxSeeds"/> items.
    /// </exception>
    public EvolutionSession(
        Func<IEvolutionTask<TGenome>, EvolutionEngine<TGenome>> engineFactory,
        IEnumerable<TGenome> initialGenomes,
        Func<TGenome, string> canonicalIdentity)
    {
        Guard.NotNull(engineFactory);
        Guard.NotNull(initialGenomes);
        Guard.NotNull(canonicalIdentity);

        _identity = canonicalIdentity;
        TGenome[] seeds = MaterializeSeeds(initialGenomes);
        var task = new QueueingTask(this);
        EvolutionEngine<TGenome> engine = engineFactory(task)
            ?? throw new ArgumentException("The engine factory returned null.", nameof(engineFactory));

        _engineRun = RunEngineAsync(engine, seeds);
    }

    /// <summary>Largest seed set accepted, so an endless sequence cannot hang construction.</summary>
    /// <remarks>
    /// `ToArray` on an infinite <see cref="IEnumerable{T}"/> never returns, and on a
    /// merely enormous one exhausts memory before evolution starts. Neither failure
    /// mentions seeds, so both are hard to diagnose from the symptom. Enumerating
    /// one past the limit and refusing turns both into an argument error naming the
    /// parameter.
    /// </remarks>
    public const int MaxSeeds = 100_000;

    /// <summary>Largest batch <see cref="AskAsync"/> will return, and preallocate for.</summary>
    /// <remarks>
    /// `new List(maxCount)` with an unrestricted count allocates whatever the caller
    /// names -- `int.MaxValue` exhausts the process before a single candidate is
    /// handed over. The cap bounds the allocation; asking for more is not an error,
    /// it simply yields at most this many.
    /// </remarks>
    public const int MaxBatchSize = 4096;

    private static TGenome[] MaterializeSeeds(IEnumerable<TGenome> initialGenomes)
    {
        var seeds = new List<TGenome>();
        foreach (TGenome genome in initialGenomes)
        {
            if (seeds.Count == MaxSeeds)
            {
                throw new ArgumentException(
                    $"An evolution session accepts at most {MaxSeeds} seeds.",
                    nameof(initialGenomes));
            }
            seeds.Add(genome);
        }
        return seeds.ToArray();
    }

    /// <summary>Gets a value indicating whether the run has finished and no further asks will yield work.</summary>
    public bool IsComplete => _completion.Task.IsCompleted;

    /// <summary>Gets the task that completes with the run result when evolution finishes.</summary>
    /// <remarks>
    /// Faults if the engine threw, and is canceled if <see cref="RequestStop"/> was followed by cancellation. Await
    /// this rather than polling <see cref="IsComplete"/> when the caller has somewhere to await.
    /// </remarks>
    public Task<EvolutionRunResult<TGenome>> Completion => _completion.Task;

    /// <summary>Requests that the engine stop at the next batch boundary, keeping results found so far.</summary>
    public void RequestStop() => _cancellation.Cancel();

    /// <summary>Waits for up to <paramref name="maxCount"/> candidates that need evaluation.</summary>
    /// <param name="maxCount">The largest batch to return. Must be positive.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// A batch of candidates, or an empty batch when the run has finished. An empty result is the only completion
    /// signal a polling caller needs, so a host that cannot await <see cref="Completion"/> can loop on it.
    /// </returns>
    /// <remarks>
    /// Returns as soon as ANY work is available rather than waiting to fill the batch. Waiting for a full batch
    /// would stall whenever the engine has fewer candidates in flight than the caller asked for, which is the
    /// normal state at the start of a run and near its end.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCount"/> is not positive.</exception>
    public async Task<IReadOnlyList<EvolutionAskItem<TGenome>>> AskAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxCount));

        // Capped before it reaches the allocator: the caller names the number.
        int wanted = Math.Min(maxCount, MaxBatchSize);
        var batch = new List<EvolutionAskItem<TGenome>>(wanted);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _closed.Token);
        try
        {
            // Block for the first item; take the rest only if they are already there.
            await _available.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            // The run finished while we waited. An empty batch is the completion signal.
            return batch;
        }

        if (_queue.TryDequeue(out PendingEvaluation? first))
        {
            _outstanding[first.Candidate.EvaluationId] = first;
            batch.Add(new EvolutionAskItem<TGenome>(first.Candidate, first.Context));
        }

        // Drain what is already queued, taking a permit for each so the count stays
        // in step with the queue.
        while (batch.Count < wanted && _available.Wait(0))
        {
            if (!_queue.TryDequeue(out PendingEvaluation? next))
            {
                // Permit taken with nothing behind it: hand it back rather than
                // losing a wakeup a concurrent writer is about to need.
                _available.Release();
                break;
            }
            _outstanding[next.Candidate.EvaluationId] = next;
            batch.Add(new EvolutionAskItem<TGenome>(next.Candidate, next.Context));
        }

        return batch;
    }

    /// <summary>Reports the outcome of one previously asked candidate.</summary>
    /// <param name="evaluationId">The <see cref="EvolutionCandidate{TGenome}.EvaluationId"/> that was asked for.</param>
    /// <param name="result">The evaluation outcome.</param>
    /// <returns>
    /// <see langword="true"/> if the candidate was outstanding and has now been completed; <see langword="false"/>
    /// if it was unknown or already told.
    /// </returns>
    /// <remarks>
    /// RETURNS A BOOL RATHER THAN THROWING, because the caller is frequently another language or another process
    /// and a duplicate tell is a routine consequence of a retry, not a programming error. Silently ignoring it
    /// would hide a real bug; throwing across an ABI would turn a recoverable duplicate into a crashed host.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public bool Tell(long evaluationId, EvolutionTaskResult result)
    {
        Guard.NotNull(result);
        if (!_outstanding.TryRemove(evaluationId, out PendingEvaluation? pending)) return false;
        return pending.Completion.TrySetResult(result);
    }

    /// <summary>Releases the session, stopping the engine and failing anything still outstanding.</summary>
    /// <remarks>
    /// <para>
    /// DISPOSAL RACES THE ENGINE, and getting that wrong deadlocks rather than throws. Cancelling the run makes
    /// the engine unwind on its own thread, which takes time and, while it happens, still touches this
    /// session's cancellation sources and semaphore. Disposing them here -- the obvious thing to write --
    /// meant the engine's own shutdown path faulted on an ObjectDisposedException, the completion was never
    /// set, and every awaiter of <see cref="Completion"/> waited forever. Caught by the disposal test, which
    /// timed out at thirty seconds instead of failing an assertion.
    /// </para>
    /// <para>
    /// So disposal is in two parts: everything that unblocks the engine happens now, and the handles it may
    /// still be using are released only once the run has actually finished.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cancellation.Cancel();
        _closed.Cancel();

        // Anything the caller never told would otherwise leave the engine awaiting forever, and with it the task
        // this session hands back.
        foreach (KeyValuePair<long, PendingEvaluation> entry in _outstanding)
        {
            entry.Value.Completion.TrySetResult(
                EvolutionTaskResult.Failed("session_disposed", "The evolution session was disposed before this candidate was told."));
        }
        _outstanding.Clear();

        if (_engineRun.IsCompleted) ReleaseHandles();
        else _engineRun.ContinueWith(
            static (_, state) => ((EvolutionSession<TGenome>)state!).ReleaseHandles(),
            this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseHandles()
    {
        _cancellation.Dispose();
        _closed.Dispose();
        _available.Dispose();
    }

    private async Task RunEngineAsync(EvolutionEngine<TGenome> engine, TGenome[] seeds)
    {
        try
        {
            EvolutionRunResult<TGenome> result = await engine.RunAsync(seeds, _cancellation.Token).ConfigureAwait(false);
            _completion.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            // No token is passed: reading `_cancellation.Token` here would touch a source disposal may
            // already have released, turning a clean cancellation into a fault nobody observes.
            _completion.TrySetCanceled();
        }
#pragma warning disable CA1031 // Do not catch general exception types
        // DELIBERATELY GENERAL, and narrowing it would lose the only report. This
        // runs on a detached background task, so an exception escaping here goes
        // to TaskScheduler.UnobservedTaskException -- a process-level event the
        // caller never sees -- while `Completion` hangs forever. Whatever the
        // engine, the task, or a variation operator threw belongs to the caller,
        // and this is the only place that can hand it over.
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
#pragma warning restore CA1031
        finally
        {
            // No further candidates can appear, so a caller blocked in AskAsync must be
            // released rather than left waiting on a queue nothing will ever write to again.
            // Guarded because disposal may have cancelled and released it already.
            try
            {
                if (!_closed.IsCancellationRequested) _closed.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposed while the run was unwinding; the waiters it would have woken are gone too.
            }
        }
    }

    private sealed class PendingEvaluation
    {
        public PendingEvaluation(EvolutionCandidate<TGenome> candidate, EvolutionEvaluationContext context)
        {
            Candidate = candidate;
            Context = context;
        }

        public EvolutionCandidate<TGenome> Candidate { get; }
        public EvolutionEvaluationContext Context { get; }

        public TaskCompletionSource<EvolutionTaskResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// The task the engine actually sees: canonicalization is delegated, evaluation is parked on the queue.
    /// </summary>
    private sealed class QueueingTask : IEvolutionTask<TGenome>
    {
        private readonly EvolutionSession<TGenome> _session;

        public QueueingTask(EvolutionSession<TGenome> session) => _session = session;

        public string Id => "ask-tell";

        public string VersionHash => "1";

        public string EvaluatorVersionHash => "external";

        public ValueTask<EvolutionCanonicalGenome<TGenome>> CanonicalizeAsync(
            TGenome genome,
            CancellationToken cancellationToken = default)
        {
            // Identity comes from the caller, never from ToString. See the
            // `canonicalIdentity` parameter for why there is no default.
            //
            // `Guard.NotNull` is not used for the genome: TGenome is unconstrained, so
            // it may be a value type, and the guard requires a reference type. An
            // explicit comparison covers both without constraining the type parameter,
            // which would be a breaking change to the engine's own signature.
            cancellationToken.ThrowIfCancellationRequested();
            if (genome is null) throw new ArgumentNullException(nameof(genome));

            string id = _session._identity(genome);
            if (string.IsNullOrEmpty(id))
            {
                throw new InvalidOperationException(
                    "The canonical identity function returned an empty id; distinct genomes " +
                    "would be deduplicated into one candidate.");
            }
            return new ValueTask<EvolutionCanonicalGenome<TGenome>>(
                new EvolutionCanonicalGenome<TGenome>(genome, id));
        }

        public async ValueTask<EvolutionTaskResult> EvaluateAsync(
            EvolutionCandidate<TGenome> candidate,
            EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            var pending = new PendingEvaluation(candidate, context);

            if (_session._closed.IsCancellationRequested)
            {
                // The session is shutting down. A failure result lets the engine record a
                // diagnostic and finish rather than hanging on a completion nobody will supply.
                return EvolutionTaskResult.Failed("session_closed", "The evolution session is no longer accepting evaluations.");
            }

            _session._queue.Enqueue(pending);
            _session._available.Release();

            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<EvolutionTaskResult>)state!).TrySetResult(
                    EvolutionTaskResult.Failed("evaluation_canceled", "The evaluation was canceled before the host reported a result.")),
                pending.Completion);

            return await pending.Completion.Task.ConfigureAwait(false);
        }
    }
}
