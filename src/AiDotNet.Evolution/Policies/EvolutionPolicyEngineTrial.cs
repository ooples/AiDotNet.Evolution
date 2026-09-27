using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>Executes declarative recipes through the real engine with fresh, metered task and operator instances.</summary>
[Experimental("AIDEVO002")]
public static class EvolutionPolicyEngineTrial
{
    /// <summary>Creates an independently versioned task trial with fixed normalization and bounded MAP-Elites archives.</summary>
    /// <remarks>
    /// Factories and seed generation must be isolated and must not perform unmetered external work. Disposable factory
    /// results are owned by the trial. Cascaded tasks require a separate coordinated driver and are refused here.
    /// Normalization clips the declared worst quality to zero and target quality to one; never fit these bounds on holdout outcomes.
    /// Producer maxima require backend enforcement. Cooperative engine timeouts do not kill arbitrary provider processes.
    /// </remarks>
    public static EvolutionPolicyTrial Create<TGenome>(string id, string family, string taskVersionHash,
        string evaluatorVersionHash, Func<IEvolutionTask<TGenome>> taskFactory,
        Func<StableRandom, int, IEnumerable<TGenome>> seedFactory,
        IEnumerable<EvolutionPolicyEngineOperator<TGenome>> operators,
        IEnumerable<EvolutionDescriptorDefinition> descriptors, IEvolutionGenomeCodec<TGenome> codec,
        EvolutionOptimizationDirection direction, double worstQuality, double targetQuality,
        decimal maximumEvaluationCostUnits, string costUnitVersionHash)
    {
        PolicyContract.Label(id); PolicyContract.Label(family); PolicyContract.Label(taskVersionHash);
        PolicyContract.Label(evaluatorVersionHash); PolicyContract.Label(costUnitVersionHash);
        Guard.NotNull(taskFactory); Guard.NotNull(seedFactory); Guard.NotNull(operators); Guard.NotNull(descriptors); Guard.NotNull(codec);
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        double span = targetQuality - worstQuality;
        if (double.IsNaN(span) || double.IsInfinity(span) || span == 0 ||
            (direction == EvolutionOptimizationDirection.Maximize ? span < 0 : span > 0))
            throw new ArgumentException("Declare finite, correctly ordered normalization bounds.", nameof(targetQuality));
        if (maximumEvaluationCostUnits < 0 || maximumEvaluationCostUnits > EvolutionResources.MaximumAmount)
            throw new ArgumentOutOfRangeException(nameof(maximumEvaluationCostUnits));
        var definitions = operators.Take(17).ToArray();
        if (definitions.Length is < 1 or > 16 || definitions.Any(value => value is null) ||
            definitions.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != definitions.Length)
            throw new ArgumentException("Declare one to sixteen unique operator backends.", nameof(operators));
        definitions = definitions.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        var axes = descriptors.Take(9).ToArray();
        if (axes.Length is < 1 or > 8 || axes.Any(value => value is null))
            throw new ArgumentException("Declare one to eight bounded archive descriptors.", nameof(descriptors));
        long gridCells = 1;
        foreach (var axis in axes)
        {
            gridCells *= axis.BinCount;
            if (gridCells > 65536) throw new ArgumentException("Policy archive grid exceeds 65536 cells.", nameof(descriptors));
        }
        int archiveCapacity = (int)Math.Min(1024, gridCells);
        string archiveHash = new MapElitesArchive<TGenome>(axes, direction, capacity: archiveCapacity, maximumGridCells: 65536).DefinitionHash;
        string protocol = EvolutionHash.Combine(new[] { "engine-policy-trial-v1", id, family, taskVersionHash, evaluatorVersionHash,
            codec.Id, codec.VersionHash, archiveHash, costUnitVersionHash, EvolutionHash.EncodeDouble(worstQuality),
            EvolutionHash.EncodeDouble(targetQuality), maximumEvaluationCostUnits.ToString(CultureInfo.InvariantCulture) }
            .Concat(definitions.SelectMany(value => new[] { value.Id, value.VersionHash,
                EvolutionHash.Combine(value.MaximumProposalResources.Amounts.SelectMany(resource => new[]
                    { resource.Key, resource.Value.ToString(CultureInfo.InvariantCulture) })) })));

        return new EvolutionPolicyTrial(id, family, protocol, costUnitVersionHash, async (policy, budget, rootSeed, token) =>
        {
            if (!policy.OperatorWeights.Keys.SequenceEqual(definitions.Select(value => value.Id)))
                throw new ArgumentException("Policy operator names differ from this task's pinned contract.", nameof(policy));
            foreach (var definition in definitions.Where(value => policy.OperatorWeights[value.Id] > 0))
                if (definition.MaximumProposalResources.Amounts.Keys.Any(key => !budget.MaximumResources.Amounts.ContainsKey(key)))
                    throw new ArgumentException("Inner budget does not declare every active producer resource.", nameof(budget));
            var spent = budget.MaximumResources.Amounts.ToDictionary(value => value.Key, _ => 0m, StringComparer.Ordinal);
            var segments = new List<object>();
            long evaluations = 0, proposals = 0, restarts = 0;
            double? bestQuality = null;
            bool valid = true, unknown = false;
            var seeds = new StableRandom(rootSeed);

            for (int segmentIndex = 0; segmentIndex <= budget.MaximumRestarts; segmentIndex++)
            {
                if (token.IsCancellationRequested) { valid = false; break; }
                int remainingEvaluations = budget.MaximumEvaluations - checked((int)evaluations);
                int remainingProposals = budget.MaximumProposals - checked((int)proposals);
                if (remainingEvaluations <= 0 || remainingProposals <= 0) break;
                int allowedEvaluations = policy.RestartAfterEvaluations > 0 && segmentIndex < budget.MaximumRestarts
                    ? Math.Min(policy.RestartAfterEvaluations, remainingEvaluations) : remainingEvaluations;
                int allowedProposals = (int)Math.Min(remainingProposals,
                    Math.Max(allowedEvaluations, (long)remainingProposals * allowedEvaluations / remainingEvaluations));
                ulong seed = seeds.NextUInt64();
                var limits = new EvolutionResources(budget.MaximumResources.Amounts.Select(value =>
                    new KeyValuePair<string, decimal>(value.Key, Math.Max(0, value.Value - spent[value.Key]))));
                var ledger = new EvolutionResourceLedger("policy-segment-" + segmentIndex.ToString(CultureInfo.InvariantCulture),
                    limits, retainedReceiptLimit: 0, maximumOperations: Math.Min(1_000_000, allowedEvaluations + allowedProposals));
                bool reused = false;
                var result = await PolicyOwnedResources.RunAsync(async owned =>
                {
                    var task = taskFactory() ?? throw new InvalidOperationException("Policy task factory returned no task.");
                    if (task is IDisposable disposable) owned.Add(disposable);
                    if (task.Id != id || task.VersionHash != taskVersionHash || task.EvaluatorVersionHash != evaluatorVersionHash)
                        throw new InvalidOperationException("Policy task factory changed its pinned task protocol.");
                    if (task is ICascadeEvolutionTask<TGenome>) throw new NotSupportedException("Cascaded policy trials require a separately coordinated driver.");
                    var fresh = new PolicyFreshTask<TGenome>(task);
                    var metered = new ResourceMeteredEvolutionTask<TGenome>(fresh, ledger, new[] { maximumEvaluationCostUnits });
                    var mixture = new PolicyEngineMixture<TGenome>(policy, definitions, ledger, costUnitVersionHash, owned);
                    var initial = (seedFactory(new StableRandom(seed), Math.Min(allowedEvaluations, allowedProposals))
                        ?? throw new InvalidOperationException("No policy trial seeds.")).Take(Math.Min(allowedEvaluations, allowedProposals) + 1).ToArray();
                    if (initial.Length == 0 || initial.Length > Math.Min(allowedEvaluations, allowedProposals))
                        throw new InvalidOperationException("Policy seed factory exceeded its admitted allowance.");
                    var selection = new PolicyEngineSelection<TGenome>(policy, budget.MaximumProposals, proposals + initial.Length);
                    var engine = new EvolutionEngine<TGenome>(metered, mixture,
                        _ => new MapElitesArchive<TGenome>(axes, direction, capacity: archiveCapacity, maximumGridCells: 65536),
                        new EvolutionEngineOptions
                        {
                            RunId = "policy-" + policy.Id + "-" + segmentIndex.ToString(CultureInfo.InvariantCulture),
                            Seed = seed,
                            MaxEvaluationAttempts = allowedEvaluations,
                            MaxProposals = allowedProposals,
                            MaxGenerations = allowedProposals,
                            MaxDegreeOfParallelism = 1,
                            ProposalBatchSize = 1,
                            MaxRetries = 0,
                            EnableEvaluationCache = false,
                            FailurePolicy = EvolutionFailurePolicy.FailFast,
                            TimeLimit = budget.Timeout,
                            EvaluationTimeout = budget.Timeout,
                            EvaluationGracePeriod = null,
                            InspirationCount = policy.Context == EvolutionPolicyContext.ParentOnly ? 0 : 2,
                            Artifacts = new EvolutionArtifactOptions
                            {
                                Enabled = policy.Context == EvolutionPolicyContext.FeedbackAndInspirations,
                                MaxArtifactBytes = 4096,
                                MaxArtifactsPerEvaluation = 4,
                                MaxBytesPerEvaluation = 16384,
                                MaxPendingCandidates = 64,
                                SanitizeSecrets = true,
                                DeliverToNextProposal = true
                            }
                        }, selection: selection, genomeCodec: codec);
                    var completed = await engine.RunAsync(initial, token).ConfigureAwait(false);
                    reused = fresh.SawReusedMeasurement;
                    return completed;
                }).ConfigureAwait(false);
                var snapshot = ledger.Snapshot();
                foreach (var amount in snapshot.Spent) spent[amount.Key] += amount.Value;
                evaluations += result.Counters.EvaluationAttempts; proposals += result.Counters.Proposals;
                if (segmentIndex > 0) restarts++;
                unknown |= snapshot.Unknown > 0;
                valid &= !token.IsCancellationRequested && snapshot.Unknown == 0 && snapshot.Denied == 0 && !snapshot.MaximumViolated && !reused;
                valid &= !result.Counters.StatusCounts.Any(value => value.Value > 0 && value.Key is
                    EvolutionEvaluationStatus.Failed or EvolutionEvaluationStatus.Canceled or EvolutionEvaluationStatus.TimedOut);
                double? quality = result.Best?.Evaluation.Quality;
                if (quality.HasValue && (!bestQuality.HasValue || (direction == EvolutionOptimizationDirection.Maximize
                    ? quality.Value > bestQuality.Value : quality.Value < bestQuality.Value))) bestQuality = quality;
                segments.Add(new
                {
                    Restart = segmentIndex,
                    Seed = seed,
                    result.StateHash,
                    result.StopReason,
                    result.Counters,
                    BestQuality = quality,
                    snapshot.Spent,
                    snapshot.Admitted,
                    snapshot.Settled,
                    snapshot.Denied,
                    snapshot.Unknown,
                    snapshot.MaximumViolated,
                    ReusedMeasurement = reused
                });
                if (!valid || policy.RestartAfterEvaluations == 0) break;
            }
            valid &= bestQuality.HasValue;
            string evidence = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                TaskId = id,
                Family = family,
                Protocol = protocol,
                PolicyId = policy.Id,
                Seed = rootSeed,
                WorstQuality = worstQuality,
                TargetQuality = targetQuality,
                Direction = direction,
                BestQuality = bestQuality,
                Segments = segments
            }, EvolutionJson.Compact);
            double? utility = bestQuality.HasValue ? Normalize(bestQuality.Value, worstQuality, targetQuality) : null;
            return new EvolutionPolicyObservation(utility, PolicyResources.WithCounters(new EvolutionResources(spent), evaluations, proposals, restarts),
                EvolutionHash.Compute(evidence), valid: valid, diagnostic: valid ? null : "Inner trial failed freshness, resource or quality checks.",
                evidenceJson: evidence, unknownConsumption: unknown);
        });
    }

    private static double Normalize(double value, double worst, double target)
    {
        if (target > worst) { if (value <= worst) return 0; if (value >= target) return 1; }
        else { if (value >= worst) return 0; if (value <= target) return 1; }
        return (value - worst) / (target - worst);
    }
}

internal sealed class PolicyFreshTask<TGenome>(IEvolutionTask<TGenome> inner) : IEvolutionTask<TGenome>
{
    internal bool SawReusedMeasurement { get; private set; }
    public string Id => inner.Id;
    public string VersionHash => inner.VersionHash;
    public string EvaluatorVersionHash => inner.EvaluatorVersionHash;
    public ValueTask<EvolutionCanonicalGenome<TGenome>> CanonicalizeAsync(TGenome genome, CancellationToken cancellationToken = default) =>
        inner.CanonicalizeAsync(genome, cancellationToken);
    public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TGenome> candidate, EvolutionEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.EvaluateAsync(candidate, context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Policy evaluator returned no receipt.");
        SawReusedMeasurement |= result.MeasurementOrigin is { Kind: not EvolutionMeasurementOriginKind.Measured };
        return result;
    }
}
