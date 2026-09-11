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
/// <b>Determinism follows the engine's dispatch and commit contracts.</b> Canonical identity does not make arbitrary
/// arrival orders equivalent: opportunistic commits can change feedback, future proposals, and the final archive.
/// Exact replay requires the same external responses and applicable scheduling provenance; timeouts and retries
/// can change the trajectory even when the random seed is unchanged.
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

    /// <summary>Serialises taking work with failing it.</summary>
    /// <remarks>
    /// TAKING A CANDIDATE IS TWO STEPS, and a stop landing between them stranded it. The
    /// dequeue removes it from `_queue` and the registration puts it in `_outstanding`;
    /// a `FailOutstanding` in that window drains both and touches neither, so the
    /// evaluation the engine is awaiting is settled by nobody and the run never
    /// finishes. Both sequences take this, so the pair is indivisible with respect to
    /// the drain. Nothing awaits while holding it, and the completion sources run their
    /// continuations asynchronously, so it cannot deadlock on a caller's continuation.
    /// </remarks>
    private readonly object _handover = new();
    private readonly CancellationTokenSource _cancellation = new();
    // Fires when no further candidates can arrive, so a caller blocked in AskAsync
    // is released rather than waiting on a queue nothing will write to again. A
    // cancellation source is used instead of releasing the semaphore N times
    // because N -- the number of waiters -- is not knowable without tracking it.
    private readonly CancellationTokenSource _closed = new();
    private readonly TaskCompletionSource<EvolutionRunResult<TGenome>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Func<TGenome, string> _identity;
    private readonly EvolutionExternalTaskIdentity _taskIdentity;
    private readonly bool _allowLegacyTells;
    private readonly EvolutionEngine<TGenome> _engine;
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
        : this(engineFactory, initialGenomes, canonicalIdentity, new EvolutionExternalTaskIdentity("ask-tell", "1", "external"), true) { }

    /// <summary>Creates a fingerprinted session requiring fenced <see cref="TellAttempt"/> results.</summary>
    /// <remarks>Inject the matching genome codec through the engine factory for checkpoints. This local queue is not a durable coordinator.</remarks>
    public EvolutionSession(Func<IEvolutionTask<TGenome>, EvolutionEngine<TGenome>> engineFactory,
        IEnumerable<TGenome> initialGenomes, Func<TGenome, string> canonicalIdentity, EvolutionExternalTaskIdentity taskIdentity)
        : this(engineFactory, initialGenomes, canonicalIdentity, taskIdentity, false) { }

    private EvolutionSession(Func<IEvolutionTask<TGenome>, EvolutionEngine<TGenome>> engineFactory,
        IEnumerable<TGenome> initialGenomes, Func<TGenome, string> canonicalIdentity, EvolutionExternalTaskIdentity taskIdentity, bool allowLegacyTells)
    {
        Guard.NotNull(engineFactory);
        Guard.NotNull(initialGenomes);
        Guard.NotNull(canonicalIdentity);
        Guard.NotNull(taskIdentity);

        _identity = canonicalIdentity;
        _taskIdentity = taskIdentity;
        _allowLegacyTells = allowLegacyTells;
        TGenome[] seeds = MaterializeSeeds(initialGenomes);
        var task = new QueueingTask(this);
        _engine = engineFactory(task)
            ?? throw new ArgumentException("The engine factory returned null.", nameof(engineFactory));

        _engineRun = RunEngineAsync(_engine, seeds);
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

    /// <summary>Gets the engine/task/evaluator compatibility fingerprint for this session.</summary>
    public string CompatibilityHash => _engine.CompatibilityHash;

    /// <summary>Requests that the engine stop at the next batch boundary, keeping results found so far.</summary>
    /// <remarks>
    /// <para>
    /// GRACEFUL, NOT CANCELLATION, and the difference is the whole value of calling it.
    /// The engine distinguishes the two deliberately: <see cref="EvolutionEngine{TGenome}.RequestStop"/>
    /// lets the current batch commit and returns a normal result whose archives hold
    /// everything found, while cancelling the token ends the run immediately and the
    /// caller gets an <see cref="OperationCanceledException"/> and nothing else.
    /// </para>
    /// <para>
    /// The first version of this method cancelled, which collapsed the two into the
    /// destructive one. It was caught by driving a real run from the host binary: a
    /// client that asked to stop and read the best genome found got
    /// <c>stopReason: "canceled"</c> and no result at all -- the archive was discarded
    /// at the moment the caller asked to see it.
    /// </para>
    /// <para>
    /// Outstanding evaluations are failed as part of stopping. The engine cannot
    /// commit a batch while it is still awaiting one, so a graceful stop with work
    /// outstanding would otherwise wait for results the caller has already decided not
    /// to produce.
    /// </para>
    /// </remarks>
    public void RequestStop()
    {
        // CLOSED FIRST, THEN DRAINED, and the order is the whole correctness argument.
        // Draining first leaves a window in which EvaluateAsync has already passed its
        // `_closed` check and enqueues afterwards: nothing then settles that completion,
        // the engine stays inside the current batch, and Completion never finishes. The
        // second half of the fix is the re-check in EvaluateAsync -- closing first
        // narrows the window, the re-check closes it.
        //
        // Releasing anyone blocked in AskAsync is the other reason to cancel here: no
        // further candidates are coming.
        if (!_closed.IsCancellationRequested) _closed.Cancel();
        _engine.RequestStop();
        FailOutstanding("stop_requested", "The session was asked to stop before this candidate was told.");
    }

    /// <summary>Ends the run immediately, discarding whatever the current batch held.</summary>
    /// <remarks>
    /// The counterpart to <see cref="RequestStop"/>, for when the work must end now and
    /// the results do not matter -- a shutting-down process, a cancelled request.
    /// <see cref="Completion"/> is canceled rather than returning a run result.
    /// </remarks>
    public void Abort()
    {
        _cancellation.Cancel();
        FailOutstanding("aborted", "The session was aborted before this candidate was told.");
    }

    /// <summary>Dequeues the next evaluation that still needs a result.</summary>
    /// <remarks>
    /// A queued evaluation can already carry a result: a stop settles it in place rather
    /// than removing it, because removing it would race the enqueue that put it there.
    /// Skipping those here is what keeps that safe.
    /// </remarks>
    private PendingEvaluation? HandOverNext()
    {
        lock (_handover)
        {
            while (_queue.TryDequeue(out PendingEvaluation? candidate))
            {
                if (candidate.Completion.Task.IsCompleted) continue;

                if (_closed.IsCancellationRequested)
                {
                    // Taken out of the queue after the door shut. Handing it to a caller
                    // who is about to be told the run is over would strand it, so it is
                    // settled here instead -- the same answer FailOutstanding would have
                    // given had it seen it.
                    candidate.Completion.TrySetResult(EvolutionTaskResult.Failed(
                        "session_closed",
                        "The evolution session stopped accepting evaluations while this one was queued."));
                    continue;
                }

                _outstanding[candidate.Candidate.EvaluationId] = candidate;
                return candidate;
            }
            return null;
        }
    }

    /// <summary>Completes every evaluation the engine is waiting on, asked for or not.</summary>
    /// <remarks>
    /// Both collections must be settled: the engine also awaits queued candidates that
    /// nobody has asked for yet. Otherwise its current batch cannot commit or observe stop.
    /// </remarks>
    private void FailOutstanding(string code, string message)
    {
        EvolutionTaskResult failure = EvolutionTaskResult.Failed(code, message);

        lock (_handover)
        {
            while (_queue.TryDequeue(out PendingEvaluation? queued))
                queued.Completion.TrySetResult(failure);

            foreach (KeyValuePair<long, PendingEvaluation> entry in _outstanding)
                entry.Value.Completion.TrySetResult(failure);
            _outstanding.Clear();
        }
    }

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
        while (batch.Count == 0)
        {
            try
            {
                await _available.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_closed.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                return batch;
            }

            // A permit may refer only to expired queued work. Consume obsolete permits and continue waiting;
            // returning an empty batch here falsely tells a remote caller that a retrying run has finished.
            if (HandOverNext() is { } first)
                batch.Add(new EvolutionAskItem<TGenome>(first.Candidate, first.Context, first.Identity));
            while (batch.Count < wanted && _available.Wait(0))
            {
                if (HandOverNext() is not { } next) break;
                batch.Add(new EvolutionAskItem<TGenome>(next.Candidate, next.Context, next.Identity));
            }
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
    /// This compatibility path accepts only first attempts on legacy-constructed sessions; it cannot fence cross-session
    /// results. Use <see cref="TellAttempt"/> with the session-issued work identity for retries and external transports.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
    public bool Tell(long evaluationId, EvolutionTaskResult result)
    {
        Guard.NotNull(result);
        lock (_handover)
        {
            // Legacy IDs cannot distinguish retries or another session. Keep only the original local first-attempt
            // compatibility path; fingerprinted sessions require the full token even for their first attempt.
            if (!_allowLegacyTells || !_outstanding.TryGetValue(evaluationId, out PendingEvaluation? pending) || pending.Context.AttemptCount != 1)
                return false;
            _outstanding.TryRemove(evaluationId, out _);
            return pending.Completion.TrySetResult(result);
        }
    }

    /// <summary>Completes exactly the run/evaluation/attempt/lease handed to the caller; stale, cross-session and duplicate results return false.</summary>
    public bool TellAttempt(EvolutionWorkIdentity identity, EvolutionTaskResult result)
    {
        Guard.NotNull(identity); Guard.NotNull(result);
        lock (_handover)
        {
            if (!_outstanding.TryGetValue(identity.EvaluationId, out PendingEvaluation? pending) || !pending.Identity.Matches(identity)) return false;
            _outstanding.TryRemove(identity.EvaluationId, out _);
            return pending.Completion.TrySetResult(result);
        }
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
        FailOutstanding(
            "session_disposed",
            "The evolution session was disposed before this candidate was told.");

        if (_engineRun.IsCompleted) ReleaseHandles();
        else _engineRun.ContinueWith(
            static (_, state) =>
            {
                if (state is not EvolutionSession<TGenome> session)
                {
                    throw new InvalidOperationException("The continuation requires its evolution session.");
                }
                session.ReleaseHandles();
            },
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
        public PendingEvaluation(EvolutionCandidate<TGenome> candidate, EvolutionEvaluationContext context, string runId)
        {
            Candidate = candidate;
            Context = context;
            Identity = new EvolutionWorkIdentity(runId, context.EvaluationId, context.AttemptCount, Guid.NewGuid().ToString("N"));
        }

        public EvolutionCandidate<TGenome> Candidate { get; }
        public EvolutionEvaluationContext Context { get; }
        public EvolutionWorkIdentity Identity { get; }

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

        public string Id => _session._taskIdentity.TaskId;

        public string VersionHash => _session._taskIdentity.TaskVersionHash;

        public string EvaluatorVersionHash => _session._taskIdentity.EvaluatorVersionHash;

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
            var pending = new PendingEvaluation(candidate, context, _session._engine.RunId);

            if (_session._closed.IsCancellationRequested)
            {
                // The session is shutting down. A failure result lets the engine record a
                // diagnostic and finish rather than hanging on a completion nobody will supply.
                return EvolutionTaskResult.Failed("session_closed", "The evolution session is no longer accepting evaluations.");
            }

            _session._queue.Enqueue(pending);
            _session._available.Release();

            // CHECKED AGAIN, AFTER ENQUEUEING. The check above can pass a moment before
            // a concurrent RequestStop drains the queue, in which case this candidate
            // lands in a queue nobody will drain again and awaits a completion nobody
            // will supply -- the engine then sits in the current batch forever and both
            // Completion and a host's FinishAsync hang. Settling it here is safe because
            // TrySetResult is idempotent: if the drain did see it, this does nothing.
            if (_session._closed.IsCancellationRequested)
            {
                pending.Completion.TrySetResult(EvolutionTaskResult.Failed(
                    "session_closed",
                    "The evolution session stopped accepting evaluations while this one was being queued."));
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state =>
                {
                    if (state is not TaskCompletionSource<EvolutionTaskResult> completion)
                    {
                        throw new InvalidOperationException("The cancellation callback requires its evaluation completion.");
                    }
                    completion.TrySetResult(EvolutionTaskResult.Failed(
                        "evaluation_canceled", "The evaluation was canceled before the host reported a result."));
                },
                pending.Completion);

            try { return await pending.Completion.Task.ConfigureAwait(false); }
            finally
            {
                // An expired attempt must not remain outstanding, nor may its cleanup remove a replacement.
                lock (_session._handover)
                    if (_session._outstanding.TryGetValue(candidate.EvaluationId, out PendingEvaluation? current) && ReferenceEquals(current, pending))
                        _session._outstanding.TryRemove(candidate.EvaluationId, out _);
            }
        }
    }
}
