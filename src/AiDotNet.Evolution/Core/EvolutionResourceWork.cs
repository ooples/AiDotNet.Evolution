namespace AiDotNet.Evolution;

/// <summary>A stage result paired with an actual resource receipt, including failed or rejected work.</summary>
public sealed class EvolutionResourceResult<T>
{
    /// <summary>Creates a value and its actual receipt. Unknown consumption is reserved for abandoned work.</summary>
    public EvolutionResourceResult(T value, EvolutionResources actual, EvolutionResourceOutcome outcome = EvolutionResourceOutcome.Completed)
    {
        Guard.NotNull(actual);
        if (!Enum.IsDefined(typeof(EvolutionResourceOutcome), outcome) || outcome == EvolutionResourceOutcome.Unknown)
            throw new ArgumentOutOfRangeException(nameof(outcome));
        Value = value; Actual = actual; Outcome = outcome;
    }
    /// <summary>Gets the consumer's stage result.</summary>
    public T Value { get; }
    /// <summary>Gets actual measured consumption.</summary>
    public EvolutionResources Actual { get; }
    /// <summary>Gets whether the measured operation succeeded, failed, rejected, or was cancelled.</summary>
    public EvolutionResourceOutcome Outcome { get; }
}

/// <summary>Executes arbitrary proposal, refinement, screening, model, or evaluation work under a shared ledger.</summary>
public static class EvolutionResourceWork
{
    /// <summary>Reserves before invoking work and reconciles before returning its value.</summary>
    /// <remarks>Failure/cancellation without a returned receipt charges the maximum as unknown. This cannot terminate
    /// an uncooperative producer; the producer must enforce the maximum externally. Nested operations must have a single
    /// owner for each charge: do not charge a model call both here and inside an aggregate evaluator receipt.</remarks>
    public static async ValueTask<T> RunAsync<T>(EvolutionResourceLedger ledger, string operationId, EvolutionResourceStage stage,
        EvolutionResources estimated, EvolutionResources maximum,
        Func<CancellationToken, ValueTask<EvolutionResourceResult<T>>> work, int attempt = 1,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(ledger);
        Guard.NotNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        using EvolutionResourceReservation reservation = ledger.TryReserve(operationId, stage, estimated, maximum, attempt)
            ?? throw new EvolutionResourceBudgetException(operationId);
        EvolutionResourceResult<T> result = await work(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The resource-metered operation returned no receipt.");
        reservation.Complete(result.Actual, result.Outcome);
        return result.Value;
    }
}

/// <summary>Indicates that a resource reservation was denied before any work was dispatched.</summary>
public sealed class EvolutionResourceBudgetException : InvalidOperationException
{
    /// <summary>Creates a budget denial without embedding potentially sensitive operation identity in the message.</summary>
    public EvolutionResourceBudgetException(string operationId) : base("The resource ledger refused dispatch.") => OperationId = operationId;
    /// <summary>Gets the denied operation identity.</summary>
    public string OperationId { get; }
}
