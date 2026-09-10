using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>A reusable mutation operator for mixed and conditional parameter spaces.</summary>
public sealed class SearchSpaceMutation : IVariationOperator<EvolutionSearchGenome>
{
    private readonly EvolutionSearchSpace _space;
    private readonly double _probability;
    private readonly double _scale;
    /// <summary>Creates normalized Gaussian numeric mutation and categorical resampling, with at least one active domain selected.</summary>
    public SearchSpaceMutation(EvolutionSearchSpace space, double probability = 0.2, double scale = 0.1)
    {
        Guard.NotNull(space);
        if (!EvolutionDescriptorDefinition.IsFinite(probability) || probability < 0 || probability > 1) throw new ArgumentOutOfRangeException(nameof(probability));
        if (!EvolutionDescriptorDefinition.IsFinite(scale) || scale <= 0 || scale > 1) throw new ArgumentOutOfRangeException(nameof(scale));
        _space = space; _probability = probability; _scale = scale;
        Id = "typed-mutation-" + probability.ToString("R", CultureInfo.InvariantCulture) + "-" + scale.ToString("R", CultureInfo.InvariantCulture);
        VersionHash = EvolutionHash.Combine(new[] { "typed-mutation-v1", space.VersionHash, Id });
    }
    /// <inheritdoc/>
    public string Id { get; }
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <inheritdoc/>
    public ValueTask<EvolutionSearchGenome> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context); cancellationToken.ThrowIfCancellationRequested();
        return new(Mutate(_space, context.Parent.Candidate.CanonicalGenome.Genome, context.Random, _probability, _scale));
    }

    internal static EvolutionSearchGenome Mutate(EvolutionSearchSpace space, EvolutionSearchGenome parent, StableRandom random, double probability, double scale)
    {
        parent = space.Validate(parent);
        string forced = parent.Values.Keys.ElementAt(random.NextInt(parent.Values.Count));
        var result = new Dictionary<string, EvolutionParameterValue>(StringComparer.Ordinal);
        foreach (EvolutionParameter parameter in space.Parameters)
        {
            if (!parameter.IsActive(result)) continue;
            if (!parent.Values.TryGetValue(parameter.Name, out EvolutionParameterValue? value)) value = parameter.Sample(random);
            else if (parameter.Name == forced || random.NextDouble() < probability)
            {
                if (parameter.Kind == EvolutionParameterKind.Categorical)
                {
                    if (parameter.Categories.Count > 1)
                    {
                        string[] alternatives = parameter.Categories.Where(category => category != value.Category).ToArray();
                        value = EvolutionParameterValue.Categorical(alternatives[random.NextInt(alternatives.Length)]);
                    }
                }
                else
                {
                    double step = scale * StandardNormal(random);
                    if (parameter.Kind == EvolutionParameterKind.Integer && parameter.Maximum > parameter.Minimum)
                    {
                        double minimumStep = 1 / (parameter.Maximum - parameter.Minimum);
                        if (Math.Abs(step) < minimumStep) step = step < 0 ? -minimumStep : minimumStep;
                    }
                    value = parameter.FromNormalized(parameter.Normalize(value) + step);
                }
            }
            result.Add(parameter.Name, value);
        }
        return space.CreateGenome(result);
    }

    internal static double StandardNormal(StableRandom random) => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}

/// <summary>Uniform crossover respecting conditional activation and independently owned offspring.</summary>
public sealed class SearchSpaceCrossover : IVariationOperator<EvolutionSearchGenome>
{
    private readonly EvolutionSearchSpace _space;
    /// <summary>Creates crossover; when no inspiration is supplied it returns an owned copy of the parent.</summary>
    public SearchSpaceCrossover(EvolutionSearchSpace space) { Guard.NotNull(space); _space = space; VersionHash = EvolutionHash.Combine(new[] { "typed-crossover-v1", space.VersionHash }); }
    /// <inheritdoc/>
    public string Id => "typed-uniform-crossover";
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <inheritdoc/>
    public ValueTask<EvolutionSearchGenome> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context); cancellationToken.ThrowIfCancellationRequested();
        EvolutionSearchGenome parent = _space.Validate(context.Parent.Candidate.CanonicalGenome.Genome);
        if (context.Inspirations.Count == 0) return new(parent);
        EvolutionSearchGenome donor = _space.Validate(context.Inspirations[context.Random.NextInt(context.Inspirations.Count)].Candidate.CanonicalGenome.Genome);
        var result = new Dictionary<string, EvolutionParameterValue>(StringComparer.Ordinal);
        foreach (EvolutionParameter parameter in _space.Parameters)
        {
            if (!parameter.IsActive(result)) continue;
            EvolutionSearchGenome source = context.Random.NextDouble() < 0.5 ? parent : donor;
            result.Add(parameter.Name, source.Values.TryGetValue(parameter.Name, out EvolutionParameterValue? value) ? value : parameter.Sample(context.Random));
        }
        return new(_space.CreateGenome(result));
    }
}

/// <summary>Independent uniform/log-uniform restart sampling for a declared parameter space.</summary>
public sealed class SearchSpaceRestart : IVariationOperator<EvolutionSearchGenome>
{
    private readonly EvolutionSearchSpace _space;
    /// <summary>Creates the restart operator.</summary>
    public SearchSpaceRestart(EvolutionSearchSpace space) { Guard.NotNull(space); _space = space; VersionHash = EvolutionHash.Combine(new[] { "typed-restart-v1", space.VersionHash }); }
    /// <inheritdoc/>
    public string Id => "typed-restart";
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <inheritdoc/>
    public ValueTask<EvolutionSearchGenome> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context); cancellationToken.ThrowIfCancellationRequested();
        _space.Validate(context.Parent.Candidate.CanonicalGenome.Genome);
        return new(_space.Sample(context.Random));
    }
}

/// <summary>Bounded, resource-metered local search that never replaces an incumbent with an invalid trial.</summary>
/// <remarks>The supplied objective must use search-visible checks only and return truthful CostUnits. Every initial
/// and trial measurement is charged. The outer engine must still evaluate the returned candidate; a local-search
/// score is not independent confirmation or permission to deploy.</remarks>
public sealed class SearchSpaceLocalRefiner : ICandidateRefiner<EvolutionSearchGenome>
{
    private readonly EvolutionSearchSpace _space;
    private readonly EvolutionResourceLedger _ledger;
    private readonly Func<EvolutionSearchGenome, CancellationToken, ValueTask<EvolutionTaskResult>> _evaluate;
    private readonly int _steps;
    private readonly double _scale;
    private readonly decimal _maximum;
    private readonly EvolutionOptimizationDirection _direction;

    /// <summary>Creates a local refiner with at most steps + 1 objective calls, including the initial point.</summary>
    public SearchSpaceLocalRefiner(EvolutionSearchSpace space, EvolutionResourceLedger ledger,
        Func<EvolutionSearchGenome, CancellationToken, ValueTask<EvolutionTaskResult>> evaluate,
        string evaluatorVersionHash, decimal maximumCostPerEvaluation, int steps = 8, double scale = 0.05,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(space); Guard.NotNull(ledger); Guard.NotNull(evaluate); Guard.NotNullOrWhiteSpace(evaluatorVersionHash);
        if (steps < 1 || steps > 1024) throw new ArgumentOutOfRangeException(nameof(steps));
        if (!EvolutionDescriptorDefinition.IsFinite(scale) || scale <= 0 || scale > 1) throw new ArgumentOutOfRangeException(nameof(scale));
        if (maximumCostPerEvaluation < 0 || maximumCostPerEvaluation > EvolutionResources.MaximumAmount) throw new ArgumentOutOfRangeException(nameof(maximumCostPerEvaluation));
        if (!ledger.Limits.Amounts.ContainsKey("cost_units")) throw new ArgumentException("The ledger must declare cost_units.", nameof(ledger));
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        _space = space; _ledger = ledger; _evaluate = evaluate; _steps = steps; _scale = scale; _maximum = maximumCostPerEvaluation; _direction = direction;
        VersionHash = EvolutionHash.Combine(new[] { "typed-local-refiner-v1", space.VersionHash, evaluatorVersionHash,
            steps.ToString(CultureInfo.InvariantCulture), scale.ToString("R", CultureInfo.InvariantCulture), maximumCostPerEvaluation.ToString(CultureInfo.InvariantCulture), direction.ToString() });
    }
    /// <inheritdoc/>
    public string Id => "typed-local-refiner";
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <inheritdoc/>
    public async ValueTask<EvolutionSearchGenome> RefineAsync(EvolutionSearchGenome genome, EvolutionRefinementContext context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context); cancellationToken.ThrowIfCancellationRequested();
        EvolutionSearchGenome best = _space.Validate(genome);
        double? bestQuality = null;
        var reserve = EvolutionResources.Of("cost_units", _maximum);
        for (int step = 0; step <= _steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EvolutionSearchGenome candidate = step == 0 ? best : SearchSpaceMutation.Mutate(_space, best, context.Random, 0.2, _scale);
            string id = "refinement/" + context.EvaluationId.ToString(CultureInfo.InvariantCulture) + "/sample/" + step.ToString(CultureInfo.InvariantCulture);
            EvolutionTaskResult result;
            try
            {
                result = await EvolutionResourceWork.RunAsync(_ledger, id, EvolutionResourceStage.Refinement, reserve, reserve, async token =>
                {
                    EvolutionTaskResult measured = await _evaluate(candidate, token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The refinement evaluator returned no receipt.");
                    return new EvolutionResourceResult<EvolutionTaskResult>(measured,
                        EvolutionResources.Of("cost_units", checked((decimal)measured.CostUnits)),
                        measured.Status == EvolutionEvaluationStatus.Completed ? EvolutionResourceOutcome.Completed : EvolutionResourceOutcome.Failed);
                }, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (EvolutionResourceBudgetException) { break; }
            if (result.Status != EvolutionEvaluationStatus.Completed || !result.Quality.HasValue || result.Direction != _direction ||
                result.ConstraintViolations.Any(violation => violation > 0)) continue;
            double quality = result.Quality.Value;
            if (!bestQuality.HasValue || (_direction == EvolutionOptimizationDirection.Maximize ? quality > bestQuality.Value : quality < bestQuality.Value))
            { best = candidate; bestQuality = quality; }
        }
        return best.CreateOwnedSnapshot();
    }
}
