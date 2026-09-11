using System.Collections.ObjectModel;

namespace AiDotNet.Evolution;

/// <summary>An immutable vector of named, nonnegative resource amounts.</summary>
/// <remarks>Names define units, not prices: for example cost_units, input_tokens, or cpu_seconds.
/// Currency conversion belongs to the consumer. Decimal arithmetic avoids cumulative rounding of receipts.</remarks>
public sealed class EvolutionResources
{
    /// <summary>Maximum amount in one receipt, leaving headroom for bounded accumulation.</summary>
    public const decimal MaximumAmount = 1_000_000_000_000_000_000m;
    /// <summary>An empty resource vector.</summary>
    public static EvolutionResources Empty { get; } = new(Array.Empty<KeyValuePair<string, decimal>>());

    /// <summary>Copies up to 32 uniquely named amounts. Missing amounts mean zero.</summary>
    public EvolutionResources(IEnumerable<KeyValuePair<string, decimal>> amounts)
    {
        Guard.NotNull(amounts);
        var copy = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, decimal> pair in amounts)
        {
            if (copy.Count >= 32) throw new ArgumentException("At most 32 resources are supported.", nameof(amounts));
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 64 || pair.Key.Any(char.IsControl))
                throw new ArgumentException("Resource names must be nonblank, printable and at most 64 characters.", nameof(amounts));
            if (pair.Value < 0 || pair.Value > MaximumAmount)
                throw new ArgumentOutOfRangeException(nameof(amounts));
            if (copy.ContainsKey(pair.Key)) throw new ArgumentException("Duplicate resource name.", nameof(amounts));
            copy.Add(pair.Key, pair.Value);
        }
        Amounts = new ReadOnlyDictionary<string, decimal>(copy);
    }

    /// <summary>Gets the detached amounts in ordinal name order.</summary>
    public IReadOnlyDictionary<string, decimal> Amounts { get; }
    /// <summary>Gets an amount, or zero when absent.</summary>
    public decimal this[string name] => Amounts.TryGetValue(name, out decimal amount) ? amount : 0;
    /// <summary>Creates a single-resource vector.</summary>
    public static EvolutionResources Of(string name, decimal amount) => new(new[] { new KeyValuePair<string, decimal>(name, amount) });
}

/// <summary>The kind of work charged independently of engine evaluation-attempt counters.</summary>
public enum EvolutionResourceStage
{
    /// <summary>Comparative setup, compilation, or configuration tuning.</summary>
    Setup,
    /// <summary>Candidate generation, including model calls.</summary>
    Proposal,
    /// <summary>Inner optimization or repair.</summary>
    Refinement,
    /// <summary>Learned-model fitting.</summary>
    SurrogateTraining,
    /// <summary>Learned-model scoring.</summary>
    SurrogateInference,
    /// <summary>Structural, behavioral, or embedding novelty checks.</summary>
    Novelty,
    /// <summary>A screening stage, including rejected candidates.</summary>
    Screening,
    /// <summary>A full evaluation or an independent replicate.</summary>
    Evaluation,
    /// <summary>A final independent correctness/performance confirmation.</summary>
    Confirmation,
    /// <summary>Persistent evidence-store calls, separate from evaluation/acquisition work.</summary>
    Persistence
}

/// <summary>A terminal resource-accounting outcome, independent of archive admission.</summary>
public enum EvolutionResourceOutcome
{
    /// <summary>The operation returned a receipt successfully.</summary>
    Completed,
    /// <summary>The operation failed after consuming reported resources.</summary>
    Failed,
    /// <summary>The operation rejected a candidate after consuming resources.</summary>
    Rejected,
    /// <summary>The operation was cancelled after consuming reported resources.</summary>
    Canceled,
    /// <summary>No actual receipt was available; the reserved upper bound was charged conservatively.</summary>
    Unknown
}

/// <summary>An immutable operation receipt with explicit estimate and upper-bound violations.</summary>
public sealed class EvolutionResourceReceipt
{
    internal EvolutionResourceReceipt(string operationId, EvolutionResourceStage stage, int attempt,
        EvolutionResources estimated, EvolutionResources maximum, EvolutionResources charged, EvolutionResourceOutcome outcome)
    {
        OperationId = operationId;
        Stage = stage;
        Attempt = attempt;
        Estimated = estimated;
        Maximum = maximum;
        Charged = charged;
        Outcome = outcome;
        ExceededEstimate = charged.Amounts.Any(pair => pair.Value > estimated[pair.Key]);
        ExceededMaximum = charged.Amounts.Any(pair => pair.Value > maximum[pair.Key]);
    }

    /// <summary>Gets the caller-supplied stable identity, including evaluation, stage, fidelity and replicate as applicable.</summary>
    public string OperationId { get; }
    /// <summary>Gets the charged stage.</summary>
    public EvolutionResourceStage Stage { get; }
    /// <summary>Gets the one-based attempt; retries need distinct operation identities.</summary>
    public int Attempt { get; }
    /// <summary>Gets the planning estimate.</summary>
    public EvolutionResources Estimated { get; }
    /// <summary>Gets the upper bound reserved before dispatch.</summary>
    public EvolutionResources Maximum { get; }
    /// <summary>Gets actual consumption, or the conservative upper bound when Outcome is Unknown.</summary>
    public EvolutionResources Charged { get; }
    /// <summary>Gets the terminal accounting outcome.</summary>
    public EvolutionResourceOutcome Outcome { get; }
    /// <summary>Gets whether actual consumption exceeded the planning estimate.</summary>
    public bool ExceededEstimate { get; }
    /// <summary>Gets whether a producer violated its declared maximum. Further reservations fail closed.</summary>
    public bool ExceededMaximum { get; }
}
