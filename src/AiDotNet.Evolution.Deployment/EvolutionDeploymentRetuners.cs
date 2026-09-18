using AiDotNet.Evolution.AutoML;
using MapElitesAutoMLOptions = AiDotNet.Evolution.AutoML.MapElitesAutoMLOptions;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Deployment;

/// <summary>Private, bounded consumer searches that return candidates, never activate production deployments.</summary>
public static class EvolutionDeploymentRetuners
{
    /// <summary>Runs the real program evolution engine with bounded attempts/proposals and fresh search correctness checks.</summary>
    /// <remarks>
    /// Each options factory must supply fresh CustomVariation and CustomFitnessEvaluator instances and bounded seeds.
    /// This in-memory retuner uses an eight-cell length archive, no engine cache/checkpoint, and the request's engine limits.
    /// Search correctness/fitness must be separate from the lifecycle's independent deployment validation protocol.
    /// Custom providers own nested work, containment and any monetary ledger; resource-accounted options are refused here.
    /// </remarks>
    public static Func<EvolutionDeploymentRetuneRequest, CancellationToken, Task<EvolutionDeployableArtifact>> Program(
        Func<EvolutionDeploymentEnvelope, ProgramDeploymentSearchOptions> optionsFactory,
        Func<EvolutionDeploymentEnvelope, IProgramFitnessEvaluator> correctnessFactory,
        EvolutionOptimizationDirection direction)
    {
        if (optionsFactory is null) throw new ArgumentNullException(nameof(optionsFactory));
        if (correctnessFactory is null) throw new ArgumentNullException(nameof(correctnessFactory));
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        return async (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            var options = (optionsFactory(request.Envelope) ?? throw new InvalidOperationException("No private program search options.")).Clone();
            if (options.ResourceAccounting is not null) throw new NotSupportedException("Resource-accounted retuning requires a separately coordinated driver.");
            if (options.SeedPrograms.Count == 0 || options.SeedPrograms.Count > request.MaximumProposals)
                throw new ArgumentException("Program retuning requires a bounded nonempty seed set.");
            var variation = options.CustomVariation ?? throw new ArgumentException("A private program variation operator is required.");
            var fitness = options.CustomFitnessEvaluator ?? throw new ArgumentException("A private program search fitness evaluator is required.");
            var correctness = correctnessFactory(request.Envelope) ?? throw new ArgumentException("Independent search correctness is required.");
            var evaluator = new CorrectnessGatedProgramFitnessEvaluator(correctness, fitness);
            var task = new RetuneTask(evaluator, options.MaxProgramChars, options.Language);
            var engine = new EvolutionEngine<ProgramGenome>(task, variation,
                _ => new MapElitesArchive<ProgramGenome>(new[]
                { new EvolutionDescriptorDefinition("deployment_length", 0, options.MaxProgramChars + 1d, 8) }, direction),
                new EvolutionEngineOptions
                {
                    RunId = "deployment-retune-" + Guid.NewGuid().ToString("N"),
                    Seed = options.Engine.Seed,
                    MaxEvaluationAttempts = request.MaximumEvaluations,
                    MaxProposals = request.MaximumProposals,
                    MaxGenerations = request.MaximumProposals,
                    MaxDegreeOfParallelism = 1,
                    ProposalBatchSize = 1,
                    MaxRetries = 0,
                    EnableEvaluationCache = false,
                    TimeLimit = request.Timeout,
                    // Keep uncooperative evaluation attached to this private search. The outer lifecycle
                    // bounds waiting without releasing its admission until the search actually settles.
                    EvaluationTimeout = request.Timeout,
                    EvaluationGracePeriod = null
                }, genomeCodec: new ProgramGenomeCodec());
            var result = await engine.RunAsync(options.CreateSeedGenomes(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var winner = result.Best ?? throw new InvalidOperationException("Bounded program search produced no valid winner.");
            return EvolutionDeployableArtifact.FromProgram(winner.Candidate.CanonicalGenome.Genome, request.Envelope);
        };
    }

    /// <summary>Runs the real MAP-Elites AutoML engine within admitted training/proposal limits and packages the trained winner.</summary>
    /// <remarks>Factories supply training/search-validation data only; keep deployment holdout data in the lifecycle evaluator.</remarks>
    public static Func<EvolutionDeploymentRetuneRequest, CancellationToken, Task<EvolutionDeployableArtifact>> AutoML<T, TInput, TOutput>(
        Func<EvolutionDeploymentEnvelope, (TInput TrainingInputs, TOutput TrainingTargets, TInput ValidationInputs, TOutput ValidationTargets)> dataFactory,
        Action<MapElitesAutoML<T, TInput, TOutput>, EvolutionDeploymentEnvelope> configure,
        string serializationVersion, MapElitesAutoMLOptions? options = null)
    {
        if (dataFactory is null) throw new ArgumentNullException(nameof(dataFactory));
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        DeploymentEncoding.RequireLabel(serializationVersion, 256);
        MapElitesAutoMLOptions frozen = (options ?? new MapElitesAutoMLOptions()).SnapshotAndValidate();
        return async (request, token) =>
        {
            token.ThrowIfCancellationRequested();
            MapElitesAutoMLOptions effective = frozen.SnapshotAndValidate();
            effective.InitialPopulationSize = Math.Min(effective.InitialPopulationSize, request.MaximumEvaluations);
            effective.MaxProposalMultiplier = Math.Min(effective.MaxProposalMultiplier, request.MaximumProposals / request.MaximumEvaluations);
            using var search = new MapElitesAutoML<T, TInput, TOutput>(effective);
            configure(search, request.Envelope);
            // Apply admitted limits after caller configuration; the sealed engine snapshots its own options.
            search.TrialLimit = request.MaximumEvaluations;
            search.TimeLimit = request.Timeout;
            var data = dataFactory(request.Envelope);
            var winner = await search.SearchAsync(data.TrainingInputs, data.TrainingTargets,
                data.ValidationInputs, data.ValidationTargets, request.Timeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return EvolutionDeployableArtifact.FromModel(winner, serializationVersion, request.Envelope);
        };
    }

    private sealed class RetuneTask(IProgramFitnessEvaluator evaluator, int maximumChars, ProgramLanguage language) : IEvolutionTask<ProgramGenome>
    {
        public string Id => "deployment-program-retune";
        public string VersionHash => EvolutionHash.Combine(new[] { Id, maximumChars.ToString(System.Globalization.CultureInfo.InvariantCulture), language.ToString() });
        public string EvaluatorVersionHash => evaluator.VersionHash;
        public ValueTask<EvolutionCanonicalGenome<ProgramGenome>> CanonicalizeAsync(ProgramGenome genome, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (genome.Language != language) throw new ArgumentException("Program language differs from the private search contract.");
            if (genome.Source.Length > maximumChars) throw new ArgumentException("Program exceeds the declared source bound.");
            return new(new EvolutionCanonicalGenome<ProgramGenome>(genome.CreateOwnedSnapshot(), genome.Id));
        }
        public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<ProgramGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            var genome = candidate.CanonicalGenome.Genome;
            var result = await evaluator.EvaluateAsync(genome, context, cancellationToken).ConfigureAwait(false);
            var measured = new EvolutionTaskResult(result.Status, result.Quality, result.Direction,
                new Dictionary<string, double> { ["deployment_length"] = genome.NormalizedSource.Length },
                result.Objectives, result.ConstraintViolations, result.CostUnits, result.Diagnostics, result.Metrics, result.Artifacts);
            return result.MeasurementOrigin is { } origin ? measured.WithMeasurementOrigin(origin) : measured;
        }
    }
}
