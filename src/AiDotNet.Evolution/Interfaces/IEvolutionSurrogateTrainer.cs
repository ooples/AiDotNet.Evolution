namespace AiDotNet.Evolution;

/// <summary>Optional model-library adapter; fitting receives only explicitly supplied measured search outcomes.</summary>
/// <typeparam name="TGenome">The immutable genome representation.</typeparam>
/// <remarks>Implementations must fit a fresh, deterministic model from the supplied observations, with no hidden
/// cross-call learning or confirmation data. Return actual same-unit costs, including failed work, and enforce
/// externally declared time/resource bounds. Do not meter these same costs twice.</remarks>
public interface IEvolutionSurrogateTrainer<TGenome>
{
    /// <summary>Gets the immutable backend, feature and fitting-policy identity.</summary>
    string VersionHash { get; }
    /// <summary>Fits a detached model and returns its actual resource receipt.</summary>
    ValueTask<EvolutionResourceResult<IEvolutionSurrogateModel<TGenome>>> FitAsync(
        IReadOnlyList<EvolutionSurrogateObservation<TGenome>> observations, CancellationToken cancellationToken = default);
}

/// <summary>A detached fitted predictor; its predictions never constitute evaluated fitness.</summary>
/// <typeparam name="TGenome">The immutable genome representation.</typeparam>
public interface IEvolutionSurrogateModel<TGenome>
{
    /// <summary>Gets the fitted model identity, including training-data and fitting semantics.</summary>
    string VersionHash { get; }
    /// <summary>Gets whether the backend's explicit validation policy permits acquisition; not a universal calibration guarantee.</summary>
    bool IsReliable { get; }
    /// <summary>Predicts one identified value per candidate, in any order, with an actual resource receipt.</summary>
    ValueTask<EvolutionResourceResult<IReadOnlyList<EvolutionSurrogatePrediction>>> PredictAsync(
        IReadOnlyList<EvolutionCanonicalGenome<TGenome>> candidates, CancellationToken cancellationToken = default);
}
