using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.CSharp;

/// <summary>Constructs compiler-guided, costed portfolio arms without the AiDotNet facade.</summary>
public static class CSharpProgramVariation
{
    /// <summary>Charges setup immediately; compilation does not execute or prove candidate correctness.</summary>
    public static MeteredProgramVariationOperator Create(ICSharpProposalClient client,
        CSharpProgramSourceOptions programOptions, CSharpProgramEvolutionOptions compilerOptions,
        ProgramEvolutionResourceOptions resources)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(programOptions);
        ArgumentNullException.ThrowIfNull(compilerOptions);
        ArgumentNullException.ThrowIfNull(resources);
        var compiler = compilerOptions.Snapshot();
        var program = programOptions.Clone();
        program.Validate();
        if (!string.Equals(compiler.CostUnitVersionHash, resources.CostUnitVersionHash, StringComparison.Ordinal))
            throw new ArgumentException("Compiler and evaluator cost-unit identities must match.", nameof(resources));
        var source = CSharpProposalSource.Create(client, compiler, program, resources.Ledger);
        return new(source, resources.Ledger, source.MaximumProposalResources, source.CostUnitVersionHash);
    }
}
