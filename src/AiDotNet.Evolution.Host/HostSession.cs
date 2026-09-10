namespace AiDotNet.Evolution.Host;

/// <summary>One run, and the translation between the wire protocol and the session.</summary>
/// <remarks>
/// Separated from the stdio loop so it can be tested without pipes: the loop's job is framing
/// and the session's job is evolution, and mixing them would mean every behavioural test had
/// to spawn a process.
/// </remarks>
internal sealed class HostSession : IDisposable
{
    private readonly EvolutionSession<ParameterGenome> _session;
    private readonly ParameterSpace _space;
    private readonly EvolutionOptimizationDirection _direction;
    private readonly Dictionary<long, ParameterGenome> _asked = new();
    private readonly List<string> _descriptorNames;

    private HostSession(
        EvolutionSession<ParameterGenome> session,
        ParameterSpace space,
        EvolutionOptimizationDirection direction,
        List<string> descriptorNames)
    {
        _session = session;
        _space = space;
        _direction = direction;
        _descriptorNames = descriptorNames;
    }

    internal bool IsComplete => _session.IsComplete;

    /// <summary>Builds a run from a client's config, validating everything it declared.</summary>
    /// <exception cref="ArgumentException">The configuration cannot produce a run.</exception>
    internal static HostSession Open(RunConfig config)
    {
        if (config.Parameters.Count == 0)
            throw new ArgumentException("config.parameters must declare at least one parameter.");
        if (config.Descriptors.Count == 0)
            throw new ArgumentException("config.descriptors must declare at least one descriptor.");

        var definitions = new List<ParameterDefinition>(config.Parameters.Count);
        foreach (ParameterConfig parameter in config.Parameters)
        {
            // A default step of a hundredth of the range: fine enough not to quantise away
            // real differences, coarse enough that float noise collapses instead of filling
            // the archive with candidates that differ in the seventeenth decimal.
            double step = parameter.Step ?? (parameter.Max - parameter.Min) / 100.0;
            definitions.Add(new ParameterDefinition(
                parameter.Name, parameter.Min, parameter.Max, step, parameter.Integral));
        }
        var space = new ParameterSpace(definitions);

        var descriptorDefinitions = new List<EvolutionDescriptorDefinition>(config.Descriptors.Count);
        var descriptorNames = new List<string>(config.Descriptors.Count);
        foreach (DescriptorConfig descriptor in config.Descriptors)
        {
            if (string.IsNullOrWhiteSpace(descriptor.Name))
                throw new ArgumentException("Every descriptor needs a name.");
            if (descriptor.Bins <= 0)
                throw new ArgumentException($"Descriptor '{descriptor.Name}' needs a positive bin count.");
            descriptorDefinitions.Add(new EvolutionDescriptorDefinition(
                descriptor.Name, descriptor.Min, descriptor.Max, descriptor.Bins,
                EvolutionOutOfRangePolicy.Clamp));
            descriptorNames.Add(descriptor.Name);
        }

        EvolutionOptimizationDirection direction =
            string.Equals(config.Direction, "minimize", StringComparison.OrdinalIgnoreCase)
                ? EvolutionOptimizationDirection.Minimize
                : EvolutionOptimizationDirection.Maximize;

        var options = new EvolutionEngineOptions
        {
            RunId = "host",
            Seed = config.Seed,
            MaxProposals = Math.Max(1, config.MaxProposals),
            MaxEvaluationAttempts = Math.Max(1, config.MaxEvaluations ?? config.MaxProposals),
            MaxGenerations = Math.Max(1, config.MaxGenerations),
            ProposalBatchSize = Math.Max(1, config.BatchSize),
            CheckpointInterval = 0,
        };

        List<ParameterGenome> seeds = BuildSeeds(config, space);

        var session = new EvolutionSession<ParameterGenome>(
            task => new EvolutionEngine<ParameterGenome>(
                task,
                new ParameterVariation(),
                _ => new MapElitesArchive<ParameterGenome>(descriptorDefinitions),
                options),
            seeds,
            // The genome's own canonical text, which IS its content rather than a type
            // name -- see ParameterGenome.CanonicalId.
            genome => genome.CanonicalId());

        return new HostSession(session, space, direction, descriptorNames);
    }

    private static List<ParameterGenome> BuildSeeds(RunConfig config, ParameterSpace space)
    {
        var seeds = new List<ParameterGenome>();
        if (config.Seeds is { Count: > 0 })
        {
            foreach (Dictionary<string, double> seed in config.Seeds)
                seeds.Add(space.Create(seed));
            return seeds;
        }

        // No seeds given: start at the midpoint of every range, which is the least
        // opinionated starting point and keeps a first run reproducible.
        seeds.Add(space.Create(new Dictionary<string, double>(StringComparer.Ordinal)));
        return seeds;
    }

    internal async Task<List<Candidate>> AskAsync(int max, CancellationToken cancellationToken)
    {
        IReadOnlyList<EvolutionAskItem<ParameterGenome>> batch =
            await _session.AskAsync(Math.Max(1, max), cancellationToken).ConfigureAwait(false);

        var candidates = new List<Candidate>(batch.Count);
        foreach (EvolutionAskItem<ParameterGenome> item in batch)
        {
            ParameterGenome genome = item.Candidate.CanonicalGenome.Genome;
            _asked[item.EvaluationId] = genome;
            candidates.Add(new Candidate
            {
                EvaluationId = item.EvaluationId,
                Parameters = _space.ToMap(genome),
            });
        }
        return candidates;
    }

    /// <summary>Applies scored results, returning how many were actually outstanding.</summary>
    internal int Tell(IReadOnlyList<TellResult> results)
    {
        int accepted = 0;
        foreach (TellResult result in results)
        {
            EvolutionTaskResult outcome = result.Quality is double quality && double.IsFinite(quality)
                ? EvolutionTaskResult.Completed(
                    quality,
                    Descriptors(result),
                    _direction)
                // A non-finite or absent quality is a FAILED evaluation, not a zero. Scoring
                // it zero would place a broken run in the archive as a genuinely poor result
                // and let it out-compete nothing, which hides the failure.
                : EvolutionTaskResult.Failed(
                    "evaluation_failed",
                    result.Reason ?? "the client reported no usable quality");

            if (_session.Tell(result.EvaluationId, outcome))
            {
                accepted += 1;
                _asked.Remove(result.EvaluationId);
            }
        }
        return accepted;
    }

    private Dictionary<string, double> Descriptors(TellResult result)
    {
        var descriptors = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string name in _descriptorNames)
        {
            // A descriptor the client omitted defaults to zero rather than throwing: a
            // partial report should place the candidate somewhere in the archive rather
            // than fail the whole batch.
            descriptors[name] = result.Descriptors is not null
                && result.Descriptors.TryGetValue(name, out double value)
                && double.IsFinite(value)
                    ? value
                    : 0.0;
        }
        return descriptors;
    }

    /// <summary>The best entry across islands, or null before anything has been archived.</summary>
    internal async Task<(Candidate? Best, string? StopReason)> FinishAsync()
    {
        _session.RequestStop();

        EvolutionRunResult<ParameterGenome>? result = null;
        try
        {
            result = await _session.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is a normal ending here; there is simply no run result to report.
        }

        if (result is null) return (null, "canceled");

        EvolutionArchiveEntry<ParameterGenome>? best = null;
        foreach (IEvolutionArchiveView<ParameterGenome> island in result.Islands)
        {
            EvolutionArchiveEntry<ParameterGenome>? candidate = island.Best;
            if (candidate is null) continue;
            if (best is null || Better(candidate, best)) best = candidate;
        }

        if (best is null) return (null, result.StopReason.ToString());

        return (
            new Candidate
            {
                EvaluationId = best.Candidate.EvaluationId,
                Parameters = _space.ToMap(best.Candidate.CanonicalGenome.Genome),
                Quality = best.Evaluation.Quality,
            },
            result.StopReason.ToString());
    }

    private bool Better(EvolutionArchiveEntry<ParameterGenome> a, EvolutionArchiveEntry<ParameterGenome> b)
    {
        double left = a.Evaluation.Quality ?? double.NaN;
        double right = b.Evaluation.Quality ?? double.NaN;
        if (double.IsNaN(left)) return false;
        if (double.IsNaN(right)) return true;
        return _direction == EvolutionOptimizationDirection.Maximize ? left > right : left < right;
    }

    public void Dispose() => _session.Dispose();
}
