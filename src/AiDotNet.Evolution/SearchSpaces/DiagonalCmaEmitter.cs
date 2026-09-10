using System.Globalization;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>An opt-in positive-weight diagonal CMA-style emitter for continuous parameter spaces.</summary>
/// <remarks>
/// Uses ranked recombination, diagonal covariance adaptation and cumulative step-size control in normalized coordinates.
/// It does not learn cross-coordinate correlations and is not a full-covariance CMA-ME implementation. Bounds are handled
/// by clipping; learning uses the evaluated clipped coordinates. Distributions and incomplete populations are checkpointed.
/// All draws use the supplied proposal stream. Calls/feedback must be serialized, as in the engine's default pipeline.
/// Overlapping populations sample immutable distributions; a completed stale population stays eligible for the archive
/// but cannot update a newer distribution. Statistics expose these discarded learning updates. Batch size one avoids
/// stale populations; aligned population-sized batches also work. No search-quality or default-promotion claim is implied.
/// Algorithm background: Hansen, The CMA Evolution Strategy: A Tutorial, arXiv:1604.00772, positive-weight variant.
/// </remarks>
public sealed class DiagonalCmaEmitter : IOutcomeAwareVariationOperator<EvolutionSearchGenome>
{
    private readonly EvolutionSearchSpace _space;
    private readonly EvolutionOptimizationDirection _direction;
    private readonly double _initialStep;
    private readonly double[] _weights;
    private readonly double _effectiveParents;
    private readonly double _cs, _ds, _cc, _c1, _cmu, _chi;
    private readonly SortedDictionary<long, Cohort> _cohorts = new();
    private readonly Dictionary<long, Pending> _pending = new();
    private Distribution? _state;
    private long _nextCohort;
    private long? _openCohort;
    private long _lastGeneration;
    private long _stale;
    private long _invalid;
    private const int MaximumSamples = 4096;

    /// <summary>Creates an emitter for one to 32 nonconstant, unconditional real/logarithmic domains.</summary>
    /// <param name="space">The continuous parameter space.</param>
    /// <param name="initialStepSize">Initial standard deviation in normalized coordinates, in [1e-12,1].</param>
    /// <param name="populationSize">Four to 256 offspring; zero selects 4 + floor(3 log(dimension)).</param>
    /// <param name="direction">Scalar optimization direction; mismatching evaluations cannot train the emitter.</param>
    public DiagonalCmaEmitter(EvolutionSearchSpace space, double initialStepSize = 0.2, int populationSize = 0,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(space);
        if (space.Parameters.Count > 32 || space.Parameters.Any(parameter => parameter.Conditions.Count != 0 ||
            parameter.Minimum == parameter.Maximum || (parameter.Kind != EvolutionParameterKind.Real && parameter.Kind != EvolutionParameterKind.Logarithmic)))
            throw new ArgumentException("Diagonal CMA requires one to 32 unconditional, nonconstant continuous parameters.", nameof(space));
        if (!EvolutionDescriptorDefinition.IsFinite(initialStepSize) || initialStepSize < 1e-12 || initialStepSize > 1)
            throw new ArgumentOutOfRangeException(nameof(initialStepSize));
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        int n = space.Parameters.Count;
        PopulationSize = populationSize == 0 ? 4 + (int)(3 * Math.Log(n)) : populationSize;
        if (PopulationSize < 4 || PopulationSize > 256) throw new ArgumentOutOfRangeException(nameof(populationSize));
        _space = space; _direction = direction; _initialStep = initialStepSize;
        int parents = PopulationSize / 2;
        _weights = Enumerable.Range(1, parents).Select(rank => Math.Log(parents + 0.5) - Math.Log(rank)).ToArray();
        double total = _weights.Sum(); for (int i = 0; i < parents; i++) _weights[i] /= total;
        _effectiveParents = 1 / _weights.Sum(weight => weight * weight);
        _cs = (_effectiveParents + 2) / (n + _effectiveParents + 5);
        _ds = 1 + 2 * Math.Max(0, Math.Sqrt((_effectiveParents - 1) / (n + 1)) - 1) + _cs;
        _cc = (4 + _effectiveParents / n) / (n + 4 + 2 * _effectiveParents / n);
        _c1 = 2 / ((n + 1.3) * (n + 1.3) + _effectiveParents);
        _cmu = Math.Min(1 - _c1, 2 * (_effectiveParents - 2 + 1 / _effectiveParents) / ((n + 2) * (n + 2) + _effectiveParents));
        _chi = Math.Sqrt(n) * (1 - 1d / (4 * n) + 1d / (21 * n * n));
        VersionHash = EvolutionHash.Combine(new[] { "diagonal-cma-v1", space.VersionHash, PopulationSize.ToString(CultureInfo.InvariantCulture),
            EvolutionParameterValue.Numeric(initialStepSize).Canonical, direction.ToString() });
    }

    /// <inheritdoc/>
    public string Id => "diagonal-cma";
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <summary>Gets the number of samples per learning population.</summary>
    public int PopulationSize { get; }
    /// <summary>Gets the current normalized step size.</summary>
    public double StepSize => _state?.Step ?? _initialStep;
    /// <summary>Gets the number of accepted distribution updates.</summary>
    public long Updates => _state?.Epoch ?? 0;
    /// <summary>Gets completed populations whose older distribution could not update current learning.</summary>
    public long StalePopulations => _stale;
    /// <summary>Gets populations with too few fresh feasible measurements to learn from.</summary>
    public long InvalidPopulations => _invalid;
    /// <summary>Gets samples still awaiting terminal feedback.</summary>
    public int PendingCount => _pending.Count;
    /// <summary>Gets a detached diagonal covariance snapshot, excluding the global step-size multiplier.</summary>
    public IReadOnlyList<double> Variances => Array.AsReadOnly(_state is null ? Enumerable.Repeat(1d, _space.Parameters.Count).ToArray() : (double[])_state.Variances.Clone());

    /// <inheritdoc/>
    public ValueTask<EvolutionSearchGenome> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context); cancellationToken.ThrowIfCancellationRequested();
        if (context.Generation <= _lastGeneration) throw new ArgumentException("Proposal generations must increase monotonically.", nameof(context));
        if ((_cohorts.Count >= 256 && !_openCohort.HasValue) || _cohorts.Values.Sum(cohort => cohort.Samples.Count) >= MaximumSamples)
            throw new InvalidOperationException("CMA feedback backlog exceeded its safety bound.");
        EvolutionSearchGenome parent = _space.Validate(context.Parent.Candidate.CanonicalGenome.Genome);
        _state ??= new Distribution
        {
            Mean = _space.Parameters.Select(parameter => parameter.Normalize(parent.Values[parameter.Name])).ToArray(),
            Variances = Enumerable.Repeat(1d, _space.Parameters.Count).ToArray(),
            SigmaPath = new double[_space.Parameters.Count],
            CovariancePath = new double[_space.Parameters.Count],
            Step = _initialStep
        };
        Cohort cohort;
        if (_openCohort.HasValue) cohort = _cohorts[_openCohort.Value];
        else
        {
            cohort = new Cohort { Id = _nextCohort++, Distribution = _state };
            _cohorts.Add(cohort.Id, cohort); _openCohort = cohort.Id;
        }
        var values = new Dictionary<string, EvolutionParameterValue>(StringComparer.Ordinal);
        var coordinates = new double[_space.Parameters.Count];
        for (int i = 0; i < coordinates.Length; i++)
        {
            EvolutionParameter parameter = _space.Parameters[i];
            double proposed = cohort.Distribution.Mean[i] + cohort.Distribution.Step * Math.Sqrt(cohort.Distribution.Variances[i]) * SearchSpaceMutation.StandardNormal(context.Random);
            EvolutionParameterValue value = parameter.FromNormalized(proposed);
            coordinates[i] = Clip(parameter.Normalize(value), 0, 1); values.Add(parameter.Name, value);
        }
        EvolutionSearchGenome genome = _space.CreateGenome(values);
        var sample = new Sample { Generation = context.Generation, Coordinates = coordinates };
        cohort.Samples.Add(sample); _pending.Add(context.Generation, new Pending(cohort, sample)); _lastGeneration = context.Generation;
        if (cohort.Samples.Count == PopulationSize) _openCohort = null;
        return new(genome);
    }

    /// <inheritdoc/>
    public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
    {
        Guard.NotNull(evaluation);
        if (!_pending.TryGetValue(evaluation.Lineage.Generation, out Pending? pending)) return;
        _pending.Remove(evaluation.Lineage.Generation);
        Sample sample = pending.Sample; sample.Settled = true;
        if (evaluation.Status == EvolutionEvaluationStatus.Completed && evaluation.Quality.HasValue &&
            evaluation.Direction == _direction && evaluation.CacheStatus != EvolutionCacheStatus.Hit && evaluation.Cost.AttemptCount > 0 &&
            !evaluation.ConstraintViolations.Any(violation => violation > 0)) sample.Score = evaluation.Quality;
        Cohort cohort = pending.Cohort;
        if (cohort.Samples.Count < PopulationSize || cohort.Samples.Any(item => !item.Settled)) return;
        _cohorts.Remove(cohort.Id);
        if (cohort.Distribution.Epoch != _state!.Epoch) { _stale++; return; }
        Sample[] valid = cohort.Samples.Where(item => item.Score.HasValue)
            .OrderBy(item => _direction == EvolutionOptimizationDirection.Maximize ? -item.Score!.Value : item.Score!.Value)
            .ThenBy(item => item.Generation).Take(_weights.Length).ToArray();
        if (valid.Length < _weights.Length) { _invalid++; return; }
        Update(cohort.Distribution, valid);
    }

    private void Update(Distribution prior, Sample[] parents)
    {
        int n = prior.Mean.Length;
        var next = new Distribution
        {
            Epoch = prior.Epoch + 1,
            Mean = new double[n],
            Variances = new double[n],
            SigmaPath = new double[n],
            CovariancePath = new double[n]
        };
        var step = new double[n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < parents.Length; i++) next.Mean[j] += _weights[i] * parents[i].Coordinates[j];
            next.Mean[j] = Clip(next.Mean[j], 0, 1);
            step[j] = (next.Mean[j] - prior.Mean[j]) / prior.Step;
            next.SigmaPath[j] = (1 - _cs) * prior.SigmaPath[j] + Math.Sqrt(_cs * (2 - _cs) * _effectiveParents) * step[j] / Math.Sqrt(prior.Variances[j]);
        }
        double pathNorm = Math.Sqrt(next.SigmaPath.Sum(value => value * value));
        bool keepPath = pathNorm / Math.Sqrt(1 - Math.Pow(1 - _cs, 2d * next.Epoch)) < (1.4 + 2d / (n + 1)) * _chi;
        for (int j = 0; j < n; j++)
        {
            next.CovariancePath[j] = (1 - _cc) * prior.CovariancePath[j] + (keepPath ? Math.Sqrt(_cc * (2 - _cc) * _effectiveParents) * step[j] : 0);
            double rankMu = 0;
            for (int i = 0; i < parents.Length; i++)
            {
                double displacement = (parents[i].Coordinates[j] - prior.Mean[j]) / prior.Step;
                rankMu += _weights[i] * displacement * displacement;
            }
            next.Variances[j] = Clip((1 - _c1 - _cmu + (keepPath ? 0 : _c1 * _cc * (2 - _cc))) * prior.Variances[j] +
                _c1 * next.CovariancePath[j] * next.CovariancePath[j] + _cmu * rankMu, 1e-14, 1e8);
        }
        next.Step = Clip(prior.Step * Math.Exp(Clip(_cs / _ds * (pathNorm / _chi - 1), -1, 1)), 1e-12, 2);
        _state = next;
    }

    /// <inheritdoc/>
    public string CaptureState() => JsonSerializer.Serialize(new Checkpoint
    {
        VersionHash = VersionHash,
        Current = _state,
        NextCohort = _nextCohort,
        OpenCohort = _openCohort,
        LastGeneration = _lastGeneration,
        Stale = _stale,
        Invalid = _invalid,
        Cohorts = _cohorts.Values.ToArray()
    });

    /// <inheritdoc/>
    public void RestoreState(string state)
    {
        Guard.NotNull(state);
        if (state.Length > 16 * 1024 * 1024) throw new ArgumentException("CMA checkpoint exceeds 16 MiB.", nameof(state));
        Checkpoint saved = JsonSerializer.Deserialize<Checkpoint>(state) ?? throw new ArgumentException("Missing CMA state.", nameof(state));
        if (saved.VersionHash != VersionHash || saved.NextCohort < 0 || saved.LastGeneration < 0 || saved.Stale < 0 || saved.Invalid < 0 ||
            saved.Cohorts is null || saved.Cohorts.Length > 256 || saved.NextCohort > saved.LastGeneration ||
            (decimal)(saved.Current?.Epoch ?? 0) + saved.Stale + saved.Invalid != (decimal)saved.NextCohort - saved.Cohorts.Length ||
            (saved.Current is null && (saved.LastGeneration != 0 || saved.NextCohort != 0 || saved.Cohorts.Length != 0)) ||
            (saved.Current is not null && saved.NextCohort == 0))
            throw new ArgumentException("Invalid or incompatible CMA state.", nameof(state));
        if (saved.Current is not null) ValidateDistribution(saved.Current);
        var cohorts = new SortedDictionary<long, Cohort>(); var pending = new Dictionary<long, Pending>(); var generations = new HashSet<long>();
        foreach (Cohort cohort in saved.Cohorts)
        {
            if (cohort is null || cohort.Id < 0 || cohort.Id >= saved.NextCohort || cohorts.ContainsKey(cohort.Id) ||
                cohort.Samples is null || cohort.Samples.Count < 1 || cohort.Samples.Count > PopulationSize || cohort.Distribution is null ||
                cohort.Distribution.Epoch > saved.Current!.Epoch || (cohort.Samples.Count < PopulationSize) != (saved.OpenCohort == cohort.Id) ||
                (cohort.Samples.Count == PopulationSize && cohort.Samples.All(sample => sample is not null && sample.Settled)))
                throw new ArgumentException("Invalid CMA population.", nameof(state));
            ValidateDistribution(cohort.Distribution);
            if (cohort.Distribution.Epoch == saved.Current!.Epoch && !SameDistribution(cohort.Distribution, saved.Current))
                throw new ArgumentException("A CMA epoch cannot have conflicting distributions.", nameof(state));
            cohorts.Add(cohort.Id, cohort);
            foreach (Sample sample in cohort.Samples)
            {
                if (sample is null || sample.Generation < 1 || sample.Generation > saved.LastGeneration || !generations.Add(sample.Generation) ||
                    generations.Count > MaximumSamples || !ValidVector(sample.Coordinates, 0, 1) ||
                    (sample.Score.HasValue && (!sample.Settled || !EvolutionDescriptorDefinition.IsFinite(sample.Score.Value))))
                    throw new ArgumentException("Invalid CMA sample.", nameof(state));
                if (!sample.Settled) pending.Add(sample.Generation, new Pending(cohort, sample));
            }
        }
        if (saved.OpenCohort.HasValue && !cohorts.ContainsKey(saved.OpenCohort.Value)) throw new ArgumentException("Missing open CMA population.", nameof(state));
        _cohorts.Clear(); foreach (var pair in cohorts) _cohorts.Add(pair.Key, pair.Value);
        _pending.Clear(); foreach (var pair in pending) _pending.Add(pair.Key, pair.Value);
        _state = saved.Current; _nextCohort = saved.NextCohort; _openCohort = saved.OpenCohort;
        _lastGeneration = saved.LastGeneration; _stale = saved.Stale; _invalid = saved.Invalid;
    }

    private void ValidateDistribution(Distribution distribution)
    {
        if (distribution.Epoch < 0 || !EvolutionDescriptorDefinition.IsFinite(distribution.Step) || distribution.Step < 1e-12 || distribution.Step > 2 ||
            !ValidVector(distribution.Mean, 0, 1) || !ValidVector(distribution.Variances, 1e-14, 1e8) ||
            !ValidVector(distribution.SigmaPath, -1e20, 1e20) || !ValidVector(distribution.CovariancePath, -1e20, 1e20))
            throw new ArgumentException("Invalid CMA distribution.", nameof(distribution));
    }
    private bool ValidVector(double[]? values, double minimum, double maximum) => values is not null && values.Length == _space.Parameters.Count &&
        values.All(value => EvolutionDescriptorDefinition.IsFinite(value) && value >= minimum && value <= maximum);
    private static bool SameDistribution(Distribution left, Distribution right) => left.Step == right.Step &&
        left.Mean.SequenceEqual(right.Mean) && left.Variances.SequenceEqual(right.Variances) &&
        left.SigmaPath.SequenceEqual(right.SigmaPath) && left.CovariancePath.SequenceEqual(right.CovariancePath);
    private static double Clip(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
    private sealed class Pending(Cohort cohort, Sample sample) { public Cohort Cohort { get; } = cohort; public Sample Sample { get; } = sample; }
    private sealed class Distribution
    {
        public long Epoch { get; set; }
        public double Step { get; set; }
        public double[] Mean { get; set; } = Array.Empty<double>();
        public double[] Variances { get; set; } = Array.Empty<double>();
        public double[] SigmaPath { get; set; } = Array.Empty<double>();
        public double[] CovariancePath { get; set; } = Array.Empty<double>();
    }
    private sealed class Sample
    {
        public long Generation { get; set; }
        public double[] Coordinates { get; set; } = Array.Empty<double>();
        public bool Settled { get; set; }
        public double? Score { get; set; }
    }
    private sealed class Cohort
    {
        public long Id { get; set; }
        public Distribution Distribution { get; set; } = new();
        public List<Sample> Samples { get; set; } = new();
    }
    private sealed class Checkpoint
    {
        public string VersionHash { get; set; } = string.Empty;
        public Distribution? Current { get; set; }
        public long NextCohort { get; set; }
        public long? OpenCohort { get; set; }
        public long LastGeneration { get; set; }
        public long Stale { get; set; }
        public long Invalid { get; set; }
        public Cohort[]? Cohorts { get; set; }
    }
}
