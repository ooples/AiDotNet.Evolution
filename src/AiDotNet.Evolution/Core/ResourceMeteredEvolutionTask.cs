using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Charges actual evaluator CostUnits per attempt/stage under explicit reserved maxima.</summary>
/// <remarks>
/// This adapter owns evaluation charges only. Meter proposal/refinement/setup/model work separately with
/// EvolutionResourceWork, without double-counting costs already included in the evaluator's receipt.
/// Cascade screens remain charged even if the engine refunds its evaluation-attempt counter. Cache hits never invoke
/// this adapter. Restore the ledger with the matching engine checkpoint; restarting an engine from an older checkpoint
/// cannot safely reuse already-spent operation identities. Producers must enforce declared maxima and isolation.
/// </remarks>
public sealed class ResourceMeteredEvolutionTask<TGenome> : ICascadeEvolutionTask<TGenome>
{
    private readonly IEvolutionTask<TGenome> _inner;
    private readonly EvolutionResourceLedger _ledger;
    private readonly decimal[] _maxima;

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
        VersionHash = EvolutionHash.Combine(new[] { "resource-metered-task-v1", inner.Id, inner.VersionHash, inner.EvaluatorVersionHash }
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
        string operation = "evaluation/" + context.EvaluationId.ToString(CultureInfo.InvariantCulture) + "/attempt/" +
            context.AttemptCount.ToString(CultureInfo.InvariantCulture) + "/stage/" + stage.ToString(CultureInfo.InvariantCulture);
        try
        {
            return await EvolutionResourceWork.RunAsync(_ledger, operation,
                stage >= 0 && stage < StageCount - 1 ? EvolutionResourceStage.Screening : EvolutionResourceStage.Evaluation,
                reserved, reserved, async token =>
                {
                    EvolutionTaskResult result = stage >= 0 && _inner is ICascadeEvolutionTask<TGenome> cascade
                        ? await cascade.EvaluateStageAsync(stage, candidate, context, token).ConfigureAwait(false)
                        : await _inner.EvaluateAsync(candidate, context, token).ConfigureAwait(false);
                    if (result is null) throw new InvalidOperationException("The evaluator returned no receipt.");
                    var actual = EvolutionResources.Of("cost_units", checked((decimal)result.CostUnits));
                    EvolutionResourceOutcome outcome = result.Status switch
                    {
                        EvolutionEvaluationStatus.Completed => EvolutionResourceOutcome.Completed,
                        EvolutionEvaluationStatus.Rejected or EvolutionEvaluationStatus.Skipped or EvolutionEvaluationStatus.Duplicate => EvolutionResourceOutcome.Rejected,
                        EvolutionEvaluationStatus.Canceled => EvolutionResourceOutcome.Canceled,
                        _ => EvolutionResourceOutcome.Failed
                    };
                    return new EvolutionResourceResult<EvolutionTaskResult>(result, actual, outcome);
                }, context.AttemptCount, cancellationToken).ConfigureAwait(false);
        }
        catch (EvolutionResourceBudgetException)
        {
            return new EvolutionTaskResult(EvolutionEvaluationStatus.Skipped,
                diagnostics: new[] { new EvolutionDiagnostic("resource_budget_reached", "Evaluation was not dispatched because its resource reservation was denied.") });
        }
    }
}
