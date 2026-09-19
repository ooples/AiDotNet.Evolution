using System.Collections.ObjectModel;
using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Fixed parent-selection schedules available to offline policy search.</summary>
public enum EvolutionPolicySelectionSchedule
{
    /// <summary>Sample occupied archive cells uniformly throughout the run.</summary>
    Uniform,
    /// <summary>Always select the current best elite.</summary>
    Greedy,
    /// <summary>Use uniform selection for the first half of the proposal allowance, then select the best elite.</summary>
    ExploreThenExploit
}

/// <summary>Bounded context contracts; policies never introduce executable code or arbitrary prompt templates.</summary>
public enum EvolutionPolicyContext
{
    /// <summary>Expose only the selected parent and deterministic proposal stream.</summary>
    ParentOnly,
    /// <summary>Also expose up to two selected inspiration elites.</summary>
    Inspirations,
    /// <summary>Also expose the engine's bounded parent feedback artifacts.</summary>
    FeedbackAndInspirations
}

/// <summary>An immutable, versioned declarative recipe for a trusted engine adapter.</summary>
public sealed class EvolutionSearchPolicy
{
    /// <summary>Creates a policy with up to sixteen named operators, integer weights, a schedule, restart interval and context contract.</summary>
    /// <remarks>Zero weights disable operators. A zero restart interval disables restarts. Inner-run budgets always override policy requests.</remarks>
    public EvolutionSearchPolicy(IEnumerable<KeyValuePair<string, int>> operatorWeights,
        EvolutionPolicySelectionSchedule selection = EvolutionPolicySelectionSchedule.Uniform,
        int restartAfterEvaluations = 0, EvolutionPolicyContext context = EvolutionPolicyContext.ParentOnly)
    {
        Guard.NotNull(operatorWeights);
        if (!Enum.IsDefined(typeof(EvolutionPolicySelectionSchedule), selection)) throw new ArgumentOutOfRangeException(nameof(selection));
        if (!Enum.IsDefined(typeof(EvolutionPolicyContext), context)) throw new ArgumentOutOfRangeException(nameof(context));
        if (restartAfterEvaluations < 0 || restartAfterEvaluations > 1_000_000) throw new ArgumentOutOfRangeException(nameof(restartAfterEvaluations));
        var weights = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in operatorWeights)
        {
            if (weights.Count >= 16) throw new ArgumentException("At most sixteen policy operators are supported.", nameof(operatorWeights));
            PolicyContract.Label(item.Key);
            if (item.Value < 0 || item.Value > 1_000_000) throw new ArgumentOutOfRangeException(nameof(operatorWeights));
            if (weights.ContainsKey(item.Key)) throw new ArgumentException("Duplicate policy operator.", nameof(operatorWeights));
            weights.Add(item.Key, item.Value);
        }
        if (weights.Count == 0 || weights.Values.All(value => value == 0)) throw new ArgumentException("A policy needs at least one enabled operator.", nameof(operatorWeights));
        // Mixtures are ratios, so proportional spellings must not become distinct meta-search trials.
        int divisor = weights.Values.Aggregate(0, GreatestCommonDivisor);
        foreach (string key in weights.Keys.ToArray()) weights[key] /= divisor;
        OperatorWeights = new ReadOnlyDictionary<string, int>(weights);
        Selection = selection; RestartAfterEvaluations = restartAfterEvaluations; Context = context;
        MixtureHash = EvolutionHash.Combine(weights.SelectMany(item => new[] { item.Key, item.Value.ToString(CultureInfo.InvariantCulture) }));
        Id = EvolutionHash.Combine(new[] { "declarative-search-policy-v1", MixtureHash, selection.ToString(),
            restartAfterEvaluations.ToString(CultureInfo.InvariantCulture), context.ToString() });
    }

    /// <summary>Gets the exact declarative recipe identity, independent of display names.</summary>
    public string Id { get; }
    /// <summary>Gets the frozen ordinal operator weights.</summary>
    public IReadOnlyDictionary<string, int> OperatorWeights { get; }
    /// <summary>Gets the fixed parent-selection schedule.</summary>
    public EvolutionPolicySelectionSchedule Selection { get; }
    /// <summary>Gets the evaluation interval between fresh restarts, or zero to disable them.</summary>
    public int RestartAfterEvaluations { get; }
    /// <summary>Gets the context permitted to reach proposal operators.</summary>
    public EvolutionPolicyContext Context { get; }
    internal string MixtureHash { get; }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0) { int remainder = left % right; left = right; right = remainder; }
        return left;
    }

    internal int Distance(EvolutionSearchPolicy other) =>
        (MixtureHash == other.MixtureHash ? 0 : 1) + (Selection == other.Selection ? 0 : 1) +
        (RestartAfterEvaluations == other.RestartAfterEvaluations ? 0 : 1) + (Context == other.Context ? 0 : 1);
}

/// <summary>A finite, predeclared catalogue that bounds the entire policy search, including mutation.</summary>
/// <remarks>Populate a Cartesian product when each dimension should vary independently. Sparse catalogues intentionally forbid missing combinations.</remarks>
public sealed class EvolutionPolicySpace
{
    private readonly EvolutionSearchPolicy[] _policies;
    private readonly HashSet<string> _identities;

    /// <summary>Copies one to 4,096 unique policies with the same declared operator contract.</summary>
    public EvolutionPolicySpace(IEnumerable<EvolutionSearchPolicy> policies)
    {
        Guard.NotNull(policies);
        var values = new List<EvolutionSearchPolicy>();
        _identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in policies)
        {
            if (values.Count >= 4096) throw new ArgumentException("Policy catalogue exceeds its finite bound.", nameof(policies));
            if (policy is null) throw new ArgumentException("Null policy.", nameof(policies));
            if (!_identities.Add(policy.Id)) throw new ArgumentException("Duplicate policy identity.", nameof(policies));
            if (values.Count != 0 && !values[0].OperatorWeights.Keys.SequenceEqual(policy.OperatorWeights.Keys))
                throw new ArgumentException("All policies must share the same declared operator names.", nameof(policies));
            values.Add(policy);
        }
        if (values.Count == 0) throw new ArgumentException("The policy catalogue cannot be empty.", nameof(policies));
        _policies = values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        Policies = Array.AsReadOnly(_policies);
        VersionHash = EvolutionHash.Combine(new[] { "finite-policy-space-v1" }.Concat(_policies.Select(value => value.Id)));
    }

    /// <summary>Gets policies in stable identity order.</summary>
    public IReadOnlyList<EvolutionSearchPolicy> Policies { get; }
    /// <summary>Gets the exact catalogue fingerprint.</summary>
    public string VersionHash { get; }
    /// <summary>Checks exact membership without accepting an executable replacement.</summary>
    public bool Contains(EvolutionSearchPolicy policy) => policy is not null && _identities.Contains(policy.Id);
    /// <summary>Samples the predeclared catalogue using a caller-owned stream.</summary>
    public EvolutionSearchPolicy Sample(StableRandom random)
    {
        Guard.NotNull(random);
        return _policies[random.NextInt(_policies.Length)];
    }
    /// <summary>Mutates one declarative dimension when a permitted neighbor exists; otherwise explores the same finite catalogue.</summary>
    public EvolutionSearchPolicy Mutate(EvolutionSearchPolicy parent, StableRandom random)
    {
        Guard.NotNull(random);
        if (!Contains(parent)) throw new ArgumentException("Parent is outside the declared policy space.", nameof(parent));
        var neighbors = _policies.Where(value => parent.Distance(value) == 1).ToArray();
        return neighbors.Length == 0 ? Sample(random) : neighbors[random.NextInt(neighbors.Length)];
    }
}

internal static class PolicyContract
{
    internal static void Hash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 protocol or evidence digest is required.", nameof(value));
    }
    internal static void Label(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) || value != value.Trim())
            throw new ArgumentException("Policy identities must be bounded, printable and unpadded.", nameof(value));
        _ = new System.Text.UTF8Encoding(false, true).GetByteCount(value);
    }
}
