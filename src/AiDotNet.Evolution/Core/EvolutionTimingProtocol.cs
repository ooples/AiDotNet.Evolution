using System.Diagnostics;

namespace AiDotNet.Evolution;

/// <summary>Sequential, explicit warmup followed by one fresh elapsed-time observation.</summary>
/// <remarks>Use one instance per declared timing protocol with a fresh replicate runner. The callback must
/// validate correctness and own process isolation/reset semantics. Cancellation is cooperative, not a process
/// kill guarantee. CostUnits counts callback invocations, not milliseconds, CPU time or currency.</remarks>
public sealed class EvolutionTimingProtocol
{
    /// <summary>Creates a protocol with 0..64 warmups and a declared maximum elapsed time per callback.</summary>
    public EvolutionTimingProtocol(int warmupCalls, double maximumMillisecondsPerCall)
    {
        if (warmupCalls is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(warmupCalls));
        if (!EvolutionDescriptorDefinition.IsFinite(maximumMillisecondsPerCall) || maximumMillisecondsPerCall <= 0 || maximumMillisecondsPerCall > 60000)
            throw new ArgumentOutOfRangeException(nameof(maximumMillisecondsPerCall));
        WarmupCalls = warmupCalls; MaximumMillisecondsPerCall = maximumMillisecondsPerCall;
        VersionHash = EvolutionHash.Combine(new[] { "explicit-warmup-timing-v1", warmupCalls.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EvolutionHash.EncodeDouble(maximumMillisecondsPerCall) });
    }
    /// <summary>Gets discarded-from-fitness but fully charged warmup invocations.</summary>
    public int WarmupCalls { get; }
    /// <summary>Gets the declared elapsed-time support limit; overruns invalidate the observation rather than clipping it.</summary>
    public double MaximumMillisecondsPerCall { get; }
    /// <summary>Gets the maximum per-observation call cost for use in the replication plan.</summary>
    public int MaximumCostPerSample => WarmupCalls + 1;
    /// <summary>Gets the timing semantics fingerprint to combine with callback/environment versions.</summary>
    public string VersionHash { get; }

    /// <summary>Measures trusted bounded work directly, without an evaluation cache or descriptor remeasurement.</summary>
    public async ValueTask<EvolutionTaskResult> MeasureAsync(Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(work); cancellationToken.ThrowIfCancellationRequested();
        int calls = 0;
        double warmup = 0, measurement = 0;
        EvolutionEvaluationStatus status = EvolutionEvaluationStatus.Completed;
        for (int index = 0; index <= WarmupCalls; index++)
        {
            if (cancellationToken.IsCancellationRequested) { status = EvolutionEvaluationStatus.Canceled; break; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(MaximumMillisecondsPerCall));
            var clock = Stopwatch.StartNew();
            calls++;
            try { await work(deadline.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                status = error is OperationCanceledException
                ? cancellationToken.IsCancellationRequested ? EvolutionEvaluationStatus.Canceled
                    : deadline.IsCancellationRequested ? EvolutionEvaluationStatus.TimedOut : EvolutionEvaluationStatus.Canceled
                : EvolutionEvaluationStatus.Failed;
            }
            clock.Stop();
            if (index < WarmupCalls) warmup += clock.Elapsed.TotalMilliseconds;
            else measurement = clock.Elapsed.TotalMilliseconds;
            if (status == EvolutionEvaluationStatus.Completed && cancellationToken.IsCancellationRequested)
                status = EvolutionEvaluationStatus.Canceled;
            else if (status == EvolutionEvaluationStatus.Completed && (deadline.IsCancellationRequested || clock.Elapsed.TotalMilliseconds > MaximumMillisecondsPerCall))
                status = EvolutionEvaluationStatus.TimedOut;
            if (status != EvolutionEvaluationStatus.Completed) break;
        }
        return new EvolutionTaskResult(status, status == EvolutionEvaluationStatus.Completed ? measurement : null,
            EvolutionOptimizationDirection.Minimize, costUnits: calls,
            metrics: new Dictionary<string, double>
            {
                ["warmup_calls"] = Math.Min(calls, WarmupCalls),
                ["warmup_elapsed_ms"] = warmup,
                ["measurement_elapsed_ms"] = measurement,
                ["callback_calls"] = calls
            });
    }
}
