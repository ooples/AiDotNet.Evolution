using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Charges actual evaluator CostUnits per attempt/stage under explicit reserved maxima.</summary>
/// <remarks>
/// This adapter owns evaluation charges only. Meter proposal/refinement/setup/model work separately with
/// EvolutionResourceWork, without double-counting costs already included in the evaluator's receipt.
/// Cascade screens remain charged even if the engine refunds its evaluation-attempt counter. Cache hits never invoke
/// this adapter. Restore the ledger with the matching engine checkpoint; restarting an engine from an older checkpoint
/// cannot safely reuse already-spent operation identities. Producers must enforce declared maxima and isolation.
/// Missing/unrepresentable receipts retain conservative engine-visible costs and cannot produce a successful candidate.
/// A producer maximum violation retains its actual charge and fails that evaluation, not only subsequent admission.
/// </remarks>
public sealed class ResourceMeteredEvolutionTask<TGenome> : ICascadeEvolutionTask<TGenome>
{
    private readonly IEvolutionTask<TGenome> _inner;
    private readonly EvolutionResourceLedger _ledger;
    private readonly decimal[] _maxima;
    private EvolutionPipelineResourcePhase? _pipelinePhase;

    internal void BeginPipelinePhase()
    {
        if (_pipelinePhase is not null) throw new InvalidOperationException("A pipeline evaluation resource phase is already active.");
        _pipelinePhase = new EvolutionPipelineResourcePhase(_ledger);
    }

    internal void ReservePipelineAttempt(long evaluationId, int attempt, bool cascade)
    {
        EvolutionPipelineResourcePhase phase = _pipelinePhase ?? throw new InvalidOperationException("Begin a pipeline resource phase first.");
        if (!cascade) phase.Reserve(Operation(evaluationId, attempt, -1), EvolutionResourceStage.Evaluation, EvolutionResources.Of("cost_units", _maxima.Sum()), attempt);
        else
            for (int stage = 0; stage < _maxima.Length; stage++)
                phase.Reserve(Operation(evaluationId, attempt, stage), stage < _maxima.Length - 1 ? EvolutionResourceStage.Screening : EvolutionResourceStage.Evaluation,
                    EvolutionResources.Of("cost_units", _maxima[stage]), attempt);
    }

    internal void EndPipelinePhase()
    {
        EvolutionPipelineResourcePhase? phase = _pipelinePhase; _pipelinePhase = null; phase?.Dispose();
    }

    /// <summary>Wraps a task with one maximum per cascade stage, or one maximum for an ordinary task.</summary>
    public ResourceMeteredEvolutionTask(IEvolutionTask<TGenome> inner, EvolutionResourceLedger ledger,
        IEnumerable<decimal> maximumStageCostUnits)
    {
        Guard.NotNull(inner); Guard.NotNull(ledger); Guard.NotNull(maximumStageCostUnits);
        _inner = inner; _ledger = ledger;
        _maxima = maximumStageCostUnits.Take(257).ToArray();
        int stages = (inner as ICascadeEvolutionTask<TGenome>)?.StageCount ?? 1;
        if (stages < 1 || stages > 256 || _maxima.Length != stages ||
            _maxima.Any(value => value < 0 || value > EvolutionResources.MaximumAmount) ||
            _maxima.Sum() > EvolutionResources.MaximumAmount)
            throw new ArgumentException("Supply a bounded maximum for every evaluation stage.", nameof(maximumStageCostUnits));
        if (!ledger.Limits.Amounts.ContainsKey("cost_units")) throw new ArgumentException("The ledger must declare cost_units.", nameof(ledger));
        Guard.NotNullOrWhiteSpace(inner.Id); Guard.NotNullOrWhiteSpace(inner.VersionHash); Guard.NotNullOrWhiteSpace(inner.EvaluatorVersionHash);
        VersionHash = EvolutionHash.Combine(new[] { "resource-metered-task-v2-fail-closed-costs", inner.Id, inner.VersionHash, inner.EvaluatorVersionHash }
            .Concat(_maxima.Select(value => value.ToString(CultureInfo.InvariantCulture))));
    }
    /// <inheritdoc/>
    public string Id => "resource-metered:" + _inner.Id;
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <inheritdoc/>
    public string EvaluatorVersionHash => VersionHash;
    /// <inheritdoc/>
    public int StageCount => _maxima.Length;
    /// <inheritdoc/>
    public ValueTask<EvolutionCanonicalGenome<TGenome>> CanonicalizeAsync(TGenome genome, CancellationToken cancellationToken = default) =>
        _inner.CanonicalizeAsync(genome, cancellationToken);
    /// <inheritdoc/>
    public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TGenome> candidate, EvolutionEvaluationContext context,
        CancellationToken cancellationToken = default) => Invoke(candidate, context, -1, cancellationToken);
    /// <inheritdoc/>
    public ValueTask<EvolutionTaskResult> EvaluateStageAsync(int stage, EvolutionCandidate<TGenome> candidate, EvolutionEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        if (stage < 0 || stage >= StageCount) throw new ArgumentOutOfRangeException(nameof(stage));
        return Invoke(candidate, context, stage, cancellationToken);
    }

    private async ValueTask<EvolutionTaskResult> Invoke(EvolutionCandidate<TGenome> candidate, EvolutionEvaluationContext context,
        int stage, CancellationToken cancellationToken)
    {
        Guard.NotNull(candidate); Guard.NotNull(context);
        decimal maximum = stage < 0 ? _maxima.Sum() : _maxima[stage];
        EvolutionResources reserved = EvolutionResources.Of("cost_units", maximum);
        string operation = Operation(context.EvaluationId, context.AttemptCount, stage);
        cancellationToken.ThrowIfCancellationRequested();
        using EvolutionResourceReservation? reservation = _pipelinePhase is not null ? _pipelinePhase.Take(operation) : _ledger.TryReserve(operation,
            stage >= 0 && stage < StageCount - 1 ? EvolutionResourceStage.Screening : EvolutionResourceStage.Evaluation,
            reserved, reserved, context.AttemptCount);
        if (reservation is null)
            return new EvolutionTaskResult(EvolutionEvaluationStatus.Skipped,
                diagnostics: new[] { new EvolutionDiagnostic("resource_budget_reached", "Evaluation was not dispatched because its resource reservation was denied.") });
        EvolutionTaskResult result;
        try
        {
            result = (stage >= 0 && _inner is ICascadeEvolutionTask<TGenome> cascade
                ? await cascade.EvaluateStageAsync(stage, candidate, context, cancellationToken).ConfigureAwait(false)
                : await _inner.EvaluateAsync(candidate, context, cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidOperationException("The evaluator returned no receipt.");
        }
        catch (Exception exception) when (EvolutionExceptionPolicy.IsRecoverable(exception))
        {
            // This boundary is after dispatch, including a nested producer's budget exception.
            reservation.Dispose();
            return new EvolutionTaskResult(exception is OperationCanceledException ? EvolutionEvaluationStatus.Canceled : EvolutionEvaluationStatus.Failed,
                costUnits: (double)maximum, diagnostics: new[] { new EvolutionDiagnostic("resource_cost_unknown",
                    "Dispatched evaluation returned no usable receipt; its reserved maximum was charged conservatively.") });
        }
        if (result.CostUnits > (double)EvolutionResources.MaximumAmount || (result.CostUnits > 0 && (decimal)result.CostUnits == 0))
        {
            reservation.Dispose();
            return InvalidReceipt(result, (double)maximum, "resource_cost_unrepresentable",
                "Reported cost " + result.CostUnits.ToString("G17", CultureInfo.InvariantCulture) + " cannot be represented by the ledger; charged reserved maximum as unknown.");
        }
        decimal actualCost = (decimal)result.CostUnits;
        if (actualCost > maximum)
        {
            reservation.Complete(EvolutionResources.Of("cost_units", actualCost), EvolutionResourceOutcome.Failed);
            return InvalidReceipt(result, result.CostUnits, "resource_maximum_exceeded", "The evaluator exceeded its declared maximum; actual cost is retained but promotion is refused.");
        }
        EvolutionResourceOutcome outcome = result.Status switch
        {
            EvolutionEvaluationStatus.Completed => EvolutionResourceOutcome.Completed,
            EvolutionEvaluationStatus.Rejected or EvolutionEvaluationStatus.Skipped or EvolutionEvaluationStatus.Duplicate => EvolutionResourceOutcome.Rejected,
            EvolutionEvaluationStatus.Canceled => EvolutionResourceOutcome.Canceled,
            _ => EvolutionResourceOutcome.Failed
        };
        reservation.Complete(EvolutionResources.Of("cost_units", actualCost), outcome);
        return result;
    }

    private static EvolutionTaskResult InvalidReceipt(EvolutionTaskResult result, double chargedCost, string code, string message)
    {
        var failed = new EvolutionTaskResult(EvolutionEvaluationStatus.Failed, result.Quality, result.Direction, result.Descriptors, result.Objectives,
            result.ConstraintViolations, chargedCost,
            new[] { new EvolutionDiagnostic(code, message) }.Concat(result.Diagnostics).Take(EvolutionTaskResult.MaximumDiagnostics), result.Metrics, result.Artifacts);
        return result.MeasurementOrigin is null ? failed : failed.WithMeasurementOrigin(result.MeasurementOrigin);
    }

    private static string Operation(long evaluationId, int attempt, int stage) => "evaluation/" + evaluationId.ToString(CultureInfo.InvariantCulture) + "/attempt/" +
        attempt.ToString(CultureInfo.InvariantCulture) + "/stage/" + stage.ToString(CultureInfo.InvariantCulture);
}
