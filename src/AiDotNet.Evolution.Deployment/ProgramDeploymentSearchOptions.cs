using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Deployment;

/// <summary>Fresh private program search; admitted deployment limits always override caller engine budgets.</summary>
public sealed class ProgramDeploymentSearchOptions
{
    public List<string> SeedPrograms { get; set; } = new();
    public ProgramLanguage Language { get; set; } = ProgramLanguage.Generic;
    public int MaxProgramChars { get; set; } = 65_536;
    public IProgramVariationOperator? CustomVariation { get; set; }
    public IProgramFitnessEvaluator? CustomFitnessEvaluator { get; set; }
    public ProgramEvolutionResourceOptions? ResourceAccounting { get; set; }
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
