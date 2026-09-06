namespace AiDotNet.Evolution;

/// <summary>Derives a whole archive grid from measurements of a seed population.</summary>
/// <remarks>
/// The result is a pure function of the supplied measurements and options. It is fixed before evolution begins and
/// enters the archive definition hash exactly like a hand-authored grid. Use <see cref="FromObservations"/> when
/// evaluations are expensive so the observations can be reused without evaluating seeds twice.
/// </remarks>
public static class EvolutionDescriptorCalibration
{
    /// <summary>Derives descriptor definitions from values that have already been measured.</summary>
    public static IReadOnlyList<EvolutionDescriptorDefinition> FromObservations(
        IReadOnlyList<IReadOnlyDictionary<string, double>> observations,
        IReadOnlyList<string>? names = null,
        EvolutionDescriptorCalibrationOptions? options = null)
    {
        Guard.NotNull(observations);
        if (observations.Count > EvolutionCollectionLimits.MaximumResultEntries)
            throw new ArgumentException("The observation set exceeds the package safety bound.", nameof(observations));
        EvolutionDescriptorCalibrationOptions settings =
            (options ?? new EvolutionDescriptorCalibrationOptions()).Clone();
        settings.Validate();

        IReadOnlyDictionary<string, double>[] observationSources = EvolutionCollection.CopyBounded(
            observations,
            EvolutionCollectionLimits.MaximumResultEntries,
            nameof(observations));
        var snapshotObservations = new IReadOnlyDictionary<string, double>[observationSources.Length];
        for (int index = 0; index < observationSources.Length; index++)
        {
            IReadOnlyDictionary<string, double> observation = observationSources[index];
            if (observation is null)
                throw new ArgumentException("An observation cannot be null.", nameof(observations));
            KeyValuePair<string, double>[] pairs = EvolutionCollection.ToBoundedArray(
                observation,
                EvolutionTaskResult.MaximumNamedValues,
                nameof(observations));
            var snapshot = new Dictionary<string, double>(pairs.Length, StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> pair in pairs)
            {
                if (pair.Key is null || snapshot.ContainsKey(pair.Key))
                    throw new ArgumentException("An observation contains an invalid or repeated name.", nameof(observations));
                snapshot.Add(pair.Key, pair.Value);
            }
            snapshotObservations[index] = snapshot;
        }

        IReadOnlyList<string> axes = names is null ? DiscoverNames(snapshotObservations) : ValidateNames(names);
        var definitions = new List<EvolutionDescriptorDefinition>(axes.Count);
        foreach (string axis in axes) definitions.Add(Calibrate(axis, snapshotObservations, settings));
        return definitions;
    }

    /// <summary>Evaluates each seed once and derives descriptor definitions from those evaluations.</summary>
    /// <remarks>
    /// This convenience pass is separate from the engine's evaluation budget and internal cache. For an expensive
    /// evaluator, retain descriptors from an existing seed-evaluation pass and call <see cref="FromObservations"/>.
    /// </remarks>
    public static async Task<IReadOnlyList<EvolutionDescriptorDefinition>> CalibrateAsync<TGenome>(
        IEvolutionTask<TGenome> task,
        IReadOnlyList<TGenome> seeds,
        IReadOnlyList<string>? names = null,
        EvolutionDescriptorCalibrationOptions? options = null,
        ulong seed = 0,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(task);
        Guard.NotNull(seeds);
        if (seeds.Count == 0) throw new ArgumentException("At least one seed is required.", nameof(seeds));
        if (seeds.Count > EvolutionCollectionLimits.MaximumResultEntries)
            throw new ArgumentException("The seed set exceeds the package safety bound.", nameof(seeds));

        TGenome[] seedSnapshot = EvolutionCollection.CopyBounded(
            seeds,
            EvolutionCollectionLimits.MaximumResultEntries,
            nameof(seeds));
        var observations = new List<IReadOnlyDictionary<string, double>>(seedSnapshot.Length);
        for (int index = 0; index < seedSnapshot.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seedSnapshot[index] is not { } genome) throw new ArgumentException("A seed cannot be null.", nameof(seeds));

            EvolutionCanonicalGenome<TGenome> canonical = await task
                .CanonicalizeAsync(genome, cancellationToken).ConfigureAwait(false);
            if (canonical is null) throw new InvalidOperationException("The task canonicalized a seed to null.");

            var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
            var candidate = new EvolutionCandidate<TGenome>(index, canonical, lineage);
            var context = new EvolutionEvaluationContext(index, seed, unchecked((ulong)index * 8UL + 2UL), 1);

            EvolutionTaskResult result = await task
                .EvaluateAsync(candidate, context, cancellationToken).ConfigureAwait(false);
            if (result is null || result.Status != EvolutionEvaluationStatus.Completed) continue;
            observations.Add(result.Descriptors);
        }

        return FromObservations(observations, names, options);
    }

    private static IReadOnlyList<string> DiscoverNames(
        IReadOnlyList<IReadOnlyDictionary<string, double>> observations)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, double> observation in observations)
        {
            foreach (KeyValuePair<string, double> pair in observation)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && EvolutionDescriptorDefinition.IsFinite(pair.Value))
                {
                    names.Add(pair.Key.Trim());
                    if (names.Count > EvolutionCollectionLimits.MaximumArchiveDimensions)
                        throw new ArgumentException(
                            $"Calibration may discover at most {EvolutionCollectionLimits.MaximumArchiveDimensions} descriptors.",
                            nameof(observations));
                }
            }
        }

        if (names.Count == 0)
        {
            throw new ArgumentException(
                "No observation reported a finite descriptor, so there is nothing to calibrate. Report at least " +
                "one descriptor from the task's evaluation, or define the archive axes by hand.",
                nameof(observations));
        }

        return new List<string>(names);
    }

    private static IReadOnlyList<string> ValidateNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0) throw new ArgumentException("At least one descriptor name is required.", nameof(names));
        if (names.Count > EvolutionCollectionLimits.MaximumArchiveDimensions)
            throw new ArgumentException(
                $"Calibration may define at most {EvolutionCollectionLimits.MaximumArchiveDimensions} descriptors.",
                nameof(names));

        string[] snapshot = EvolutionCollection.CopyBounded(
            names,
            EvolutionCollectionLimits.MaximumArchiveDimensions,
            nameof(names));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var axes = new List<string>(snapshot.Length);
        foreach (string name in snapshot)
        {
            if (name is not { } text || text.Trim().Length == 0)
                throw new ArgumentException("A descriptor name cannot be empty or white space.", nameof(names));
            string trimmed = text.Trim();
            if (!seen.Add(trimmed)) throw new ArgumentException("Descriptor names must be distinct.", nameof(names));
            axes.Add(trimmed);
        }

        return axes;
    }

    private static EvolutionDescriptorDefinition Calibrate(
        string name,
        IReadOnlyList<IReadOnlyDictionary<string, double>> observations,
        EvolutionDescriptorCalibrationOptions options)
    {
        double minimum = 0;
        double maximum = 0;
        bool observed = false;

        foreach (IReadOnlyDictionary<string, double> observation in observations)
        {
            bool found = false;
            double value = 0;
            foreach (KeyValuePair<string, double> pair in observation)
            {
                if (!string.Equals(pair.Key.Trim(), name, StringComparison.Ordinal)) continue;
                if (found)
                    throw new ArgumentException(
                        "An observation contains duplicate descriptor names after trimming.",
                        nameof(observations));
                found = true;
                value = pair.Value;
            }
            if (!found || !EvolutionDescriptorDefinition.IsFinite(value)) continue;
            if (!observed)
            {
                minimum = value;
                maximum = value;
                observed = true;
                continue;
            }

            if (value < minimum) minimum = value;
            if (value > maximum) maximum = value;
        }

        if (!observed)
        {
            throw new ArgumentException(
                "No observation reported a finite value for descriptor '" + name + "', so its range cannot be " +
                "derived. Report it from the task's evaluation, or define this axis by hand.",
                nameof(observations));
        }

        if (minimum == maximum) return Degenerate(name, minimum, options);

        var axis = new EvolutionDescriptorCalibrator(name, options.BinCount, options.OutOfRangePolicy);
        axis.Observe(minimum);
        axis.Observe(maximum);
        try
        {
            return axis.Freeze(options.Padding);
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(
                "The values reported for descriptor '" + name + "' do not produce a finite range. Define this axis " +
                "by hand, reduce the padding, or report values on a smaller scale.",
                nameof(observations), exception);
        }
    }

    private static EvolutionDescriptorDefinition Degenerate(
        string name, double value, EvolutionDescriptorCalibrationOptions options)
    {
        double half = options.DegenerateSpan / 2;
        double minimum = value - half;
        double maximum = value + half;
        if (!EvolutionDescriptorDefinition.IsFinite(minimum) || !EvolutionDescriptorDefinition.IsFinite(maximum) ||
            !EvolutionDescriptorDefinition.IsFinite(maximum - minimum) || maximum <= minimum)
        {
            throw new ArgumentException(
                "Descriptor '" + name + "' reported one value that no span can be centred on. Define this axis by " +
                "hand, or report values on a smaller scale.",
                "observations");
        }

        return new EvolutionDescriptorDefinition(name, minimum, maximum, options.BinCount, options.OutOfRangePolicy);
    }
}
