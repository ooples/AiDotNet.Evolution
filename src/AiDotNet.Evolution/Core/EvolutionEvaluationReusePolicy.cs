using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Explicit opt-in meaning of an existing measurement; no mode creates independent new observations.</summary>
public enum EvolutionEvaluationReuseMode
{
    /// <summary>Do not reuse stored evaluations.</summary>
    Disabled,
    /// <summary>The caller declares that exact-key evaluation semantics are deterministic.</summary>
    Deterministic,
    /// <summary>Reuse a prior sample aggregate with its original IDs, uncertainty and time, not as a fresh replicate.</summary>
    ExistingSamples
}

/// <summary>Auditable reason why existing evidence can or cannot be reused.</summary>
public enum EvolutionEvaluationReuseDecision
{
    /// <summary>The explicit applicability/freshness/sample policy permits a copy.</summary>
    Eligible,
    /// <summary>Persistent reuse is disabled.</summary>
    Disabled,
    /// <summary>The caller requested fresh work regardless of a stored result.</summary>
    ForceFresh,
    /// <summary>No existing record was returned.</summary>
    Missing,
    /// <summary>Applicability, genome bytes or measurement policy differs.</summary>
    KeyMismatch,
    /// <summary>The observation is in the future relative to the caller's clock.</summary>
    FutureObservation,
    /// <summary>The measurement exceeds the explicitly declared maximum age.</summary>
    Expired,
    /// <summary>The original sample count is below the declared minimum.</summary>
    InsufficientSamples,
    /// <summary>No standard error was supplied for stochastic sample reuse.</summary>
    UncertaintyUnavailable,
    /// <summary>The retained standard error exceeds the optional declared maximum.</summary>
    UncertaintyTooHigh,
    /// <summary>No store call was dispatched because its resource reservation was denied.</summary>
    BudgetExhausted,
    /// <summary>The dispatched store call failed or returned malformed evidence; never a cache hit.</summary>
    StorageUnavailable
}

/// <summary>Immutable exact-key freshness and original-sample policy; callers provide the clock explicitly.</summary>
public sealed class EvolutionEvaluationReusePolicy
{
    /// <summary>Creates an explicit opt-in policy; use Disabled or forceFresh for independent confirmation.</summary>
    public EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode mode, TimeSpan maximumAge, int minimumSamples = 1,
        double? maximumStandardError = null)
    {
        if (!Enum.IsDefined(typeof(EvolutionEvaluationReuseMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (maximumAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumAge));
        if (minimumSamples < 1 || minimumSamples > EvolutionMeasurementOrigin.MaximumSampleIds) throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        if (maximumStandardError.HasValue && (!EvolutionDescriptorDefinition.IsFinite(maximumStandardError.Value) || maximumStandardError.Value < 0))
            throw new ArgumentOutOfRangeException(nameof(maximumStandardError));
        Mode = mode; MaximumAge = maximumAge; MinimumSamples = minimumSamples; MaximumStandardError = maximumStandardError;
        VersionHash = EvolutionHash.Combine(new[] { "persistent-reuse-policy-v1", mode.ToString(), maximumAge.Ticks.ToString(CultureInfo.InvariantCulture),
            minimumSamples.ToString(CultureInfo.InvariantCulture), maximumStandardError?.ToString("R", CultureInfo.InvariantCulture) ?? "none" });
    }
    /// <summary>Gets the caller's explicit reuse declaration.</summary>
    public EvolutionEvaluationReuseMode Mode { get; }
    /// <summary>Gets the maximum permitted age since the original observation, not since file creation.</summary>
    public TimeSpan MaximumAge { get; }
    /// <summary>Gets the minimum number of original samples.</summary>
    public int MinimumSamples { get; }
    /// <summary>Gets the optional upper standard-error threshold.</summary>
    public double? MaximumStandardError { get; }
    /// <summary>Gets the policy's semantic fingerprint.</summary>
    public string VersionHash { get; }

    /// <summary>Checks eligibility without changing clocks, evidence, budgets or storage.</summary>
    public EvolutionEvaluationReuseDecision Check(EvolutionEvaluationCacheKey requested, EvolutionEvaluationCacheRecord? existing,
        DateTimeOffset now, bool forceFresh = false)
    {
        Guard.NotNull(requested);
        if (now == default) throw new ArgumentOutOfRangeException(nameof(now));
        if (forceFresh) return EvolutionEvaluationReuseDecision.ForceFresh;
        if (Mode == EvolutionEvaluationReuseMode.Disabled) return EvolutionEvaluationReuseDecision.Disabled;
        if (existing is null) return EvolutionEvaluationReuseDecision.Missing;
        if (requested.StableKey != existing.Key.StableKey) return EvolutionEvaluationReuseDecision.KeyMismatch;
        if (existing.Origin.ObservedAt > now) return EvolutionEvaluationReuseDecision.FutureObservation;
        if (now - existing.Origin.ObservedAt > MaximumAge) return EvolutionEvaluationReuseDecision.Expired;
        if (existing.Origin.SampleCount < MinimumSamples) return EvolutionEvaluationReuseDecision.InsufficientSamples;
        if ((Mode == EvolutionEvaluationReuseMode.ExistingSamples || MaximumStandardError.HasValue) && !existing.Origin.StandardError.HasValue)
            return EvolutionEvaluationReuseDecision.UncertaintyUnavailable;
        if (MaximumStandardError.HasValue && existing.Origin.StandardError > MaximumStandardError)
            return EvolutionEvaluationReuseDecision.UncertaintyTooHigh;
        return EvolutionEvaluationReuseDecision.Eligible;
    }
}
