// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/AllPassProgramFitnessEvaluator.cs
// Original license retained in ../Legacy/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.Programs;

/// <summary>Turns a public-test pass fraction into a hard gate without executing those tests twice.</summary>
internal sealed class AllPassProgramFitnessEvaluator : IProgramFitnessEvaluator
{
    private readonly IProgramFitnessEvaluator _inner;
    internal AllPassProgramFitnessEvaluator(IProgramFitnessEvaluator inner)
    {
        _inner = new VersionPinnedProgramFitnessEvaluator(inner);
        VersionHash = EvolutionHash.Combine(new[] { "all-pass-program-v1", _inner.Id, _inner.VersionHash });
    }
    public string Id => "all-pass-program";
    public string VersionHash { get; }
    public async ValueTask<EvolutionTaskResult> EvaluateAsync(ProgramGenome candidate, EvolutionEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.EvaluateAsync(candidate, context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Public correctness returned no result or resource receipt.");
        return result.Status != EvolutionEvaluationStatus.Completed ||
            (result.Direction == EvolutionOptimizationDirection.Maximize && result.Quality == 1 &&
             !result.ConstraintViolations.Any(value => value > 0))
            ? result : CorrectnessGatedProgramFitnessEvaluator.Copy(result, EvolutionEvaluationStatus.Rejected, result.CostUnits);
    }
}
