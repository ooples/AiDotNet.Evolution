using System.Diagnostics;

namespace AiDotNet.Evolution;

public sealed partial class EvolutionEngine<TGenome>
{
    /// <summary>
    /// Runs the search as a continuously refilled window of evaluations instead of as a sequence of batches.
    /// </summary>
    /// <param name="seeds">The caller's seed genomes.</param>
    /// <param name="seedIndex">How many seeds a resumed run already consumed.</param>
    /// <param name="runTimer">The stopwatch the time limit is measured on.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>Why the run stopped.</returns>
    /// <remarks>
    /// <para>
    /// The window holds at most <c>MaxInFlight</c> evaluations, defaulting to the worker count. Each time the oldest
    /// evaluation in the window finishes it is committed on its own and exactly one replacement proposal is prepared,
    /// so no worker waits for the rest of a batch and no proposal is more than one window behind the archive. Pairing
    /// each commit with exactly one preparation is also what keeps the mode deterministic: the proposal for evaluation
    /// N is prepared once evaluation N minus the window size has committed, whatever the worker count or the
    /// evaluator's timing.
    /// </para>
    /// <para>
    /// Three rules keep that schedule honest. Commits follow evaluation-id order under
    /// <see cref="EvolutionExecutionMode.Deterministic"/>, so a finished evaluation waits for its predecessors. The
    /// oldest evaluation is dispatched even when its island is at its in-flight quota, so a quota can delay work but
    /// never deadlock it. And a checkpoint is written only once the window has drained, because the run counters a
    /// checkpoint records already count every in-flight proposal, so writing one mid-window would claim proposals
    /// whose outcome was never committed.
    /// </para>
    /// </remarks>
    private async Task<EvolutionStopReason> RunContinuousLoopAsync(TGenome[] seeds, int seedIndex, Stopwatch runTimer,
        CancellationToken cancellationToken)
    {
        var state = new ContinuousState(seeds, seedIndex, _options.ResolveInFlightWindow(),
            _variation is IDeterministicConcurrentVariationOperator<TGenome> concurrent && concurrent.SupportsDeterministicConcurrency);

        using var semaphore = new SemaphoreSlim(_options.MaxDegreeOfParallelism, _options.MaxDegreeOfParallelism);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await FillWindowAsync(state, semaphore, runTimer, cancellationToken).ConfigureAwait(false);

                if (state.InFlight.Count == 0 && state.Proposing.Count == 0)
                {
                    // The window is drained, which is the only point at which the run's counters describe exactly
                    // what has been committed. Capturing here is what makes the final checkpoint the run's real
                    // final state instead of whatever was last captured, which with checkpointing switched off was
                    // the empty state the run started from.
                    CaptureSafeState(state.Seeds, state.CommittedSeeds);
                    if (state.DrainingForCheckpoint)
                    {
                        state.DrainingForCheckpoint = false;
                        await SaveCheckpointAsync(force: false, cancellationToken).ConfigureAwait(false);
                        if (state.Stop is null && Volatile.Read(ref _stopRequested) == 0) continue;
                    }

                    if (Volatile.Read(ref _stopRequested) != 0) return EvolutionStopReason.Canceled;
                    return state.Stop ?? StopReasonWithNothingInFlight();
                }

                DispatchWaiting(state, semaphore, cancellationToken);
                StartProposals(state, cancellationToken);
                await AdmitFinishedProposalsAsync(state, semaphore, cancellationToken).ConfigureAwait(false);
                List<Task> awaited = WaitableWork(state);
                if (awaited.Count > 0)
                {
                    await Task.WhenAny(awaited).ConfigureAwait(false);
                    await ReapFinishedAsync(state, semaphore, cancellationToken).ConfigureAwait(false);
                    await AdmitFinishedProposalsAsync(state, semaphore, cancellationToken).ConfigureAwait(false);
                    StartProposals(state, cancellationToken);
                }

                while (TryTakeCommittable(state, out WorkItem? ready) && ready is not null)
                {
                    state.InFlight.Remove(ready);
                    if (ready.IsSeed) state.CommittedSeeds++;

                    // A single-item commit reuses the batch commit path, so archives, cache, elites, history,
                    // observers, and the failure policy all behave exactly as they do in batch mode.
                    bool failedFast = await CommitBatchAsync(new List<WorkItem> { ready }, CancellationToken.None)
                        .ConfigureAwait(false);
                    UpdateEarlyStopping(1);
                    await MigrateIfBatchBoundaryAsync(ready.EvaluationId).ConfigureAwait(false);

                    if (failedFast) state.Stop ??= EvolutionStopReason.CandidateFailure;
                    else if (IsTargetReached()) state.Stop ??= EvolutionStopReason.TargetReached;
                    else if (IsEarlyStopped()) state.Stop ??= EvolutionStopReason.EarlyStopped;
                    if (state.Stop is not null) break;

                    if (_checkpointStore is not null && _options.CheckpointInterval > 0 &&
                        _commitsSinceCheckpoint >= _options.CheckpointInterval)
                    {
                        state.DrainingForCheckpoint = true;
                        break;
                    }

                    // Refill at this commit point, against the archive as it stands after exactly these commits. In a
                    // full window that is one replacement per commit. A window left short (no parent existed yet when
                    // the seeds were admitted) must also be topped up here: topping it up at the loop head instead
                    // happened after however many commits a wake-up had batched, so a slow evaluation changed which
                    // archive later proposals were planned from.
                    await FillWindowAsync(state, semaphore, runTimer, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            RollbackInFlight(state);
            throw;
        }
        finally
        {
            // Evaluations already inside the worker pool release their slot as they unwind, so disposing the
            // semaphore while any of them is still running would fault a task nobody is waiting on. Every run that
            // reaches its time limit takes this path, so it is not an exotic one.
            await DrainRunningAsync(state).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for every dispatched evaluation and outstanding proposal to unwind, ignoring how each ended.</summary>
    private static async Task DrainRunningAsync(ContinuousState state)
    {
        if (state.Running.Count == 0 && state.Proposing.Count == 0) return;
        try
        {
            await Task.WhenAll(state.Running.Values.Concat(state.Proposing.Select(proposal => proposal.Response)
                .OfType<Task>())).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception exception) when (EvolutionExceptionPolicy.IsRecoverable(exception))
#pragma warning restore CA1031
        {
            // Whatever these tasks were doing, the run is already ending and their outcomes are discarded by the
            // rollback. Waiting is only about not disposing the pool from under them.
        }
        finally
        {
            state.Running.Clear();
        }
    }

    /// <summary>Starts planned model calls, oldest first, until <c>Pipeline.MaxProposalConcurrency</c> are running.</summary>
    /// <remarks>
    /// Calls start on the loop, in identifier order, so an operator that fixes its view of history when a call starts
    /// sees horizons that never go backwards. The cap bounds spend and provider load separately from the window, which
    /// bounds staleness; it changes only when calls run, never what they are asked.
    /// </remarks>
    private void StartProposals(ContinuousState state, CancellationToken cancellationToken)
    {
        int running = state.Proposing.Count(proposal => proposal.Response is not null && !proposal.Response.IsCompleted);
        foreach (ContinuousProposal proposal in state.Proposing)
        {
            if (running >= _options.Pipeline.MaxProposalConcurrency) break;
            if (proposal.Response is not null) continue;
            proposal.Response = InvokeVariationAsync(proposal.Request, cancellationToken);
            if (!proposal.Response.IsCompleted) running++;
        }
    }

    /// <summary>Returns the lowest identifier not yet committed: every admitted or outstanding item is uncommitted.</summary>
    private long LowestUncommitted(ContinuousState state) =>
        state.InFlight.Select(entry => entry.EvaluationId)
            .Concat(state.Proposing.Select(entry => entry.Request.EvaluationId))
            .DefaultIfEmpty(_nextEvaluationId).Min();

    /// <summary>Returns the tasks whose completion can let the loop make progress.</summary>
    private List<Task> WaitableWork(ContinuousState state)
    {
        var tasks = new List<Task>(state.Running.Values);
        if (state.Proposing.Count == 0) return tasks;
        // Any running call can unblock the loop: its completion frees a slot for the next planned call. Finished calls
        // are left out, because one that is not the next to admit would make every wait return at once.
        foreach (ContinuousProposal proposal in state.Proposing)
            if (proposal.Response is { IsCompleted: false } response) tasks.Add(response);
        return tasks;
    }

    /// <summary>Turns finished proposals into window entries and dispatches their evaluations.</summary>
    /// <remarks>
    /// Admission reads the evaluation cache, the duplicate set and the archive (for structural novelty), and commits
    /// change all three. A deterministic run therefore admits proposal N at exactly one point: in identifier order,
    /// once the committed prefix reaches N minus <see cref="ContinuousState.AdmissionLag"/>, while
    /// <see cref="TryTakeCommittable"/> holds the commit of that prefix's next item until N is admitted. What N sees
    /// is then fixed, whenever its model call happens to return. An opportunistic run admits whatever has finished.
    /// </remarks>
    private async Task AdmitFinishedProposalsAsync(ContinuousState state, SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        bool admitted = false;
        while (true)
        {
            int index = _options.ExecutionMode == EvolutionExecutionMode.Deterministic
                ? (state.Proposing.Count > 0 && state.Proposing[0].Response is { IsCompleted: true } &&
                   LowestUncommitted(state) >= state.Proposing[0].Request.EvaluationId - state.AdmissionLag ? 0 : -1)
                : state.Proposing.FindIndex(proposal => proposal.Response is { IsCompleted: true });
            if (index < 0) break;
            ContinuousProposal proposal = state.Proposing[index];
            if (proposal.Response is not Task<VariationResponse> call) break;
            // The proposal leaves Proposing only once it is an InFlight item. If either await throws (a cancelled model
            // call, or cancellation during completion), it is still listed, so the rollback undoes its identifier and
            // counters instead of losing them.
            VariationResponse response = await call.ConfigureAwait(false);
            WorkItem item = await CompleteVariationAsync(proposal.Request, response, cancellationToken).ConfigureAwait(false);
            item.IsSeed = false;
            state.Proposing.RemoveAt(index);
            state.InFlight.Add(item);
            admitted = true;
        }
        if (admitted) DispatchWaiting(state, semaphore, cancellationToken);
    }

    /// <summary>Undoes exactly the proposals that were prepared but never committed.</summary>
    /// <remarks>
    /// A snapshot taken after the last commit cannot do this job, because by then the window already holds prepared
    /// proposals whose identifiers and counters are inside the snapshot: restoring it would leave the run counting
    /// proposals whose deduplication entries had just been removed. Undoing each in-flight item by what it actually
    /// consumed is exact, and needs no snapshot at all.
    /// </remarks>
    private void RollbackInFlight(ContinuousState state)
    {
        long? lowestInFlightId = null;
        foreach (ContinuousProposal proposal in state.Proposing)
        {
            lowestInFlightId = lowestInFlightId.HasValue
                ? Math.Min(lowestInFlightId.Value, proposal.Request.EvaluationId)
                : proposal.Request.EvaluationId;
            _proposals--;
            _generation--;
            int island = proposal.Request.Island;
            if (island >= 0 && island < _islandGenerations.Length) _islandGenerations[island]--;
        }
        state.Proposing.Clear();
        foreach (WorkItem item in state.InFlight)
        {
            lowestInFlightId = lowestInFlightId.HasValue
                ? Math.Min(lowestInFlightId.Value, item.EvaluationId)
                : item.EvaluationId;
            if (item.AddedToSeen && item.Candidate is not null) _seen.Remove(item.Candidate.CanonicalGenome.Id);
            _evaluationAttempts -= item.ChargedAttempts;
            _proposals--;
            if (item.IsSeed) continue;

            _generation--;
            if (item.Island >= 0 && item.Island < _islandGenerations.Length) _islandGenerations[item.Island]--;
        }

        // Deterministic dispatch commits in identifier order, so every rolled-back id is above every committed id and
        // the contiguous suffix can be reused. Completion-ordered dispatch may already have committed higher ids;
        // retaining its allocation cursor prevents those identities and their random streams from being reused.
        if (_options.ExecutionMode == EvolutionExecutionMode.Deterministic && lowestInFlightId.HasValue)
            _nextEvaluationId = lowestInFlightId.Value;
        state.InFlight.Clear();
    }

    /// <summary>Reports why a continuous run has nothing left to do.</summary>
    private EvolutionStopReason StopReasonWithNothingInFlight()
    {
        if (_evaluationAttempts >= _options.MaxEvaluationAttempts) return EvolutionStopReason.EvaluationBudgetReached;
        if (_proposals >= _options.MaxProposals) return EvolutionStopReason.ProposalBudgetReached;
        if (_generation >= _options.MaxGenerations) return EvolutionStopReason.GenerationLimitReached;
        return EvolutionStopReason.NoCandidates;
    }

    /// <summary>Fills the window up to its size, stopping at the first proposal that cannot be made.</summary>
    private async Task FillWindowAsync(ContinuousState state, SemaphoreSlim semaphore, Stopwatch runTimer,
        CancellationToken cancellationToken)
    {
        while (state.Occupied < state.Window && state.Stop is null && !state.DrainingForCheckpoint &&
               await FillOneAsync(state, semaphore, runTimer, cancellationToken).ConfigureAwait(false))
        {
            // FillOneAsync performs the admission; the loop continues until the window or a run limit stops it.
        }
    }

    /// <summary>Prepares and admits exactly one proposal into the window.</summary>
    /// <returns><c>true</c> when a proposal was admitted; <c>false</c> when nothing more can be proposed now.</returns>
    private async Task<bool> FillOneAsync(ContinuousState state, SemaphoreSlim semaphore, Stopwatch runTimer,
        CancellationToken cancellationToken)
    {
        if (state.Stop is not null || state.DrainingForCheckpoint || state.Occupied >= state.Window) return false;
        cancellationToken.ThrowIfCancellationRequested();

        // A stop request has to reach the proposing side, not only the loop head. Checking it only where the window
        // is already empty means a steady-state run refills forever and never sees the request at all.
        if (Volatile.Read(ref _stopRequested) != 0) return false;

        EvolutionStopReason? limit = GetLimitStopReason(runTimer);
        if (limit.HasValue)
        {
            state.Stop = limit.Value;
            return false;
        }

        // Admitted work that has not been charged yet still consumes the budget, so counting it here keeps admission
        // a function of the window's contents rather than of how far the evaluator happens to have got.
        // With overlapping model calls, whether a late proposal has resolved yet (and turned out to be a cache hit or a
        // duplicate) depends on which call returned first, so only the ones the commit order guarantees are admitted may
        // be discounted: every identifier up to the last commit plus the admission lag. Later ones stay reserved whether
        // or not they have resolved, which keeps this check a function of the commit count. The cost is that a run can
        // stop up to AdmissionLag evaluations short of the budget when those late proposals would not have needed one.
        // Serial proposing resolves each proposal before the next check, so it keeps the exact count.
        int uncharged;
        if (state.ConcurrentProposals)
        {
            long admittedThrough = LowestUncommitted(state) - 1 + state.AdmissionLag;
            uncharged = state.InFlight.Count(item => item.ChargedAttempts == 0 &&
                                                     (item.RequiresEvaluation || item.EvaluationId > admittedThrough)) +
                        state.Proposing.Count;
        }
        else
        {
            uncharged = state.InFlight.Count(item => item.RequiresEvaluation && item.ChargedAttempts == 0);
        }
        if (_evaluationAttempts + uncharged >= _options.MaxEvaluationAttempts)
        {
            state.Stop = EvolutionStopReason.EvaluationBudgetReached;
            return false;
        }

        WorkItem item;
        bool isSeed = state.SeedIndex < state.Seeds.Length;
        if (isSeed)
        {
            PreparedProposal seeded = await PrepareSeedAsync(state.Seeds[state.SeedIndex], cancellationToken)
                .ConfigureAwait(false);
            state.SeedIndex++;
            item = seeded.Item;
        }
        else
        {
            if (_generation >= _options.MaxGenerations)
            {
                state.Stop = EvolutionStopReason.GenerationLimitReached;
                return false;
            }
            if (state.ConcurrentProposals)
            {
                // The request is planned here, paired with a commit exactly as a serial proposal would be, and
                // reads a snapshot of the archive, so the model call can overlap later commits without seeing them.
                // Every identifier below the oldest outstanding one has committed, so that is the history horizon.
                long committedBefore = LowestUncommitted(state);
                VariationRequest? request = CreateVariationRequest(new Dictionary<int, PipelineArchiveContext>(), committedBefore);
                if (request is null) return false;
                state.Proposing.Add(new ContinuousProposal(request));
                StartProposals(state, cancellationToken);
                return true;
            }
            PreparedProposal? prepared = await PrepareVariationAsync(cancellationToken).ConfigureAwait(false);
            if (prepared is null) return false;
            item = prepared.Item;
        }

        item.IsSeed = isSeed;
        state.InFlight.Add(item);
        DispatchWaiting(state, semaphore, cancellationToken);
        return true;
    }

    /// <summary>Starts every admitted evaluation the worker pool and the island quotas currently allow.</summary>
    /// <remarks>
    /// The oldest admitted evaluation is exempt from the island quota. Commits follow identifier order, so holding the
    /// oldest one back would stop every commit, which would in turn stop the quota from ever freeing; exempting it
    /// keeps the quota a throttle rather than a deadlock.
    /// </remarks>
    private void DispatchWaiting(ContinuousState state, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        int quota = _options.MaxInFlightPerIsland;
        List<WorkItem> ordered = state.InFlight.OrderBy(item => item.EvaluationId).ToList();
        var perIsland = new int[_islands.Length];
        foreach (WorkItem item in ordered)
            if (state.Running.ContainsKey(item.EvaluationId)) perIsland[item.Island]++;

        long? oldestPendingId = ordered
            .Where(item => item.RequiresEvaluation && !state.Running.ContainsKey(item.EvaluationId))
            .Select(item => (long?)item.EvaluationId)
            .FirstOrDefault();
        foreach (WorkItem item in ordered)
        {
            if (!item.RequiresEvaluation || state.Running.ContainsKey(item.EvaluationId)) continue;
            bool isOldest = oldestPendingId == item.EvaluationId;

            if (_evaluationAttempts >= _options.MaxEvaluationAttempts)
            {
                // Only give up on the budget once nothing is still running, because a cascade stage that screens a
                // candidate out refunds its attempt when it completes and may make room after all.
                if (state.Running.Count > 0) continue;
                item.RequiresEvaluation = false;
                item.Result = new EvolutionTaskResult(EvolutionEvaluationStatus.Skipped,
                    diagnostics: new[] { new EvolutionDiagnostic("budget_exhausted", "Evaluator budget was exhausted before dispatch.") });
                item.CompletionOrder = Interlocked.Increment(ref _completionSequence);
                continue;
            }

            if (!isOldest && quota > 0 && perIsland[item.Island] >= quota) continue;

            item.AttemptCount++;
            item.ChargedAttempts++;
            _evaluationAttempts++;
            perIsland[item.Island]++;
            state.Running[item.EvaluationId] = EvaluateAfterDelayAsync(item, RetryDelayForAttempt(item.AttemptCount),
                semaphore, cancellationToken);
        }
    }

    /// <summary>Waits out any retry backoff and then evaluates one item inside a worker slot.</summary>
    private async Task EvaluateAfterDelayAsync(WorkItem item, TimeSpan delay, SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero) await EvolutionClock.Delay(_options.TimeProvider, delay, cancellationToken).ConfigureAwait(false);
        await EvaluateWithSlotAsync(item, semaphore, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Collects finished evaluations, refunds screened-out attempts, and leaves retryable ones queued.</summary>
    private async Task ReapFinishedAsync(ContinuousState state, SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        long[] finished = state.Running.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key)
            .OrderBy(id => id).ToArray();
        foreach (long id in finished)
        {
            Task task = state.Running[id];
            state.Running.Remove(id);
            await task.ConfigureAwait(false);

            WorkItem item = state.InFlight.Single(candidate => candidate.EvaluationId == id);
            if (item.CascadeRejectedStage.HasValue && !_options.Cascade.ChargeRejectedStagesToBudget)
            {
                _evaluationAttempts--;
                item.ChargedAttempts--;
            }

            bool retry = IsRetryable(item.Result) && item.AttemptCount <= _options.MaxRetries &&
                         _evaluationAttempts < _options.MaxEvaluationAttempts;
            if (!retry) item.RequiresEvaluation = false;
        }

        DispatchWaiting(state, semaphore, cancellationToken);
    }

    /// <summary>Picks the next window entry that may be committed, or none when the head is still running.</summary>
    private bool TryTakeCommittable(ContinuousState state, out WorkItem? ready)
    {
        ready = null;
        if (state.InFlight.Count == 0) return false;

        if (_options.ExecutionMode == EvolutionExecutionMode.Deterministic)
        {
            WorkItem head = state.InFlight.OrderBy(item => item.EvaluationId).First();
            if (head.RequiresEvaluation || head.Result is null) return false;
            // The other half of the admission rule: every proposal within AdmissionLag of this commit must be admitted
            // first, so none of them can see this commit's effect on the cache, duplicate set or archive.
            if (state.Proposing.Count > 0 && state.Proposing[0].Request.EvaluationId <= head.EvaluationId + state.AdmissionLag)
                return false;
            ready = head;
            return true;
        }

        ready = state.InFlight.Where(item => !item.RequiresEvaluation && item.Result is not null)
            .OrderBy(item => item.CompletionOrder).ThenBy(item => item.EvaluationId).FirstOrDefault();
        return ready is not null;
    }

    /// <summary>Advances the migration counter once per logical batch worth of committed evaluations.</summary>
    /// <param name="committedEvaluationId">The identifier of the evaluation that just committed.</param>
    /// <remarks>
    /// Continuous dispatch has no batches, but <c>MigrationInterval</c> is defined in them, so the boundary is taken
    /// from the committed identifier rather than from a counter. That keeps migration on exactly the same schedule as
    /// batch mode and needs no extra checkpoint state, because identifiers are restored on resume.
    /// </remarks>
    private async Task MigrateIfBatchBoundaryAsync(long committedEvaluationId)
    {
        if (_options.MigrationInterval <= 0 || _islands.Length <= 1) return;
        int batchSize = Math.Max(1, _options.ProposalBatchSize);
        if ((committedEvaluationId + 1) % batchSize != 0) return;
        _batchesSinceMigration++;
        await MigrateIfDueAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The mutable bookkeeping of one continuous run, kept in one place so the loop reads as a pipeline.</summary>
    private sealed class ContinuousState
    {
        public ContinuousState(TGenome[] seeds, int seedIndex, int window, bool concurrentProposals)
        {
            ConcurrentProposals = concurrentProposals;
            AdmissionLag = window / 2;
            Seeds = seeds;
            SeedIndex = seedIndex;
            CommittedSeeds = seedIndex;
            Window = window;
        }

        public TGenome[] Seeds { get; }
        public int Window { get; }
        public int SeedIndex { get; set; }
        public int CommittedSeeds { get; set; }
        public bool DrainingForCheckpoint { get; set; }
        public EvolutionStopReason? Stop { get; set; }
        public List<WorkItem> InFlight { get; } = new();
        public Dictionary<long, Task> Running { get; } = new();
        /// <summary>Planned proposals whose model call is still outstanding, in identifier order.</summary>
        public List<ContinuousProposal> Proposing { get; } = new();
        /// <summary>Whether the operator lets proposals overlap each other and later commits.</summary>
        public bool ConcurrentProposals { get; }
        /// <summary>
        /// How many commits a finished proposal may run ahead of: proposal N is admitted when the committed prefix
        /// reaches N minus this. Half the window, which is part of the run's identity, so up to this many evaluations
        /// overlap while the rest of the window keeps model calls running.
        /// </summary>
        public int AdmissionLag { get; }
        /// <summary>Window slots taken by outstanding proposals and admitted evaluations together.</summary>
        public int Occupied => InFlight.Count + Proposing.Count;
    }

    /// <summary>A planned proposal whose model call is running outside the loop.</summary>
    private sealed class ContinuousProposal(VariationRequest request)
    {
        public VariationRequest Request { get; } = request;
        /// <summary>The model call, or null while it waits for a proposal slot.</summary>
        public Task<VariationResponse>? Response { get; set; }
    }
}
