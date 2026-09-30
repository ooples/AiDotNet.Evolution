using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Deployment;

/// <summary>Fresh private program search; admitted deployment limits always override caller engine budgets.</summary>
public sealed class ProgramDeploymentSearchOptions
{
    /// <summary>Gets or sets the starting programs; at least one, and no more than the request's proposal budget.</summary>
    public List<string> SeedPrograms { get; set; } = new();

    /// <summary>Gets or sets the program language.</summary>
    public ProgramLanguage Language { get; set; } = ProgramLanguage.Generic;

    /// <summary>Gets or sets the longest accepted program, in characters. Defaults to 65,536.</summary>
    public int MaxProgramChars { get; set; } = 65_536;

    /// <summary>Gets or sets the variation operator. Required; supply a fresh instance per search.</summary>
    public IProgramVariationOperator? CustomVariation { get; set; }

    /// <summary>Gets or sets the search fitness evaluator. Required; supply a fresh instance per search.</summary>
    public IProgramFitnessEvaluator? CustomFitnessEvaluator { get; set; }

    /// <summary>Gets or sets resource accounting. Must be <c>null</c>: resource-accounted retuning is refused.</summary>
    public ProgramEvolutionResourceOptions? ResourceAccounting { get; set; }

    /// <summary>Gets or sets engine options. Only <see cref="EvolutionEngineOptions.Seed"/> is used; the deployment request sets the limits.</summary>
    public EvolutionEngineOptions Engine { get; set; } = new();

    internal ProgramDeploymentSearchOptions Clone()
    {
        if (SeedPrograms is null || Engine is null) throw new ArgumentException("Seeds and engine options are required.");
        if (MaxProgramChars is < 1 or > ProgramGenome.MaxSourceLength) throw new ArgumentOutOfRangeException(nameof(MaxProgramChars));
        var copy = (ProgramDeploymentSearchOptions)MemberwiseClone();
        copy.SeedPrograms = new(SeedPrograms);
        copy.Engine = new() { Seed = Engine.Seed };
        return copy;
    }

    internal IEnumerable<ProgramGenome> CreateSeedGenomes() => SeedPrograms.Select(source => new ProgramGenome(source, Language));
}
