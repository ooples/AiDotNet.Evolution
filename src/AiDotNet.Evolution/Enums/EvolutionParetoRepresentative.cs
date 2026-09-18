namespace AiDotNet.Evolution;

/// <summary>Names the explicitly lossy single representative returned by Best on a Pareto front.</summary>
public enum EvolutionParetoRepresentative
{
    /// <summary>Minimizes the sum of squared normalized losses; ties use objective order then genome identity.</summary>
    ClosestToIdeal = 0,
    /// <summary>Uses the declared objective order as a lexicographic priority.</summary>
    Lexicographic = 1,
    /// <summary>Uses scalar quality among retained front entries only, not as an archive admission rule.</summary>
    ScalarQuality = 2
}
