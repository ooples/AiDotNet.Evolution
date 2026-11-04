namespace AiDotNet.Evolution;

public sealed partial class EvolutionEngine<TGenome>
{
    /// <summary>Overwrites an evaluation's relative descriptors with the task's current reading.</summary>
    /// <remarks>
    /// Applied when an evaluation is committed, before it reaches an archive, so a result measured before a rebase - a
    /// slow evaluation or a cached one - is still filed against the reading the archive holds.
    /// </remarks>
    private EvolutionEvaluation WithCurrentDescriptors(EvolutionEvaluation evaluation, TGenome genome)
    {
        if (_rebasingTask is null || evaluation.Status != EvolutionEvaluationStatus.Completed) return evaluation;
        return evaluation.WithDescriptors(MergeRebasable(evaluation.Descriptors, genome));
    }

    private IReadOnlyDictionary<string, double> MergeRebasable(IReadOnlyDictionary<string, double> descriptors, TGenome genome)
    {
        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, double> pair in descriptors) merged[pair.Key] = pair.Value;
        foreach (KeyValuePair<string, double> pair in _rebasingTask!.MeasureRebasable(genome)) merged[pair.Key] = pair.Value;
        return merged;
    }

    /// <summary>Lets a rebasing task change its reading after a committed batch, and re-files everything when it does.</summary>
    /// <remarks>
    /// Runs on the sequential commit step, before the next parent is drawn. Every island is re-measured, then the
    /// cross-island elite index is re-keyed against the islands' new grids, so selection never mixes two readings. An
    /// elite-index record whose new values no grid can place is dropped from the index; its island already holds or
    /// has discarded the entry by the archive's own rules.
    /// </remarks>
    private void RebaseIfDue()
    {
        if (_rebasingTask is null) return;
        var islands = new IReadOnlyList<EvolutionArchiveEntry<TGenome>>[_islands.Length];
        for (int island = 0; island < _islands.Length; island++) islands[island] = _islands[island].Entries;
        if (!_rebasingTask.TryRebase(_completedEvaluations, islands)) return;

        foreach (IEvolutionArchive<TGenome> archive in _islands)
            ((IRemeasurableEvolutionArchive<TGenome>)archive).RemeasureEntries(
                entry => MergeRebasable(entry.Evaluation.Descriptors, entry.Candidate.CanonicalGenome.Genome));

        if (_globalElites.Capacity > 0 && _globalElites.Count > 0)
        {
            var rekeyed = new List<EvolutionEliteRecord<TGenome>>(_globalElites.Count);
            foreach (EvolutionEliteRecord<TGenome> record in _globalElites.Entries)
            {
                EvolutionEvaluation evaluation = record.Entry.Evaluation.WithDescriptors(
                    MergeRebasable(record.Entry.Evaluation.Descriptors, record.Entry.Candidate.CanonicalGenome.Genome));
                EvolutionCellKey? cell = TryCreateCellKey(_islands[record.Island], evaluation.Descriptors);
                if (cell is null) continue;
                rekeyed.Add(new EvolutionEliteRecord<TGenome>(record.Island,
                    new EvolutionArchiveEntry<TGenome>(cell, record.Entry.Candidate, evaluation)));
            }
            _globalElites.Replace(rekeyed);
        }
        RebuildEarlyStoppingArchiveMetric();
    }
}
