using System.Diagnostics;

namespace AiDotNet.Evolution;

/// <summary>Identifies a deterministic snapshot allocation or an actual committed outcome.</summary>
public enum EvolutionPipelineScheduleKind
{
    /// <summary>A proposal identity and its parent/archive snapshot were allocated.</summary>
    ProposalSnapshot,
    /// <summary>An outcome and its operator feedback were committed.</summary>
    Commit,
    /// <summary>An admitted wave was aborted after its owned callbacks drained.</summary>
    WaveAborted
}

/// <summary>One immutable logical schedule record; callback timings are deliberately excluded.</summary>
public sealed class EvolutionPipelineScheduleEntry
{
    internal EvolutionPipelineScheduleEntry(EvolutionPipelineScheduleKind kind, long evaluationId, long generation, string identity)
    { Kind = kind; EvaluationId = evaluationId; Generation = generation; Identity = identity; }
    /// <summary>Gets the logical event.</summary>
    public EvolutionPipelineScheduleKind Kind { get; }
    /// <summary>Gets the allocated evaluation identifier.</summary>
    public long EvaluationId { get; }
    /// <summary>Gets the proposal generation, zero for a seed, or the pre-wave cursor for an abort.</summary>
    public long Generation { get; }
    /// <summary>Gets the snapshot fingerprint, committed genome identity, or fixed canceled/faulted abort classification.</summary>
    public string Identity { get; }
}

/// <summary>Immutable pipeline queue/utilization diagnostics, separate from deterministic search state.</summary>
/// <remarks>Busy seconds sum callback durations, not measured CPU consumption. Slot utilization also includes waits
/// inside a callback. A complete logical schedule still needs the original external responses to reproduce proposals
/// and measurements. Opportunistic commit order is recorded, not represented as timing-independent.</remarks>
public sealed class EvolutionPipelineReport
{
    internal EvolutionPipelineReport(EvolutionExecutionMode mode, int proposalWorkers, int evaluationWorkers,
        long proposalCalls, long evaluationCalls, int proposalQueuePeak, int evaluationQueuePeak, int proposalRunningPeak,
        int evaluationRunningPeak, double proposalBusySeconds, double evaluationBusySeconds, double elapsedSeconds,
        long completedWaves, long dropped, EvolutionPipelineScheduleEntry[] schedule,
        string runId, string compatibilityHash, long firstEvaluationId, EvolutionPipelineOptions options, long abortedWaves)
    {
        ExecutionMode = mode; ProposalWorkers = proposalWorkers; EvaluationWorkers = evaluationWorkers;
        ProposalCalls = proposalCalls; EvaluationCalls = evaluationCalls; ProposalQueuePeak = proposalQueuePeak;
        EvaluationQueuePeak = evaluationQueuePeak; ProposalRunningPeak = proposalRunningPeak; EvaluationRunningPeak = evaluationRunningPeak;
        ProposalBusySeconds = proposalBusySeconds; EvaluationBusySeconds = evaluationBusySeconds; ElapsedSeconds = elapsedSeconds;
        CompletedWaves = completedWaves; DroppedScheduleRecords = dropped; Schedule = Array.AsReadOnly(schedule);
        RunId = runId; CompatibilityHash = compatibilityHash; FirstEvaluationId = firstEvaluationId;
        WaveSize = options.WaveSize; ProposalQueueCapacity = options.ProposalQueueCapacity; EvaluationQueueCapacity = options.EvaluationQueueCapacity;
        AbortedWaves = abortedWaves;
    }
    /// <summary>Gets the caller's run identity.</summary>
    public string RunId { get; }
    /// <summary>Gets the exact engine/component/scheduling compatibility identity.</summary>
    public string CompatibilityHash { get; }
    /// <summary>Gets the first allocation cursor for this invocation; resumed reports do not contain earlier invocation records.</summary>
    public long FirstEvaluationId { get; }
    /// <summary>Gets the separate planning-buffer bound.</summary>
    public int WaveSize { get; }
    /// <summary>Gets the waiting proposal queue bound, excluding active slots and the planning buffer.</summary>
    public int ProposalQueueCapacity { get; }
    /// <summary>Gets the waiting evaluator queue bound, excluding active slots.</summary>
    public int EvaluationQueueCapacity { get; }
    /// <summary>Gets deterministic identifier ordering or explicitly opportunistic completion ordering.</summary>
    public EvolutionExecutionMode ExecutionMode { get; }
    /// <summary>Gets the actual proposal-worker limit after the operator capability check.</summary>
    public int ProposalWorkers { get; }
    /// <summary>Gets the evaluator-worker limit.</summary>
    public int EvaluationWorkers { get; }
    /// <summary>Gets operator-entry callbacks, including budget denials/failures; not necessarily external model calls.</summary>
    public long ProposalCalls { get; }
    /// <summary>Gets evaluator-entry attempts, including adapter denials/retries; not necessarily physical backend calls.</summary>
    public long EvaluationCalls { get; }
    /// <summary>Gets peak proposal callbacks waiting for a worker slot.</summary>
    public int ProposalQueuePeak { get; }
    /// <summary>Gets peak evaluator attempts waiting for a worker slot.</summary>
    public int EvaluationQueuePeak { get; }
    /// <summary>Gets peak claimed proposal slots, including rate-limit waits.</summary>
    public int ProposalRunningPeak { get; }
    /// <summary>Gets peak claimed evaluator slots, including rate-limit waits.</summary>
    public int EvaluationRunningPeak { get; }
    /// <summary>Gets summed proposal callback duration; not CPU time or a resource receipt.</summary>
    public double ProposalBusySeconds { get; }
    /// <summary>Gets summed evaluator callback duration; not CPU time or a resource receipt.</summary>
    public double EvaluationBusySeconds { get; }
    /// <summary>Gets elapsed pipeline time, excluded from deterministic state and learning.</summary>
    public double ElapsedSeconds { get; }
    /// <summary>Gets waves with all outcomes committed.</summary>
    public long CompletedWaves { get; }
    /// <summary>Gets admitted waves aborted by cancellation or a fault; their actual resource charges were not undone.</summary>
    public long AbortedWaves { get; }
    /// <summary>Gets logical records omitted after reaching the configured bound.</summary>
    public long DroppedScheduleRecords { get; }
    /// <summary>Gets whether no logical records were dropped; external response evidence is still required for replay.</summary>
    public bool IsScheduleComplete => DroppedScheduleRecords == 0;
    /// <summary>Gets retained immutable logical events in allocation/commit order.</summary>
    public IReadOnlyList<EvolutionPipelineScheduleEntry> Schedule { get; }
}

internal sealed class EvolutionPipelineStatistics(EvolutionExecutionMode mode, int proposalWorkers, int evaluationWorkers, int recordLimit,
    string runId, string compatibilityHash, long firstEvaluationId, EvolutionPipelineOptions options)
{
    private readonly object _gate = new();
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private readonly List<EvolutionPipelineScheduleEntry> _schedule = new();
    private readonly int[] _queued = new int[2], _active = new int[2], _queuePeak = new int[2], _activePeak = new int[2];
    private readonly long[] _calls = new long[2];
    private readonly double[] _seconds = new double[2];
    private long _waves, _dropped, _aborted;
    public void Enqueue(int stage) { lock (_gate) { _queued[stage]++; _queuePeak[stage] = Math.Max(_queuePeak[stage], Math.Max(0, _queued[stage] - Math.Max(0, (stage == 0 ? proposalWorkers : evaluationWorkers) - _active[stage]))); } }
    public void Claim(int stage) { lock (_gate) { _queued[stage]--; _active[stage]++; _activePeak[stage] = Math.Max(_activePeak[stage], _active[stage]); } }
    public void CancelQueued(int stage) { lock (_gate) _queued[stage]--; }
    public void Started(int stage) { lock (_gate) _calls[stage]++; }
    public void Released(int stage, double seconds) { lock (_gate) { _active[stage]--; _seconds[stage] += seconds; } }
    public void WaveCommitted() { lock (_gate) _waves++; }
    public void WaveAborted(long firstId, long generation, bool canceled)
    {
        lock (_gate)
        {
            _aborted++;
            Record(EvolutionPipelineScheduleKind.WaveAborted, firstId, generation, canceled ? "canceled" : "faulted");
        }
    }
    public void Record(EvolutionPipelineScheduleKind kind, long id, long generation, string identity)
    {
        lock (_gate)
        {
            if (_schedule.Count == recordLimit) _dropped++;
            else _schedule.Add(new EvolutionPipelineScheduleEntry(kind, id, generation, identity));
        }
    }
    public void Stop() { lock (_gate) _timer.Stop(); }
    public EvolutionPipelineReport Snapshot()
    {
        lock (_gate) return new(mode, proposalWorkers, evaluationWorkers, _calls[0], _calls[1], _queuePeak[0], _queuePeak[1],
            _activePeak[0], _activePeak[1], _seconds[0], _seconds[1], _timer.Elapsed.TotalSeconds, _waves, _dropped, _schedule.ToArray(),
            runId, compatibilityHash, firstEvaluationId, options, _aborted);
    }
}

internal sealed class EvolutionPipelineRateGate(TimeSpan interval) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _timer = Stopwatch.StartNew();
    private TimeSpan _last;
    private bool _started;
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (interval == TimeSpan.Zero) { cancellationToken.ThrowIfCancellationRequested(); return; }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_started)
            {
                TimeSpan remaining = interval - (_timer.Elapsed - _last);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested(); _last = _timer.Elapsed; _started = true;
        }
        finally { _gate.Release(); }
    }
    public void Dispose() => _gate.Dispose();
}
