using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace AiDotNet.Evolution;

public sealed partial class EvolutionEngine<TGenome>
{
    private EvolutionPipelineStatistics? _pipelineStatistics;
    private EvolutionPipelineRateGate? _pipelineEvaluationRate;
    private readonly bool _pipelineConcurrentProposals;

    private bool SupportsConcurrentPipelineProposals() =>
        (_variation is IDeterministicConcurrentVariationOperator<TGenome> concurrent && concurrent.SupportsDeterministicConcurrency) ||
        (_variation is ResourceMeteredVariationOperator<TGenome> costed && costed.SupportsPipelineConcurrency);

    /// <summary>Gets a defensive snapshot of opt-in pipeline diagnostics, or null when pipeline dispatch has not run.</summary>
    public EvolutionPipelineReport? PipelineReport => _pipelineStatistics?.Snapshot();

    private async Task<EvolutionStopReason> RunPipelineLoopAsync(TGenome[] seeds, int seedIndex, Stopwatch runTimer, CancellationToken cancellationToken)
    {
        EvolutionPipelineOptions settings = _options.Pipeline;
        if (SupportsConcurrentPipelineProposals() != _pipelineConcurrentProposals)
            throw new InvalidOperationException("The configured proposal concurrency capability changed after engine construction.");
        int workers = _pipelineConcurrentProposals ? settings.MaxProposalConcurrency : 1;
        _pipelineStatistics = new(_options.ExecutionMode, workers, _options.MaxDegreeOfParallelism, settings.MaximumScheduleRecords,
            _options.RunId, _compatibilityHash, _nextEvaluationId, settings);
        using var proposalSlots = new SemaphoreSlim(workers, workers);
        using var evaluatorSlots = new SemaphoreSlim(_options.MaxDegreeOfParallelism, _options.MaxDegreeOfParallelism);
        using var proposalRate = new EvolutionPipelineRateGate(settings.MinimumProposalStartInterval);
        using var evaluatorRate = new EvolutionPipelineRateGate(settings.MinimumEvaluationStartInterval);
        _pipelineEvaluationRate = evaluatorRate;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _stopRequested) != 0) return EvolutionStopReason.Canceled;
                EvolutionStopReason? limit = GetLimitStopReason(runTimer);
                if (limit.HasValue) return limit.Value;
                int waveLimit = (int)Math.Min(settings.WaveSize, Math.Min(_options.MaxProposals - _proposals, _options.MaxEvaluationAttempts - _evaluationAttempts));
                BatchTransaction transaction = CaptureBatchTransaction();
                var batch = new List<WorkItem>(waveLimit);
                var proposals = new Queue<PipelineProposal>();
                var allProposals = new List<Task>();
                var evaluations = new List<Task>();
                var retryEvaluations = new List<Task>();
                var activeEvaluations = new List<Task>();
                var snapshots = new Dictionary<int, PipelineArchiveContext>();
                using var waveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CancellationToken waveToken = waveCancellation.Token;
                var meteredVariation = _variation as ResourceMeteredVariationOperator<TGenome>;
                var meteredTask = _task as ResourceMeteredEvolutionTask<TGenome>;
                bool proposalResourcePhase = false, evaluationResourcePhase = false;
                try
                {
                    // A bounded planning buffer fixes identities, snapshots and all first-attempt reservations before
                    // callbacks may refund resources. The smaller submission queues still control runnable work.
                    List<PipelineProposal> plan = PlanPipelineWave(seeds, ref seedIndex, waveLimit, snapshots);
                    if (meteredVariation is not null) { meteredVariation.BeginPipelinePhase(); proposalResourcePhase = true; }
                    if (meteredTask is not null) { meteredTask.BeginPipelinePhase(); evaluationResourcePhase = true; }
                    foreach (PipelineProposal planned in plan)
                    {
                        bool canPropose = planned.Request is null || meteredVariation is null || meteredVariation.ReservePipelineProposal(planned.Lineage.Generation);
                        if (canPropose) meteredTask?.ReservePipelineAttempt(planned.EvaluationId, 1, _options.Cascade.Enabled);
                    }
                    int admitted = 0;
                    while (proposals.Count > 0 || admitted < plan.Count)
                    {
                        while (admitted < plan.Count && proposals.Count < workers + settings.ProposalQueueCapacity)
                        {
                            waveToken.ThrowIfCancellationRequested();
                            PipelineProposal planned = plan[admitted++];
                            if (planned.Request is not null)
                            {
                                planned.Response = RunPipelineProposalAsync(planned.Request, proposalSlots, proposalRate, waveToken);
                                allProposals.Add(planned.Response);
                            }
                            proposals.Enqueue(planned);
                        }
                        if (proposals.Count == 0) break;
                        PipelineProposal next = proposals.Dequeue();
                        WorkItem item = next.Request is null
                            ? await PrepareGenomeAsync(next.Seed, next.EvaluationId, next.Island, next.Lineage, waveToken).ConfigureAwait(false)
                            : await CompleteVariationAsync(next.Request, await next.Response!.ConfigureAwait(false), waveToken).ConfigureAwait(false);
                        item.IsSeed = next.Request is null;
                        batch.Add(item);
                        if (!item.RequiresEvaluation) continue;
                        activeEvaluations.RemoveAll(task => task.IsCompleted);
                        while (activeEvaluations.Count >= _options.MaxDegreeOfParallelism + settings.EvaluationQueueCapacity)
                        {
                            await Task.WhenAny(activeEvaluations).ConfigureAwait(false);
                            waveToken.ThrowIfCancellationRequested();
                            activeEvaluations.RemoveAll(task => task.IsCompleted);
                        }
                        item.AttemptCount++; item.ChargedAttempts++; _evaluationAttempts++;
                        Task evaluation = EvaluateWithSlotAsync(item, evaluatorSlots, waveToken);
                        evaluations.Add(evaluation); activeEvaluations.Add(evaluation);
                    }
                    await Task.WhenAll(evaluations).ConfigureAwait(false);
                    waveToken.ThrowIfCancellationRequested();
                    if (proposalResourcePhase) { proposalResourcePhase = false; meteredVariation!.EndPipelinePhase(); }
                    if (evaluationResourcePhase) { evaluationResourcePhase = false; meteredTask!.EndPipelinePhase(); }
                    foreach (WorkItem item in batch.Where(item => item.RequiresEvaluation))
                    {
                        if (item.CascadeRejectedStage.HasValue && !_options.Cascade.ChargeRejectedStagesToBudget)
                        { _evaluationAttempts--; item.ChargedAttempts--; }
                        item.RequiresEvaluation = IsRetryable(item.Result) && item.AttemptCount <= _options.MaxRetries;
                    }
                    // Retries are separate settled rounds; feedback cannot race proposal preparation or backend learning.
                    await EvaluatePipelineRetriesAsync(batch, evaluatorSlots, retryEvaluations, waveToken).ConfigureAwait(false);
                    waveToken.ThrowIfCancellationRequested();
                }
                catch (Exception exception)
                {
                    Exception? faultBehindCancellation = null;
                    try
                    {
                        try { waveCancellation.Cancel(); }
                        catch (Exception cancellationFailure) when (EvolutionExceptionPolicy.IsRecoverable(cancellationFailure))
                        {
                            // A caller's cancellation callback must not replace the original wave failure.
                            _pipelineStatistics.RecordCancellationFailure();
                        }
                    }
                    finally
                    {
                        try
                        {
                            _pipelineStatistics.RecordDrain(await DrainPipelineTasksAsync(allProposals.Concat(evaluations)).ConfigureAwait(false));
                            // Retry rounds drain their own tasks before rethrowing, so every task inspected here has settled.
                            if (exception is OperationCanceledException)
                                faultBehindCancellation = FindFaultBehindCancellation(allProposals.Concat(evaluations).Concat(retryEvaluations));
                        }
                        finally
                        {
                            // Fatal drain failures still require settled callbacks before restoring cursors.
                            _pipelineStatistics.WaveAborted(transaction.NextEvaluationId, transaction.Generation,
                                exception is OperationCanceledException && faultBehindCancellation is null);
                            RestoreBatchTransaction(transaction, batch);
                        }
                    }
                    // A real wave failure is preserved as-is, with sibling faults counted by the drain. A cancellation is
                    // not a failure, so when owned work faulted during it, that fault is the actual outcome of the wave.
                    if (faultBehindCancellation is not null) ExceptionDispatchInfo.Capture(faultBehindCancellation).Throw();
                    throw;
                }
                finally
                {
                    try { if (proposalResourcePhase) meteredVariation!.EndPipelinePhase(); }
                    finally { if (evaluationResourcePhase) meteredTask!.EndPipelinePhase(); }
                }
                if (batch.Count == 0) return StopReasonWithNothingInFlight();
                bool failedFast = await CommitBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
                _pipelineStatistics.WaveCommitted();
                if (_options.MigrationInterval > 0 && _islands.Length > 1)
                { _batchesSinceMigration++; await MigrateIfDueAsync(CancellationToken.None).ConfigureAwait(false); }
                UpdateEarlyStopping(batch.Count);
                CaptureSafeState(seeds, seedIndex);
                await SaveCheckpointAsync(force: false, CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (failedFast) return EvolutionStopReason.CandidateFailure;
                if (IsTargetReached()) return EvolutionStopReason.TargetReached;
                if (IsEarlyStopped()) return EvolutionStopReason.EarlyStopped;
            }
        }
        finally { _pipelineEvaluationRate = null; _pipelineStatistics.Stop(); }
    }

    private async Task<VariationResponse> RunPipelineProposalAsync(VariationRequest request, SemaphoreSlim slots,
        EvolutionPipelineRateGate rate, CancellationToken cancellationToken)
    {
        _pipelineStatistics!.Enqueue(0);
        try { await slots.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { _pipelineStatistics.CancelQueued(0); throw; }
        _pipelineStatistics.Claim(0);
        Stopwatch? timer = null;
        try
        {
            await rate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _pipelineStatistics.Started(0); timer = Stopwatch.StartNew();
            return await Task.Run(() => InvokeVariationAsync(request, cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _pipelineStatistics.Released(0, timer?.Elapsed.TotalSeconds ?? 0); slots.Release(); }
    }

    private List<PipelineProposal> PlanPipelineWave(TGenome[] seeds, ref int seedIndex, int limit,
        Dictionary<int, PipelineArchiveContext> snapshots)
    {
        var plan = new List<PipelineProposal>(limit);
        while (plan.Count < limit)
        {
            if (seedIndex < seeds.Length)
            {
                long id = AllocateProposalId(); int island = (int)(id % _islands.Length);
                var lineage = new EvolutionLineage(null, null, "seed", _refiner?.Id, 0, island, (ulong)id);
                plan.Add(new PipelineProposal(id, island, lineage, seeds[seedIndex++], null, null));
                _pipelineStatistics!.Record(EvolutionPipelineScheduleKind.ProposalSnapshot, id, 0, "seed");
            }
            else
            {
                if (_generation >= _options.MaxGenerations) break;
                VariationRequest? request = CreateVariationRequest(snapshots);
                if (request is null) break;
                plan.Add(new PipelineProposal(request.EvaluationId, request.Island, request.Lineage, default!, request, null));
                _pipelineStatistics!.Record(EvolutionPipelineScheduleKind.ProposalSnapshot, request.EvaluationId, request.Lineage.Generation, request.Context.ProposalIdentity!);
            }
        }
        return plan;
    }

    private static async Task<EvolutionPipelineDrainStatus> DrainPipelineTasksAsync(IEnumerable<Task> tasks)
    {
        Task completion = Task.WhenAll(tasks);
        try { await completion.ConfigureAwait(false); }
#pragma warning disable CA1031
        catch (Exception exception) when (EvolutionExceptionPolicy.IsRecoverable(exception))
        {
            // Await surfaces one failure, but another owned callback may have failed fatally.
            // Inspect the complete aggregate before treating a drained batch as recoverable.
            if (completion.Exception is AggregateException aggregate && !EvolutionExceptionPolicy.IsRecoverable(aggregate))
                throw aggregate;
            // Preserve the caller's original failure while making the drain outcome observable.
            // Diagnostics contain only bounded counts, never callback exception text or genomes.
            return completion.IsCanceled ? EvolutionPipelineDrainStatus.Canceled : EvolutionPipelineDrainStatus.Faulted;
        }
#pragma warning restore CA1031
        return EvolutionPipelineDrainStatus.Completed;
    }

    /// <summary>Finds owned-task faults that a cancellation abort would otherwise report as a clean cancellation.</summary>
    /// <param name="settledTasks">Proposal and evaluation tasks of the aborted wave; every one must already have settled.</param>
    /// <returns>
    /// <c>null</c> when every task completed or stopped by cancellation; the single fault when exactly one task failed
    /// otherwise; or an <see cref="AggregateException"/> of every distinct fault.
    /// </returns>
    /// <remarks>
    /// Cancellation is the only expected way for owned work to end once a wave is canceled. Proposal and evaluator
    /// callback failures are already converted into failed results at their boundaries, so a task that still faulted
    /// with anything else escaped that failure path. Rethrowing the cancellation would make the run report
    /// <see cref="EvolutionStopReason.Canceled"/> (or a time limit) with the fault reduced to a drain counter.
    /// </remarks>
    internal static Exception? FindFaultBehindCancellation(IEnumerable<Task> settledTasks)
    {
        var faults = new List<Exception>();
        foreach (Task task in settledTasks)
        {
            if (task.Exception is not AggregateException taskFailure) continue;
            foreach (Exception fault in taskFailure.InnerExceptions)
            {
                if (fault is OperationCanceledException || faults.Contains(fault)) continue;
                faults.Add(fault);
            }
        }
        return faults.Count switch
        {
            0 => null,
            1 => faults[0],
            _ => new AggregateException("Owned pipeline work faulted while the wave was being canceled.", faults)
        };
    }

    private async Task EvaluatePipelineRetriesAsync(List<WorkItem> batch, SemaphoreSlim slots, List<Task> started, CancellationToken cancellationToken)
    {
        var pending = batch.Where(item => item.RequiresEvaluation).OrderBy(item => item.EvaluationId).ToList();
        while (pending.Count > 0 && _evaluationAttempts < _options.MaxEvaluationAttempts)
        {
            // Select the entire logical retry round before any work starts. Re-inserting a retry after each worker-sized
            // chunk lets a small pool retry low IDs again before high IDs get their first retry, changing tight budgets.
            int count = (int)Math.Min(pending.Count, _options.MaxEvaluationAttempts - _evaluationAttempts);
            WorkItem[] round = pending.Take(count).ToArray(); pending.RemoveRange(0, count);
            foreach (WorkItem item in round) { item.AttemptCount++; item.ChargedAttempts++; _evaluationAttempts++; }
            TimeSpan delay = RetryDelayForAttempt(round.Max(item => item.AttemptCount));
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var tasks = new List<Task>(count);
            var active = new List<Task>();
            var meteredTask = _task as ResourceMeteredEvolutionTask<TGenome>;
            bool resourcePhase = false;
            try
            {
                if (meteredTask is not null)
                {
                    meteredTask.BeginPipelinePhase(); resourcePhase = true;
                    foreach (WorkItem item in round) meteredTask.ReservePipelineAttempt(item.EvaluationId, item.AttemptCount, _options.Cascade.Enabled);
                }
                foreach (WorkItem item in round)
                {
                    active.RemoveAll(task => task.IsCompleted);
                    while (active.Count >= _options.MaxDegreeOfParallelism + _options.Pipeline.EvaluationQueueCapacity)
                    {
                        await Task.WhenAny(active).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested(); active.RemoveAll(task => task.IsCompleted);
                    }
                    Task task = EvaluateWithSlotAsync(item, slots, cancellationToken);
                    tasks.Add(task); active.Add(task); started.Add(task);
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    var statistics = _pipelineStatistics ?? throw new InvalidOperationException("Pipeline diagnostics were not initialized.");
                    statistics.RecordDrain(await DrainPipelineTasksAsync(tasks).ConfigureAwait(false));
                }
                finally
                {
                    if (resourcePhase && meteredTask is not null) meteredTask.EndPipelinePhase();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            foreach (WorkItem item in round)
            {
                if (item.CascadeRejectedStage.HasValue && !_options.Cascade.ChargeRejectedStagesToBudget)
                { _evaluationAttempts--; item.ChargedAttempts--; }
                if (IsRetryable(item.Result) && item.AttemptCount <= _options.MaxRetries) pending.Add(item);
                else item.RequiresEvaluation = false;
            }
            pending = pending.OrderBy(item => item.EvaluationId).ToList();
        }
        // The already-reported terminal attempt remains evidence when there is no budget for another retry.
        foreach (WorkItem item in pending) item.RequiresEvaluation = false;
    }

    private sealed class PipelineProposal(long evaluationId, int island, EvolutionLineage lineage, TGenome seed,
        VariationRequest? request, Task<VariationResponse>? response)
    {
        public long EvaluationId { get; } = evaluationId;
        public int Island { get; } = island;
        public EvolutionLineage Lineage { get; } = lineage;
        public TGenome Seed { get; } = seed;
        public VariationRequest? Request { get; } = request;
        public Task<VariationResponse>? Response { get; set; } = response;
    }
}
