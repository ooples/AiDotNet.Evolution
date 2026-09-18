using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Runs fresh bounded scalar measurements under an independent resource ledger, without an evaluation cache.</summary>
/// <typeparam name="TGenome">The immutable candidate representation.</typeparam>
/// <remarks>
/// The supplied evaluator must execute independent fresh measurements and enforce its resource/time/isolation bounds.
/// It must not hide caching or meter the same cost a second time. Pseudorandom stream separation does not prove physical
/// independence. Confirmation uses separate identities and accounting; this API never inserts evidence into an archive
/// or sends confirmation feedback to a proposer. Changing batchId requests new work, not a statistical multiplicity fix.
/// Supplied measurement origins must declare one newly measured observation whose id equals the supplied
/// EvolutionReplicateContext.SampleIdentity; all origins in a batch must share one scope. Reuse and aggregates
/// invalidate the batch after retaining the receipt and its current cost, never increasing statistical precision.
/// </remarks>
public sealed class EvolutionReplicateRunner<TGenome>
{
    private readonly EvolutionReplicationPlan _plan;
    private readonly EvolutionResourceLedger _ledger;
    private readonly Func<TGenome, EvolutionReplicateContext, CancellationToken, ValueTask<EvolutionTaskResult>> _evaluate;

    /// <summary>Creates a runner with a versioned evaluator, immutable plan and shared cost_units ledger.</summary>
    /// <param name="evaluatorVersionHash">Fingerprint of evaluator, data, fidelity and environment semantics.</param>
    /// <param name="plan">Predeclared bounds, sample count and precision policy.</param>
    /// <param name="ledger">Independent actual resource accounting; must declare cost_units.</param>
    /// <param name="evaluate">A fresh-measurement callback returning actual same-unit cost even on rejection/failure.</param>
    public EvolutionReplicateRunner(string evaluatorVersionHash, EvolutionReplicationPlan plan, EvolutionResourceLedger ledger,
        Func<TGenome, EvolutionReplicateContext, CancellationToken, ValueTask<EvolutionTaskResult>> evaluate)
    {
        Guard.NotNullOrWhiteSpace(evaluatorVersionHash); Guard.NotNull(plan); Guard.NotNull(ledger); Guard.NotNull(evaluate);
        if (!ledger.Limits.Amounts.ContainsKey("cost_units")) throw new ArgumentException("The ledger must declare cost_units.", nameof(ledger));
        _plan = plan; _ledger = ledger; _evaluate = evaluate;
        VersionHash = EvolutionHash.Combine(new[] { "fresh-replicate-runner-v2", evaluatorVersionHash, plan.VersionHash });
    }
    /// <summary>Gets the evaluator and policy semantic identity.</summary>
    public string VersionHash { get; }

    /// <summary>Executes a new batch sequentially; reusing an already charged sample identity is rejected before work.</summary>
    /// <remarks>Pre-cancellation throws without dispatch. Cancellation after dispatch returns incomplete evidence and
    /// retains conservative charges for in-flight work. A batch never selects a winner or authorizes deployment.</remarks>
    public async ValueTask<EvolutionReplicationReport> RunAsync(EvolutionCanonicalGenome<TGenome> candidate,
        EvolutionEvaluationContext context, string batchId, EvolutionReplicationPurpose purpose = EvolutionReplicationPurpose.Search,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(candidate); Guard.NotNull(context); Guard.NotNullOrWhiteSpace(batchId);
        if (batchId.Length > 128 || batchId.Any(char.IsControl)) throw new ArgumentException("Batch identity must be bounded and printable.", nameof(batchId));
        if (!Enum.IsDefined(typeof(EvolutionReplicationPurpose), purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
        cancellationToken.ThrowIfCancellationRequested();
        string identity = IdentifyBatch(VersionHash, candidate.Id, context, batchId, purpose);
        var samples = new List<EvolutionReplicateMeasurement>();
        string? originScope = null;
        var moments = new EvolutionReplicationMoments();
        var maximum = EvolutionResources.Of("cost_units", _plan.MaximumCostPerSample);
        EvolutionReplicationReport Report(EvolutionReplicationStopReason reason) => new(identity, _plan, reason, samples, moments.Mean, moments.DeviationScale, moments.ScaledSquares);
        for (int index = 0; index < _plan.MaximumSamples; index++)
        {
            if (cancellationToken.IsCancellationRequested) return Report(EvolutionReplicationStopReason.Canceled);
            var sampleContext = new EvolutionReplicateContext(identity, index, purpose, context);
            using EvolutionResourceReservation? reservation = _ledger.TryReserve("replicate/" + sampleContext.SampleIdentity,
                purpose == EvolutionReplicationPurpose.Confirmation ? EvolutionResourceStage.Confirmation : EvolutionResourceStage.Evaluation,
                maximum, maximum, context.AttemptCount);
            if (reservation is null) return Report(EvolutionReplicationStopReason.BudgetExhausted);
            EvolutionTaskResult result;
            try
            {
                result = await _evaluate(candidate.Genome, sampleContext, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("No measurement receipt returned.");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                bool canceled = exception is OperationCanceledException;
                reservation.Dispose(); // Settle unknown work before exposing its report.
                samples.Add(new(sampleContext, canceled ? EvolutionEvaluationStatus.Canceled : EvolutionEvaluationStatus.Failed,
                    null, _plan.MaximumCostPerSample, true));
                return Report(canceled ? EvolutionReplicationStopReason.Canceled : EvolutionReplicationStopReason.UnknownCost);
            }
            if (result.CostUnits > (double)EvolutionResources.MaximumAmount ||
                (result.CostUnits > 0 && (decimal)result.CostUnits == 0))
            {
                reservation.Dispose();
                samples.Add(new(sampleContext, result.Status, result.Quality, _plan.MaximumCostPerSample, true, result.CostUnits, result.MeasurementOrigin));
                return Report(EvolutionReplicationStopReason.UnknownCost);
            }
            decimal actual = (decimal)result.CostUnits;
            EvolutionMeasurementOrigin? origin = result.MeasurementOrigin;
            bool validOrigin = origin is null || (origin.Kind == EvolutionMeasurementOriginKind.Measured &&
                origin.SampleCount == 1 && origin.SampleIds[0] == sampleContext.SampleIdentity &&
                (originScope is null || originScope == origin.ScopeKey));
            if (origin is not null) originScope = origin.ScopeKey;
            bool valid = result.Status == EvolutionEvaluationStatus.Completed && result.Quality.HasValue &&
                result.Direction == _plan.Direction && !result.ConstraintViolations.Any(value => value > 0) &&
                result.Quality.Value >= _plan.MinimumQuality && result.Quality.Value <= _plan.MaximumQuality && validOrigin;
            reservation.Complete(EvolutionResources.Of("cost_units", actual),
                valid && actual <= _plan.MaximumCostPerSample ? EvolutionResourceOutcome.Completed : EvolutionResourceOutcome.Failed);
            samples.Add(new(sampleContext, result.Status, result.Quality, actual, false, result.CostUnits, origin));
            if (actual > _plan.MaximumCostPerSample) return Report(EvolutionReplicationStopReason.MaximumCostExceeded);
            if (!valid) return Report(EvolutionReplicationStopReason.InvalidMeasurement);
            moments.Add(result.Quality!.Value);
            if (_plan.NormalizedWidthTarget > 0 && samples.Count >= _plan.MinimumSamples)
            {
                _plan.Bounds(moments.Mean, samples.Count, out double lower, out double upper);
                if ((upper - lower) / _plan.Span <= _plan.NormalizedWidthTarget)
                    return Report(EvolutionReplicationStopReason.PrecisionReached);
            }
        }
        return Report(EvolutionReplicationStopReason.Completed);
    }

    internal static string IdentifyBatch(string version, string genomeId, EvolutionEvaluationContext context, string batchId, EvolutionReplicationPurpose purpose) =>
        EvolutionHash.Combine(new[] { version, genomeId, batchId, purpose.ToString(), context.EvaluationId.ToString(CultureInfo.InvariantCulture),
            context.RootSeed.ToString(CultureInfo.InvariantCulture), context.SeedStream.ToString(CultureInfo.InvariantCulture), context.AttemptCount.ToString(CultureInfo.InvariantCulture) });
}

// Shared by actual measurement and checkpoint evidence reconstruction; never update from rejected samples.
internal sealed class EvolutionReplicationMoments
{
    internal int Count { get; private set; }
    internal double Mean { get; private set; }
    internal double DeviationScale { get; private set; }
    internal double ScaledSquares { get; private set; }
    internal void Add(double quality)
    {
        Count++;
        if (Count == 1) { Mean = quality; return; }
        // Scaled Welford update: wide support bounds must not erase small observed differences.
        double delta = quality - Mean, magnitude = Math.Abs(delta), weight = (Count - 1d) / Count;
        if (magnitude > DeviationScale)
        {
            double ratio = DeviationScale / magnitude;
            ScaledSquares = ScaledSquares * ratio * ratio + weight; DeviationScale = magnitude;
        }
        else if (DeviationScale > 0)
        {
            double ratio = magnitude / DeviationScale; ScaledSquares += ratio * ratio * weight;
        }
        Mean += delta / Count;
    }
}
