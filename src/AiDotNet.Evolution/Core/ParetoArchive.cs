using System.Globalization;
using System.IO;

namespace AiDotNet.Evolution;

/// <summary>Optional bounded feasible nondominated archive with deterministic crowding pruning.</summary>
/// <remarks>Entries are the front, not scalar-ranked winners. Best is only its scalar-quality representative.
/// Equal tolerance boxes retain the ordinal-smallest genome. Pruning is online and insertion-order dependent,
/// but repeatable for the same sequence. No discarded or infeasible historical population is retained.</remarks>
public sealed class ParetoArchive<TGenome> : ICheckpointableEvolutionArchive<TGenome>, IEvolutionObjectiveArchiveView, IEvolutionArchiveCellCount
{
    private IReadOnlyList<EvolutionArchiveEntry<TGenome>> _entries = Array.Empty<EvolutionArchiveEntry<TGenome>>();
    /// <summary>Creates an empty front. Scalar direction affects only the explicit Best representative.</summary>
    public ParetoArchive(EvolutionParetoDefinition definition, EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(definition);
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        ParetoDefinition = definition; Direction = direction;
        DefinitionHash = EvolutionHash.Combine(new[] { definition.DefinitionHash, direction.ToString() });
    }
    /// <inheritdoc/>
    public EvolutionParetoDefinition ParetoDefinition { get; }
    /// <inheritdoc/>
    public IReadOnlyList<EvolutionDescriptorDefinition> Descriptors => Array.Empty<EvolutionDescriptorDefinition>();
    /// <inheritdoc/>
    public string DefinitionHash { get; }
    /// <inheritdoc/>
    public EvolutionOptimizationDirection Direction { get; }
    /// <inheritdoc/>
    public int Count => _entries.Count;
    /// <inheritdoc/>
    public long Version { get; private set; }
    /// <summary>Gets capacity, not a behavior-grid size.</summary>
    public long TotalCells => ParetoDefinition.Capacity;
    /// <inheritdoc/>
    public IReadOnlyList<EvolutionArchiveEntry<TGenome>> Entries => _entries;
    /// <summary>Gets the scalar-quality representative of the retained front, not a replacement for the front.</summary>
    public EvolutionArchiveEntry<TGenome>? Best => _entries.OrderBy(entry => entry, EvolutionEntryOrdering.BestFirst<TGenome>(Direction)).FirstOrDefault();
    /// <inheritdoc/>
    public EvolutionArchiveEntry<TGenome>? Get(EvolutionCellKey cell)
    {
        Guard.NotNull(cell);
        return _entries.FirstOrDefault(entry => entry.Cell.Equals(cell));
    }
    /// <inheritdoc/>
    public EvolutionArchiveEntry<TGenome>? Sample(StableRandom random)
    {
        Guard.NotNull(random);
        return Count == 0 ? null : _entries[random.NextInt(0, Count)];
    }
    /// <inheritdoc/>
    public EvolutionArchiveInsertionResult TryAdd(EvolutionCandidate<TGenome> candidate, EvolutionEvaluation evaluation)
    {
        Guard.NotNull(candidate); Guard.NotNull(evaluation);
        if (!ParetoDefinition.IsFeasible(evaluation) || evaluation.Direction != Direction ||
            candidate.EvaluationId != evaluation.EvaluationId || candidate.CanonicalGenome.Id != evaluation.GenomeId)
            return EvolutionArchiveInsertionResult.Rejected;
        if (_entries.Any(entry => entry.Evaluation.GenomeId == evaluation.GenomeId)) return EvolutionArchiveInsertionResult.NotImproved;
        if (_entries.Any(entry => ParetoDefinition.Compare(entry.Evaluation, evaluation) < 0 ||
            (ParetoDefinition.Equivalent(entry.Evaluation, evaluation) && StringComparer.Ordinal.Compare(entry.Evaluation.GenomeId, evaluation.GenomeId) < 0)))
            return EvolutionArchiveInsertionResult.NotImproved;
        var added = new EvolutionArchiveEntry<TGenome>(Key(evaluation.GenomeId), candidate, evaluation);
        if (_entries.Any(entry => entry.Cell.Equals(added.Cell))) throw new InvalidOperationException("Pareto identity hash collision.");
        var next = _entries.Where(entry => ParetoDefinition.Compare(evaluation, entry.Evaluation) >= 0 &&
            !ParetoDefinition.Equivalent(evaluation, entry.Evaluation)).ToList();
        int removed = Count - next.Count;
        next.Add(added);
        bool evicted = next.Count > ParetoDefinition.Capacity;
        if (evicted)
        {
            var ranked = EvolutionParetoQuery.CrowdingOrder(next, ParetoDefinition);
            var victim = ranked[ranked.Count - 1];
            if (ReferenceEquals(victim, added)) return EvolutionArchiveInsertionResult.NotImproved;
            next.Remove(victim);
        }
        _entries = Array.AsReadOnly(next.OrderBy(entry => entry.Cell.StableKey, StringComparer.Ordinal).ToArray());
        Version++;
        return evicted ? EvolutionArchiveInsertionResult.InsertedWithEviction : removed > 0
            ? EvolutionArchiveInsertionResult.Replaced : EvolutionArchiveInsertionResult.Inserted;
    }
    /// <inheritdoc/>
    public void Restore(IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries, IReadOnlyList<EvolutionDescriptorDefinition> descriptors, long version)
    {
        Guard.NotNull(entries); Guard.NotNull(descriptors);
        if (Count != 0 || Version != 0) throw new InvalidOperationException("Restore requires an empty archive.");
        if (descriptors.Count != 0 || entries.Count > ParetoDefinition.Capacity || version < entries.Count)
            throw new InvalidDataException("Invalid Pareto checkpoint shape or version.");
        var copy = EvolutionCollection.CopyBounded(entries, ParetoDefinition.Capacity, nameof(entries));
        for (int i = 0; i < copy.Length; i++)
        {
            var entry = copy[i];
            if (entry is null || !ParetoDefinition.IsFeasible(entry.Evaluation) || entry.Evaluation.Direction != Direction ||
                !entry.Cell.Equals(Key(entry.Evaluation.GenomeId))) throw new InvalidDataException("Invalid Pareto checkpoint entry.");
            for (int j = 0; j < i; j++)
                if (entry.Cell.Equals(copy[j].Cell) || ParetoDefinition.Compare(entry.Evaluation, copy[j].Evaluation) != 0 ||
                    ParetoDefinition.Equivalent(entry.Evaluation, copy[j].Evaluation))
                    throw new InvalidDataException("Checkpoint entries do not form a distinct nondominated front.");
        }
        _entries = Array.AsReadOnly(copy.OrderBy(entry => entry.Cell.StableKey, StringComparer.Ordinal).ToArray()); Version = version;
    }
    private static EvolutionCellKey Key(string id)
    {
        string hash = EvolutionHash.Compute(id);
        return new EvolutionCellKey(Enumerable.Range(0, 16).Select(i => int.Parse(hash.Substring(i * 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
    }
}
