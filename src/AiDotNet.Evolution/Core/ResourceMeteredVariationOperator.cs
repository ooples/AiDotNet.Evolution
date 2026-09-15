using System.Globalization;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>Adapts a cost-receipting proposal backend to the engine with checkpointed generation-level cost attribution.</summary>
/// <typeparam name="TGenome">The immutable task-specific genome.</typeparam>
/// <remarks>Charges proposal work only, not evaluator/setup/refinement work outside the backend. Both its pending
/// attribution and backend state are checkpointed; persist the matching shared ledger separately. Methods are serialized,
/// like the engine's current proposal path. Mid-dispatch checkpoints and concurrent calls are rejected. No diagnostic
/// observer supplies learning or costs. A restored backend must be discarded if its own RestoreState fails.</remarks>
public sealed class ResourceMeteredVariationOperator<TGenome> : IOutcomeAwareVariationOperator<TGenome>, IEvolutionProposalCostProvider
{
    private const int MaximumPending = 65_536, MaximumStateCharacters = 16 * 1024 * 1024;
    private readonly ICostedEvolutionProposalSource<TGenome> _source;
    private readonly EvolutionResourceLedger _ledger;
    private readonly EvolutionResources _maximum;
    private SortedDictionary<long, Pending> _pending = new();
    private int _busy;

    /// <summary>Creates an adapter with externally enforced proposal maxima, including an explicit cost_units maximum.</summary>
    public ResourceMeteredVariationOperator(ICostedEvolutionProposalSource<TGenome> source, EvolutionResourceLedger ledger,
        EvolutionResources maximumProposalResources, string costUnitVersionHash)
    {
        Guard.NotNull(source); Guard.NotNull(ledger); Guard.NotNull(maximumProposalResources);
        ValidateIdentity(source.Id, 64, nameof(source)); ValidateIdentity(source.VersionHash, 256, nameof(source));
        ValidateIdentity(costUnitVersionHash, 256, nameof(costUnitVersionHash));
        if (!maximumProposalResources.Amounts.ContainsKey("cost_units") || maximumProposalResources.Amounts.Keys.Any(key => !ledger.Limits.Amounts.ContainsKey(key)))
            throw new ArgumentException("Declare cost_units and ledger limits for every proposal resource.", nameof(maximumProposalResources));
        _source = source; _ledger = ledger; _maximum = maximumProposalResources; CostUnitVersionHash = costUnitVersionHash;
        Id = "resource-metered:" + source.Id;
        VersionHash = EvolutionHash.Combine(new[] { "resource-metered-variation-v1", source.Id, source.VersionHash, costUnitVersionHash }
            .Concat(_maximum.Amounts.SelectMany(pair => new[] { pair.Key, pair.Value.ToString(CultureInfo.InvariantCulture) })));
    }
    /// <inheritdoc/>
    public string Id { get; }
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <inheritdoc/>
    public string CostUnitVersionHash { get; }

    /// <inheritdoc/>
    public async ValueTask<TGenome> ProposeAsync(EvolutionVariationContext<TGenome> context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context); using var guard = Enter(); cancellationToken.ThrowIfCancellationRequested();
        if (context.Generation <= 0 || _pending.ContainsKey(context.Generation)) throw new ArgumentException("Unique positive generation required.", nameof(context));
        if (_pending.Count >= MaximumPending) throw new InvalidOperationException("Pending proposal costs reached their bound.");
        string operation = Operation(context.Generation);
        using EvolutionResourceReservation? reservation = _ledger.TryReserve(operation, EvolutionResourceStage.Proposal, _maximum, _maximum);
        if (reservation is null)
        {
            _pending.Add(context.Generation, new Pending { Charged = Copy(EvolutionResources.Of("cost_units", 0)), Outcome = EvolutionResourceOutcome.Rejected });
            throw new EvolutionResourceBudgetException(operation);
        }
        var pending = new Pending { Charged = Copy(_maximum), Outcome = EvolutionResourceOutcome.Unknown, Dispatched = true };
        _pending.Add(context.Generation, pending);
        var result = await _source.ProposeAsync(context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Proposal backend returned no receipt.");
        if (!result.Actual.Amounts.ContainsKey("cost_units")) throw new InvalidOperationException("Proposal receipt omitted explicit cost_units.");
        bool exceeded = result.Actual.Amounts.Any(pair => pair.Value > _maximum[pair.Key]);
        bool failed = exceeded || result.Value is null || result.Outcome != EvolutionResourceOutcome.Completed;
        EvolutionResourceOutcome outcome = failed && result.Outcome == EvolutionResourceOutcome.Completed ? EvolutionResourceOutcome.Failed : result.Outcome;
        reservation.Complete(result.Actual, outcome);
        pending.Charged = Copy(result.Actual); pending.Outcome = outcome; pending.ExceededMaximum = exceeded;
        if (failed) throw new InvalidOperationException("Proposal backend failed, returned no genome, or exceeded its declared maximum; its charge is retained.");
        return result.Value;
    }

    /// <inheritdoc/>
    public EvolutionProposalCost GetProposalCost(long generation)
    {
        using var guard = Enter();
        if (!_pending.TryGetValue(generation, out Pending? pending)) throw new InvalidOperationException("Unknown or consumed proposal cost identity.");
        return new EvolutionProposalCost(Operation(generation), new EvolutionResources(pending.Charged!), pending.Outcome, pending.ExceededMaximum);
    }

    /// <inheritdoc/>
    public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
    {
        Guard.NotNull(evaluation); using var guard = Enter();
        long generation = evaluation.Lineage.Generation;
        if (!_pending.TryGetValue(generation, out Pending? pending)) throw new InvalidOperationException("Unknown or repeated proposal outcome.");
        if (pending.Dispatched) _source.Observe(evaluation, insertionResult);
        _pending.Remove(generation);
    }

    /// <inheritdoc/>
    public string CaptureState()
    {
        using var guard = Enter();
        string backend = _source.CaptureState();
        if (string.IsNullOrEmpty(backend)) throw new InvalidOperationException("Backend checkpoint state must be explicit and nonempty.");
        string state = JsonSerializer.Serialize(new State { VersionHash = VersionHash, Backend = backend, Pending = _pending }, EvolutionJson.Compact);
        if (state.Length > MaximumStateCharacters) throw new InvalidOperationException("Proposal checkpoint exceeds its bound.");
        return state;
    }

    /// <inheritdoc/>
    public void RestoreState(string state)
    {
        Guard.NotNull(state); using var guard = Enter();
        if (state.Length > MaximumStateCharacters) throw new InvalidDataException("Proposal checkpoint exceeds its bound.");
        State? restored;
        try { restored = JsonSerializer.Deserialize<State>(state, EvolutionJson.Compact); }
        catch (JsonException exception) { throw new InvalidDataException("Malformed proposal checkpoint.", exception); }
        if (restored is null || restored.VersionHash != VersionHash || string.IsNullOrEmpty(restored.Backend) || restored.Pending is null || restored.Pending.Count > MaximumPending)
            throw new InvalidDataException("Incompatible proposal checkpoint.");
        foreach (var pair in restored.Pending)
        {
            Pending pending = pair.Value;
            if (pair.Key <= 0 || pending is null || pending.Charged is null || !Enum.IsDefined(typeof(EvolutionResourceOutcome), pending.Outcome))
                throw new InvalidDataException("Invalid pending proposal identity or receipt.");
            EvolutionResources charged;
            try { charged = new EvolutionResources(pending.Charged); }
            catch (ArgumentException exception) { throw new InvalidDataException("Invalid proposal resource amount.", exception); }
            if (!charged.Amounts.ContainsKey("cost_units") || charged.Amounts.Keys.Any(key => !_ledger.Limits.Amounts.ContainsKey(key)))
                throw new InvalidDataException("Missing cost_units or undeclared proposal resource.");
            if (pending.ExceededMaximum != charged.Amounts.Any(item => item.Value > _maximum[item.Key])) throw new InvalidDataException("Inconsistent maximum violation.");
            if (pending.Outcome == EvolutionResourceOutcome.Unknown && _maximum.Amounts.Any(item => charged[item.Key] != item.Value))
                throw new InvalidDataException("Unknown proposal cost must retain its maximum.");
            if (!pending.Dispatched && (pending.Outcome != EvolutionResourceOutcome.Rejected || charged.Amounts.Any(item => item.Value != 0)))
                throw new InvalidDataException("Undispatched work cannot have consumed resources.");
            if (pending.ExceededMaximum && pending.Outcome == EvolutionResourceOutcome.Completed) throw new InvalidDataException("Overrun cannot be successful.");
        }
        _source.RestoreState(restored.Backend!);
        _pending = restored.Pending;
    }

    private string Operation(long generation) => "proposal/" + VersionHash + "/" + generation.ToString(CultureInfo.InvariantCulture);
    private Invocation Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("Concurrent proposal use or mid-dispatch checkpoint is unsupported.");
        return new Invocation(this);
    }
    private sealed class Invocation(ResourceMeteredVariationOperator<TGenome> owner) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref owner._busy, 0);
    }
    private static Dictionary<string, decimal> Copy(EvolutionResources values) => values.Amounts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static void ValidateIdentity(string value, int limit, string argument)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(char.IsControl)) throw new ArgumentException("Bounded printable identity required.", argument);
    }
    private sealed class Pending
    {
        public Pending() { }
        public Dictionary<string, decimal>? Charged { get; set; }
        public EvolutionResourceOutcome Outcome { get; set; }
        public bool ExceededMaximum { get; set; }
        public bool Dispatched { get; set; }
    }
    private sealed class State
    {
        public State() { }
        public string? VersionHash { get; set; }
        public string? Backend { get; set; }
        public SortedDictionary<long, Pending>? Pending { get; set; }
    }
}
