using System.Text.Json;

namespace AiDotNet.Evolution.Programs;

public sealed partial class ProgramEvolutionTask
{
    private const string RebaseStateSchema = "program-descriptor-rebase-state-v1";
    private readonly ProgramDescriptorRebaseOptions? _rebase;
    // Touched only on the engine's sequential commit step and in checkpoint restore.
    private long _rebasedAt = -1;
    private IReadOnlyList<string> _references = Array.Empty<string>();

    /// <inheritdoc/>
    /// <remarks>True only when the task was built with rebase options and a relative descriptor to apply them to.</remarks>
    public bool RebasesDescriptors => _rebase is not null;

    /// <summary>Gets the normalized reference programs the relative descriptors currently measure against; empty
    /// until the first pick, when they still use the references they were built with.</summary>
    public IReadOnlyList<string> RebaseReferences => _references;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, double> MeasureRebasable(ProgramGenome genome)
    {
        ProgramGuard.NotNull(genome);
        ProgramDescriptorSet current = _descriptors;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int index = 0; index < current.Count; index++)
        {
            if (current.Descriptors[index] is not IRebasableProgramDescriptor descriptor) continue;
            double value = descriptor.Compute(genome);
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new InvalidOperationException($"Descriptor '{current.Names[index]}' returned a value that is not finite.");
            values[current.Names[index]] = value;
        }
        return values;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The pick is a greedy max-min walk over the distinct archived programs, using the diversity descriptor's own
    /// distance. It starts at the best program, keeps the best-quality entry of a genome that more than one island holds,
    /// and breaks ties by the ordinally smaller genome identifier, so the same archives always give the same set. A pick
    /// identical to the current references counts as done but reports no change, so nothing is re-filed for nothing.
    /// </remarks>
    public bool TryRebase(long completedEvaluations, IReadOnlyList<IReadOnlyList<EvolutionArchiveEntry<ProgramGenome>>> islands)
    {
        ProgramGuard.NotNull(islands);
        if (_rebase is null) return false;

        var pool = new Dictionary<string, EvolutionArchiveEntry<ProgramGenome>>(StringComparer.Ordinal);
        foreach (IReadOnlyList<EvolutionArchiveEntry<ProgramGenome>> island in islands)
            foreach (EvolutionArchiveEntry<ProgramGenome> entry in island)
                if (!pool.TryGetValue(entry.Evaluation.GenomeId, out EvolutionArchiveEntry<ProgramGenome>? held) || Better(entry, held))
                    pool[entry.Evaluation.GenomeId] = entry;
        if (pool.Count == 0) return false;

        bool due = _rebasedAt < 0
            ? pool.Count >= _rebase.ReferenceSize || completedEvaluations >= _rebase.ReferenceSize
            : _rebase.Mode == ProgramDescriptorRebaseMode.Periodic && completedEvaluations - _rebasedAt >= _rebase.Interval;
        if (!due) return false;

        string[] picked = Pick(pool.Values, _rebase.ReferenceSize);
        _rebasedAt = completedEvaluations;
        if (picked.SequenceEqual(_references, StringComparer.Ordinal)) return false;
        Apply(picked);
        return true;
    }

    /// <inheritdoc/>
    public string CaptureRebaseState() => JsonSerializer.Serialize(new RebaseState
    {
        Schema = RebaseStateSchema,
        RebasedAt = _rebasedAt,
        References = _references.ToList()
    });

    /// <inheritdoc/>
    public void RestoreRebaseState(string state, long completedEvaluations)
    {
        ProgramGuard.NotNull(state);
        RebaseState? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RebaseState>(state);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The descriptor-rebase state is not valid JSON.", exception);
        }
        if (parsed is null || parsed.Schema != RebaseStateSchema || parsed.References is null || parsed.RebasedAt < -1 ||
            // A later position would hold off the next periodic pick until the counter caught up with it.
            parsed.RebasedAt > completedEvaluations ||
            parsed.References.Any(reference => reference is null) ||
            // A pick always stamps its position and keeps at least the best program, so the two agree or neither is set.
            (parsed.RebasedAt < 0) != (parsed.References.Count == 0) ||
            (_rebase is not null && parsed.References.Count > _rebase.ReferenceSize))
            throw new InvalidDataException("The descriptor-rebase state is malformed or belongs to a different task.");

        string[] references = parsed.References.ToArray();
        ProgramGenome[] genomes;
        try
        {
            genomes = Genomes(references);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The descriptor-rebase state holds a reference that is not a program.", exception);
        }
        // References are recorded normalized; anything else was not written by a pick.
        for (int index = 0; index < references.Length; index++)
            if (!string.Equals(genomes[index].NormalizedSource, references[index], StringComparison.Ordinal))
                throw new InvalidDataException("The descriptor-rebase state holds a reference that is not normalized.");

        _rebasedAt = parsed.RebasedAt;
        if (references.Length == 0)
        {
            _references = Array.Empty<string>();
            _descriptors = _initialDescriptors;
        }
        else
        {
            Apply(references, genomes);
        }
    }

    private ProgramGenome[] Genomes(string[] references) =>
        references.Select(source => new ProgramGenome(source, _options.Language)).ToArray();

    private void Apply(string[] references) => Apply(references, Genomes(references));

    private void Apply(string[] references, ProgramGenome[] genomes)
    {
        _descriptors = _initialDescriptors.Rebase(genomes);
        _references = Array.AsReadOnly(references);
    }

    private static bool Better(EvolutionArchiveEntry<ProgramGenome> candidate, EvolutionArchiveEntry<ProgramGenome> held)
    {
        double a = candidate.Evaluation.Quality ?? double.NaN, b = held.Evaluation.Quality ?? double.NaN;
        if (a.Equals(b) || double.IsNaN(a)) return false;
        if (double.IsNaN(b)) return true;
        return candidate.Evaluation.Direction == EvolutionOptimizationDirection.Minimize ? a < b : a > b;
    }

    /// <summary>The greedy max-min walk: the best program, then repeatedly the one farthest from everything picked.</summary>
    private static string[] Pick(IEnumerable<EvolutionArchiveEntry<ProgramGenome>> entries, int size)
    {
        // Ordinal genome order fixes every tie; the best is found in that order with a strict comparison.
        EvolutionArchiveEntry<ProgramGenome>[] ordered = entries
            .OrderBy(entry => entry.Evaluation.GenomeId, StringComparer.Ordinal)
            .ToArray();
        int start = 0;
        for (int index = 1; index < ordered.Length; index++)
            if (Better(ordered[index], ordered[start])) start = index;

        string[] sources = ordered.Select(entry => entry.Candidate.CanonicalGenome.Genome.NormalizedSource).ToArray();
        var picked = new List<string>(Math.Min(size, sources.Length)) { sources[start] };
        var chosen = new bool[sources.Length];
        chosen[start] = true;
        var nearest = new double[sources.Length];
        for (int index = 0; index < sources.Length; index++)
            nearest[index] = ProgramDiversityDescriptor.Distance(sources[index], sources[start]);

        while (picked.Count < size)
        {
            int farthest = -1;
            for (int index = 0; index < sources.Length; index++)
                if (!chosen[index] && (farthest < 0 || nearest[index] > nearest[farthest])) farthest = index;
            if (farthest < 0) break;
            chosen[farthest] = true;
            picked.Add(sources[farthest]);
            for (int index = 0; index < sources.Length; index++)
                if (!chosen[index])
                    nearest[index] = Math.Min(nearest[index], ProgramDiversityDescriptor.Distance(sources[index], sources[farthest]));
        }
        return picked.ToArray();
    }

    private sealed class RebaseState
    {
        public string? Schema { get; set; }
        public long RebasedAt { get; set; }
        public List<string>? References { get; set; }
    }
}
