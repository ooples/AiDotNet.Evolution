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

        // BOUNDED BEFORE ANYTHING IS ALLOCATED FROM THEM. One well-formed frame declaring
        // a million parameters is small on the wire and large in the heap, and the
        // descriptor grid is worse than linear: every dimension multiplies the cell count.
        if (config.Parameters.Count > ProtocolLimits.MaxDimensions)
            throw new ArgumentException($"config.parameters declares {config.Parameters.Count} parameters, more than the {ProtocolLimits.MaxDimensions} limit.");
        if (config.Descriptors.Count > ProtocolLimits.MaxDimensions)
            throw new ArgumentException($"config.descriptors declares {config.Descriptors.Count} descriptors, more than the {ProtocolLimits.MaxDimensions} limit.");
        if (config.Seeds is { Count: > ProtocolLimits.MaxSeeds })
            throw new ArgumentException($"config.seeds carries {config.Seeds.Count} seeds, more than the {ProtocolLimits.MaxSeeds} limit.");

        // The host always supplies a seed, so it needs a positive proposal budget.
        // Zero evaluations does no work; zero generations evaluates seeds only.
        // Preserve those engine contracts instead of silently buying one unit.
        if (config.MaxProposals <= 0)
            throw new ArgumentException("config.maxProposals must be positive because a run includes at least one seed.", nameof(config));
        if (config.MaxEvaluations < 0)
            throw new ArgumentException("config.maxEvaluations must be non-negative.", nameof(config));
        if (config.MaxGenerations < 0)
            throw new ArgumentException("config.maxGenerations must be non-negative.", nameof(config));
        if (config.BatchSize <= 0)
            throw new ArgumentException("config.batchSize must be positive.", nameof(config));

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

        // AN UNRECOGNISED DIRECTION IS AN ERROR, not a fallback to Maximize. Falling back
        // means a client that sent "minimise" or "min" gets a run that optimises the
        // opposite way and reports success -- the one failure a caller cannot detect from
        // the results, because a wrong-direction search still returns a plausible genome.
        EvolutionOptimizationDirection direction;
        if (string.Equals(config.Direction, "minimize", StringComparison.OrdinalIgnoreCase))
            direction = EvolutionOptimizationDirection.Minimize;
        else if (string.Equals(config.Direction, "maximize", StringComparison.OrdinalIgnoreCase))
            direction = EvolutionOptimizationDirection.Maximize;
        else
            throw new ArgumentException(
                $"config.direction must be 'maximize' or 'minimize', not '{config.Direction}'.");

        var options = new EvolutionEngineOptions
        {
            RunId = "host",
            Seed = config.Seed,
            MaxProposals = config.MaxProposals,
            MaxEvaluationAttempts = config.MaxEvaluations ?? config.MaxProposals,
            MaxGenerations = config.MaxGenerations,
            ProposalBatchSize = config.BatchSize,
            CheckpointInterval = 0,
        };

        List<ParameterGenome> seeds = BuildSeeds(config, space);

        var session = new EvolutionSession<ParameterGenome>(
            task => new EvolutionEngine<ParameterGenome>(
                task,
                new ParameterVariation(),
                // THE ARCHIVE MUST BE TOLD THE DIRECTION TOO. MapElitesArchive defaults to
                // Maximize and rejects any evaluation whose direction differs from its own,
                // so a minimizing run built on a defaulted archive inserts nothing, finds no
                // elites to breed from, and stops after the seed batch with
                // StopReason.NoCandidates -- a silent empty result, not an error.
                _ => new MapElitesArchive<ParameterGenome>(descriptorDefinitions, direction),
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
            EvolutionTaskResult outcome = ReportedOutcome(result);

            // NOTHING IS REMEMBERED PER ASK. A dictionary of asked genomes used to be
            // kept here and only ever written to: the session already owns the
            // outstanding set, so this was a second copy that nobody read and that only
            // an accepted tell would shrink. A client that asked and never told grew it
            // for the life of the run.
            if (_session.Tell(result.EvaluationId, outcome)) accepted += 1;
        }
        return accepted;
    }

    private EvolutionTaskResult ReportedOutcome(TellResult result)
    {
        if (result.Quality is not double quality || !double.IsFinite(quality))
            return EvolutionTaskResult.Failed("evaluation_failed",
                result.Reason ?? "the client reported no usable quality");

        var descriptors = new Dictionary<string, double>(_descriptorNames.Count, StringComparer.Ordinal);
        foreach (string name in _descriptorNames)
        {
            // A fabricated coordinate is as misleading as a fabricated quality: it can
            // displace a real elite and change future parents. Settle this evaluation as
            // failed without inserting it; an explicitly reported finite zero is valid.
            if (result.Descriptors is null || !result.Descriptors.TryGetValue(name, out double? measurement)
                || measurement is not double value || !double.IsFinite(value))
                return EvolutionTaskResult.Failed("invalid_descriptors",
                    $"the client reported no finite value for descriptor '{name}'");
            descriptors[name] = value;
        }
        return EvolutionTaskResult.Completed(quality, descriptors, _direction);
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

        if (best is null) return (null, Protocol.StopReasonToWire(result.StopReason));

        return (
            new Candidate
            {
                EvaluationId = best.Candidate.EvaluationId,
                Parameters = _space.ToMap(best.Candidate.CanonicalGenome.Genome),
                Quality = best.Evaluation.Quality,
            },
            Protocol.StopReasonToWire(result.StopReason));
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
