using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Optional stateless, cost-metered proposal-pool acquisition with explicit uniform exploration and fallback.</summary>
/// <typeparam name="TGenome">The immutable genome representation.</typeparam>
/// <remarks>All observations and randomness are explicit inputs. Backends must fit detached deterministic models;
/// callers must checkpoint the input observation set and proposal generator state. This is not an engine observer
/// or a hidden learning side channel. Only true evaluations may become training records or archive fitness.
/// Serialize calls sharing a trainer unless its backend explicitly supports concurrent fitting.</remarks>
public sealed class EvolutionSurrogateSelector<TGenome>
{
    private readonly IEvolutionSurrogateTrainer<TGenome> _trainer;
    private readonly EvolutionResourceLedger _ledger;
    private readonly EvolutionResources _proposalMaximum, _trainingMaximum, _inferenceMaximum;
    private readonly string _taskVersion, _evaluatorVersion, _trainerVersion;
    private readonly int _minimumSamples;
    private readonly double _exploration, _optimism;
    private readonly EvolutionOptimizationDirection _direction;

    /// <summary>Creates a selector with declared per-stage maxima, frozen provenance and a nonzero exploration allocation.</summary>
    public EvolutionSurrogateSelector(IEvolutionSurrogateTrainer<TGenome> trainer, EvolutionResourceLedger ledger,
        string taskVersionHash, string evaluatorVersionHash, EvolutionResources proposalMaximum,
        EvolutionResources trainingMaximum, EvolutionResources inferenceMaximum, int minimumSamples = 8,
        double explorationProbability = 0.2, double optimism = 1,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(trainer); Guard.NotNull(ledger); Guard.NotNullOrWhiteSpace(taskVersionHash); Guard.NotNullOrWhiteSpace(evaluatorVersionHash);
        Guard.NotNull(proposalMaximum); Guard.NotNull(trainingMaximum); Guard.NotNull(inferenceMaximum); Guard.NotNullOrWhiteSpace(trainer.VersionHash);
        if (minimumSamples is < 2 or > 256) throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        if (!EvolutionDescriptorDefinition.IsFinite(explorationProbability) || explorationProbability < 0.05 || explorationProbability > 1 ||
            !EvolutionDescriptorDefinition.IsFinite(optimism) || optimism < 0 || optimism > 100)
            throw new ArgumentOutOfRangeException(nameof(explorationProbability));
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        foreach (EvolutionResources maximum in new[] { proposalMaximum, trainingMaximum, inferenceMaximum })
            if (maximum.Amounts.Keys.Any(name => !ledger.Limits.Amounts.ContainsKey(name)))
                throw new ArgumentException("All stage resource dimensions must exist in the ledger.", nameof(ledger));
        _trainer = trainer; _ledger = ledger; _taskVersion = taskVersionHash; _evaluatorVersion = evaluatorVersionHash;
        _trainerVersion = trainer.VersionHash; _proposalMaximum = proposalMaximum; _trainingMaximum = trainingMaximum; _inferenceMaximum = inferenceMaximum;
        _minimumSamples = minimumSamples; _exploration = explorationProbability; _optimism = optimism; _direction = direction;
        VersionHash = EvolutionHash.Combine(new[] { "surrogate-selector-v3-distinct-original-samples", _trainerVersion, _taskVersion, _evaluatorVersion,
            minimumSamples.ToString(CultureInfo.InvariantCulture), Bits(explorationProbability), Bits(optimism), direction.ToString(),
            ResourceHash(proposalMaximum), ResourceHash(trainingMaximum), ResourceHash(inferenceMaximum) });
    }
    /// <summary>Gets the immutable backend, provenance, acquisition and metering identity.</summary>
    public string VersionHash { get; }

    /// <summary>Charges all generated proposals, optionally fits/scores, then selects a candidate that still needs true evaluation.</summary>
    /// <remarks>Proposal failures propagate because there is no valid fallback pool. Model failures retain their
    /// charges and use uniform fallback. Cancellation and declared maximum violations propagate; they are not success.</remarks>
    public async ValueTask<EvolutionSurrogateSelection<TGenome>> SelectAsync(string operationId,
        IReadOnlyList<EvolutionSurrogateObservation<TGenome>> observations, StableRandom random,
        Func<CancellationToken, ValueTask<EvolutionResourceResult<IReadOnlyList<EvolutionCanonicalGenome<TGenome>>>>> proposePool,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(operationId); Guard.NotNull(observations); Guard.NotNull(random); Guard.NotNull(proposePool);
        if (operationId.Length > 128 || operationId.Any(char.IsControl)) throw new ArgumentException("Bounded printable operation identity required.", nameof(operationId));
        cancellationToken.ThrowIfCancellationRequested();
        var training = EvolutionCollection.CopyBounded(observations, 256, nameof(observations));
        if (training.Any(value => value is null || value.Evaluation.TaskVersionHash != _taskVersion ||
                value.Evaluation.EvaluatorVersionHash != _evaluatorVersion || value.Evaluation.Direction != _direction) ||
            training.Select(value => value.Evaluation.GenomeId + "/" + value.Evaluation.EvaluationId.ToString(CultureInfo.InvariantCulture)).Distinct().Count() != training.Length)
            throw new ArgumentException("Observations must have matching provenance/direction and unique measured identities.", nameof(observations));
        var originalSamples = new HashSet<string>(StringComparer.Ordinal);
        foreach (EvolutionSurrogateObservation<TGenome> observation in training)
            foreach (string sample in observation.Evaluation.MeasurementOrigin?.SampleIds ?? Array.Empty<string>())
                if (!originalSamples.Add(sample))
                    throw new ArgumentException("Declared original sample identities overlap between training records.", nameof(observations));
        if (_trainer.VersionHash != _trainerVersion) throw new InvalidOperationException("Trainer semantic identity changed.");
        string trainingIdentity = EvolutionHash.Combine(training.Select(value => EvolutionHash.Combine(new[] { value.Candidate.Id,
            value.Evaluation.EvaluationId.ToString(CultureInfo.InvariantCulture), Bits(value.Evaluation.Quality!.Value), _taskVersion, _evaluatorVersion,
            value.Evaluation.MeasurementOrigin is { } origin ? EvolutionHash.Combine(new[] { "original-measurement-v1", origin.ToJson() }) : "unreported-samples" })));
        string identity = EvolutionHash.Combine(new[] { VersionHash, operationId, trainingIdentity });
        var proposal = await Meter(identity + "/proposals", EvolutionResourceStage.Proposal, _proposalMaximum, proposePool, cancellationToken).ConfigureAwait(false);
        if (proposal.Outcome != EvolutionResourceOutcome.Completed) throw new InvalidOperationException("Proposal pool did not complete.");
        var pool = EvolutionCollection.CopyBounded(proposal.Value, 64, nameof(proposePool));
        if (pool.Length == 0 || pool.Any(candidate => candidate is null || candidate.Id.Length > 256) || pool.Select(candidate => candidate.Id).Distinct().Count() != pool.Length)
            throw new ArgumentException("Require 1..64 uniquely identified canonical candidates.", nameof(proposePool));
        int fallback = random.NextInt(pool.Length);
        bool explore = random.NextDouble() < _exploration;
        string? modelVersion = null;
        EvolutionSurrogateValidationReport? validationReport = null;
        EvolutionSurrogatePrediction[] predictions = Array.Empty<EvolutionSurrogatePrediction>();
        EvolutionSurrogateSelection<TGenome> Result(EvolutionSurrogateSelectionReason reason, int index = -1) =>
            new(pool[index < 0 ? fallback : index], reason, identity, trainingIdentity, modelVersion, _exploration, pool.Length, predictions, validationReport);
        if (training.Length < _minimumSamples) return Result(EvolutionSurrogateSelectionReason.InsufficientData);
        if (explore) return Result(EvolutionSurrogateSelectionReason.Exploration);
        IEvolutionSurrogateModel<TGenome> model;
        try
        {
            var fitted = await Meter(identity + "/training", EvolutionResourceStage.SurrogateTraining, _trainingMaximum,
                token => _trainer.FitAsync(Array.AsReadOnly(training), token), cancellationToken).ConfigureAwait(false);
            if (fitted.Outcome != EvolutionResourceOutcome.Completed || fitted.Value is null) return Result(EvolutionSurrogateSelectionReason.TrainingFailure);
            model = fitted.Value; modelVersion = model.VersionHash;
            if (string.IsNullOrWhiteSpace(modelVersion) || modelVersion!.Length > 256) return Result(EvolutionSurrogateSelectionReason.TrainingFailure);
            validationReport = (model as IEvolutionSurrogateDiagnosticModel)?.ValidationReport;
            if (!model.IsReliable) return Result(EvolutionSurrogateSelectionReason.UnreliableModel);
        }
        catch (EvolutionResourceBudgetException) { return Result(EvolutionSurrogateSelectionReason.TrainingBudgetDenied); }
        catch (Exception exception) when (CanFallback(exception)) { return Result(EvolutionSurrogateSelectionReason.TrainingFailure); }
        try
        {
            var inferred = await Meter(identity + "/inference", EvolutionResourceStage.SurrogateInference, _inferenceMaximum,
                token => model.PredictAsync(Array.AsReadOnly(pool), token), cancellationToken).ConfigureAwait(false);
            if (inferred.Outcome != EvolutionResourceOutcome.Completed || inferred.Value is null) return Result(EvolutionSurrogateSelectionReason.InferenceFailure);
            predictions = EvolutionCollection.CopyBounded(inferred.Value, 64, nameof(inferred));
        }
        catch (EvolutionResourceBudgetException) { return Result(EvolutionSurrogateSelectionReason.InferenceBudgetDenied); }
        catch (Exception exception) when (CanFallback(exception)) { return Result(EvolutionSurrogateSelectionReason.InferenceFailure); }
        if (predictions.Length != pool.Length || predictions.Any(value => value is null) ||
            predictions.Select(value => value.GenomeId).Distinct().Count() != pool.Length ||
            predictions.Any(value => !pool.Any(candidate => candidate.Id == value.GenomeId)))
        { predictions = Array.Empty<EvolutionSurrogatePrediction>(); return Result(EvolutionSurrogateSelectionReason.InvalidPredictions); }
        if (predictions.Any(value => !value.WithinTrainingDomain)) return Result(EvolutionSurrogateSelectionReason.UnfamiliarPool);
        int best = 0; double bestScore = double.NegativeInfinity;
        for (int index = 0; index < pool.Length; index++)
        {
            var prediction = predictions.Single(value => value.GenomeId == pool[index].Id);
            double score = (_direction == EvolutionOptimizationDirection.Maximize ? prediction.Mean : -prediction.Mean) + _optimism * prediction.Uncertainty;
            if (!EvolutionDescriptorDefinition.IsFinite(score)) return Result(EvolutionSurrogateSelectionReason.InvalidPredictions);
            if (score > bestScore) { best = index; bestScore = score; }
        }
        return Result(EvolutionSurrogateSelectionReason.Acquisition, best);
    }

    private bool CanFallback(Exception exception) => exception is not (OperationCanceledException or OutOfMemoryException) && !_ledger.Snapshot().MaximumViolated;
    private async ValueTask<EvolutionResourceResult<T>> Meter<T>(string identity, EvolutionResourceStage stage, EvolutionResources maximum,
        Func<CancellationToken, ValueTask<EvolutionResourceResult<T>>> work, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var reservation = _ledger.TryReserve("surrogate/" + identity, stage, maximum, maximum)
            ?? throw new EvolutionResourceBudgetException(identity);
        EvolutionResourceResult<T> result;
        try { result = await work(token).ConfigureAwait(false) ?? throw new InvalidOperationException("No stage receipt returned."); }
        catch (EvolutionResourceBudgetException exception)
        {
            // Only this selector's pre-dispatch denial may use the BudgetDenied classification. A nested backend
            // denial happened after dispatch; absent a receipt, its reserved maximum remains unknown work.
            throw new InvalidOperationException("The dispatched surrogate backend reported a nested budget denial.", exception);
        }
        reservation.Complete(result.Actual, result.Outcome);
        if (_ledger.Snapshot().MaximumViolated) throw new InvalidOperationException("A declared surrogate stage maximum was exceeded.");
        token.ThrowIfCancellationRequested();
        return result;
    }
    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value).ToString("x16", CultureInfo.InvariantCulture);
    private static string ResourceHash(EvolutionResources resources) => EvolutionHash.Combine(resources.Amounts.Select(pair => pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture)));
}
