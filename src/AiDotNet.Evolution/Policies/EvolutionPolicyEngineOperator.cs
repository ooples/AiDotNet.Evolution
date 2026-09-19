namespace AiDotNet.Evolution;

/// <summary>A pinned, cost-receipting operator factory; policy search varies its weight, never its executable implementation.</summary>
/// <typeparam name="TGenome">The immutable genome type.</typeparam>
public sealed class EvolutionPolicyEngineOperator<TGenome>
{
    private readonly Func<ICostedEvolutionProposalSource<TGenome>> _factory;

    /// <summary>Registers a fixed backend and its externally enforceable per-proposal resource maximum.</summary>
    /// <remarks>Factories must create fresh isolated backends without unmetered external setup work. The engine adapter disposes owned disposable backends.</remarks>
    public EvolutionPolicyEngineOperator(string id, string versionHash, EvolutionResources maximumProposalResources,
        Func<ICostedEvolutionProposalSource<TGenome>> factory)
    {
        PolicyContract.Label(id); PolicyContract.Label(versionHash);
        if (id.Length > 64) throw new ArgumentException("Metered operator identities cannot exceed sixty-four characters.", nameof(id));
        Guard.NotNull(maximumProposalResources);
        if (!maximumProposalResources.Amounts.ContainsKey("cost_units") || maximumProposalResources.Amounts.Keys.Any(PolicyResources.IsCounter))
            throw new ArgumentException("Declare proposal cost_units without redefining policy counters.", nameof(maximumProposalResources));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        Id = id; VersionHash = versionHash; MaximumProposalResources = maximumProposalResources;
    }
    /// <summary>Gets the exact registered operator name used by every policy.</summary>
    public string Id { get; }
    /// <summary>Gets the pinned implementation/configuration fingerprint.</summary>
    public string VersionHash { get; }
    /// <summary>Gets the same-unit reservation ceiling for one proposal.</summary>
    public EvolutionResources MaximumProposalResources { get; }
    internal ICostedEvolutionProposalSource<TGenome> Create() =>
        _factory() ?? throw new InvalidOperationException("Policy operator factory returned no backend.");
}
