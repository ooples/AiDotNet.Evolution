using System.Collections.ObjectModel;

namespace AiDotNet.Evolution;

/// <summary>Optional fitted-model reliability evidence; the generic selector does not invent calibration claims.</summary>
public interface IEvolutionSurrogateDiagnosticModel
{
    /// <summary>Gets immutable backend-specific evidence retained with acquisition and unreliable-model fallback decisions.</summary>
    EvolutionSurrogateValidationReport ValidationReport { get; }
}

/// <summary>A bounded, detached reliability report whose meaning is defined by its versioned backend policy.</summary>
public sealed class EvolutionSurrogateValidationReport
{
    /// <summary>Creates auditable validation metadata without admitting predictions to an archive.</summary>
    public EvolutionSurrogateValidationReport(string policyVersionHash, string reason, IReadOnlyDictionary<string, double> metrics)
    {
        Guard.NotNullOrWhiteSpace(policyVersionHash); Guard.NotNullOrWhiteSpace(reason); Guard.NotNull(metrics);
        if (policyVersionHash.Length > 256 || policyVersionHash.Any(char.IsControl) || reason.Length > 128 || reason.Any(char.IsControl) || metrics.Count > 32)
            throw new ArgumentException("Surrogate validation metadata exceeds its bounds.");
        var copy = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var metric in metrics)
        {
            if (copy.Count >= 32 || string.IsNullOrWhiteSpace(metric.Key) || metric.Key.Length > 128 || metric.Key.Any(char.IsControl) ||
                !EvolutionDescriptorDefinition.IsFinite(metric.Value) || copy.ContainsKey(metric.Key))
                throw new ArgumentException("Surrogate validation metrics must have unique bounded names and finite values.", nameof(metrics));
            copy.Add(metric.Key, metric.Value);
        }
        PolicyVersionHash = policyVersionHash; Reason = reason; Metrics = new ReadOnlyDictionary<string, double>(copy);
    }
    /// <summary>Gets the policy fingerprint defining the diagnostic metrics and their limitations.</summary>
    public string PolicyVersionHash { get; }
    /// <summary>Gets the backend's acceptance or fallback reason.</summary>
    public string Reason { get; }
    /// <summary>Gets at most 32 finite backend-defined metrics; these are not archive fitness.</summary>
    public IReadOnlyDictionary<string, double> Metrics { get; }
}
