namespace AiDotNet.Evolution;

/// <summary>A feasible, freshly measured search outcome; constructing this contract does not prove evaluator correctness.</summary>
/// <typeparam name="TGenome">The immutable genome representation.</typeparam>
public sealed class EvolutionSurrogateObservation<TGenome>
{
    /// <summary>Validates candidate/evaluation identity and rejects failures, cache hits, zero-attempt and infeasible records.</summary>
    /// <remarks>Do not supply sealed confirmation data. This API validates record structure, not data access isolation.</remarks>
    public EvolutionSurrogateObservation(EvolutionCandidate<TGenome> candidate, EvolutionEvaluation evaluation)
    {
        Guard.NotNull(candidate); Guard.NotNull(evaluation);
        if (candidate.EvaluationId != evaluation.EvaluationId || candidate.CanonicalGenome.Id != evaluation.GenomeId ||
            evaluation.Status != EvolutionEvaluationStatus.Completed || !evaluation.Quality.HasValue ||
            evaluation.IsMeasurementReuse || evaluation.Cost.AttemptCount == 0 ||
            evaluation.ConstraintViolations.Any(value => value > 0))
            throw new ArgumentException("Surrogate observations require matching fresh feasible measured outcomes.", nameof(evaluation));
        Candidate = candidate.CanonicalGenome; Evaluation = evaluation;
    }
    /// <summary>Gets the independently owned canonical candidate.</summary>
    public EvolutionCanonicalGenome<TGenome> Candidate { get; }
    /// <summary>Gets the measured outcome and its provenance.</summary>
    public EvolutionEvaluation Evaluation { get; }
}

/// <summary>Unvalidated model output, deliberately separate from task/evaluation result types.</summary>
public sealed class EvolutionSurrogatePrediction
{
    /// <summary>Creates one finite prediction. Uncertainty is a backend-defined nonnegative quality-scale estimate, not a confidence interval.</summary>
    public EvolutionSurrogatePrediction(string genomeId, double mean, double uncertainty, bool withinTrainingDomain)
    {
        Guard.NotNullOrWhiteSpace(genomeId);
        if (genomeId.Length > 256) throw new ArgumentException("Prediction identity exceeds 256 characters.", nameof(genomeId));
        if (!EvolutionDescriptorDefinition.IsFinite(mean) || !EvolutionDescriptorDefinition.IsFinite(uncertainty) || uncertainty < 0)
            throw new ArgumentOutOfRangeException(nameof(uncertainty));
        GenomeId = genomeId; Mean = mean; Uncertainty = uncertainty; WithinTrainingDomain = withinTrainingDomain;
    }
    /// <summary>Gets the exact canonical identity this prediction describes.</summary>
    public string GenomeId { get; }
    /// <summary>Gets predicted scalar quality, not measured fitness.</summary>
    public double Mean { get; }
    /// <summary>Gets backend-defined uncertainty on the quality scale.</summary>
    public double Uncertainty { get; }
    /// <summary>Gets whether the backend considers this candidate within its declared support domain.</summary>
    public bool WithinTrainingDomain { get; }
}

/// <summary>The acquisition or explicit uniform-fallback path used for a proposal pool.</summary>
public enum EvolutionSurrogateSelectionReason
{
    /// <summary>Selected by direction-aware optimistic acquisition.</summary>
    Acquisition,
    /// <summary>The explicit probabilistic exploration allocation selected uniform sampling.</summary>
    Exploration,
    /// <summary>There were too few distinct measured records to fit.</summary>
    InsufficientData,
    /// <summary>The fitted model did not pass its backend validation policy.</summary>
    UnreliableModel,
    /// <summary>At least one proposed candidate was outside the backend's supported domain.</summary>
    UnfamiliarPool,
    /// <summary>Predictions were missing, duplicated, mismatched or numerically unusable.</summary>
    InvalidPredictions,
    /// <summary>Training failed or returned a non-success receipt.</summary>
    TrainingFailure,
    /// <summary>Inference failed or returned a non-success receipt.</summary>
    InferenceFailure,
    /// <summary>The ledger refused training before dispatch.</summary>
    TrainingBudgetDenied,
    /// <summary>The ledger refused inference before dispatch.</summary>
    InferenceBudgetDenied
}

/// <summary>A candidate selection and unvalidated prediction audit; no archive admission or deployment authorization.</summary>
/// <typeparam name="TGenome">The immutable genome representation.</typeparam>
public sealed class EvolutionSurrogateSelection<TGenome>
{
    internal EvolutionSurrogateSelection(EvolutionCanonicalGenome<TGenome> candidate, EvolutionSurrogateSelectionReason reason,
        string operationIdentity, string trainingIdentity, string? modelVersionHash, double explorationProbability,
        int poolSize, IEnumerable<EvolutionSurrogatePrediction> predictions)
    {
        Candidate = candidate; Reason = reason; OperationIdentity = operationIdentity; TrainingIdentity = trainingIdentity;
        ModelVersionHash = modelVersionHash; ExplorationProbability = explorationProbability; PoolSize = poolSize;
        Predictions = Array.AsReadOnly(predictions.ToArray());
    }
    /// <summary>Gets the selected candidate, which still requires true evaluation.</summary>
    public EvolutionCanonicalGenome<TGenome> Candidate { get; }
    /// <summary>Gets the acquisition or fallback reason.</summary>
    public EvolutionSurrogateSelectionReason Reason { get; }
    /// <summary>Gets the identity linking proposal, training and inference ledger operations.</summary>
    public string OperationIdentity { get; }
    /// <summary>Gets the identity of the ordered, explicitly supplied measured records.</summary>
    public string TrainingIdentity { get; }
    /// <summary>Gets the fitted-model identity when fitting succeeded.</summary>
    public string? ModelVersionHash { get; }
    /// <summary>Gets the configured exploration probability, not a guaranteed finite-run quota.</summary>
    public double ExplorationProbability { get; }
    /// <summary>Gets the complete charged proposal-pool size, including unselected candidates.</summary>
    public int PoolSize { get; }
    /// <summary>Gets all returned well-formed predictions, including those rejected for unfamiliarity.</summary>
    public IReadOnlyList<EvolutionSurrogatePrediction> Predictions { get; }
}
