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
    /// cross-island elite index and each island's retained history are re-keyed against the islands' new grids, so
    /// selection and checkpoints never mix two readings. An island elite, elite-index record or history entry whose new
    /// values no grid can place is dropped.
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
                EvolutionArchiveEntry<TGenome>? entry = Rekey(record.Island, record.Entry);
                if (entry is not null) rekeyed.Add(new EvolutionEliteRecord<TGenome>(record.Island, entry));
            }
            _globalElites.Replace(rekeyed);
        }
        for (int island = 0; island < _histories.Length; island++)
        {
            int owner = island;
            _histories[island].Rekey(entry => Rekey(owner, entry));
        }
        RebuildEarlyStoppingArchiveMetric();
    }

    /// <summary>Re-measures one retained entry and keys it on its island's current grid, or drops it when no cell fits.</summary>
    private EvolutionArchiveEntry<TGenome>? Rekey(int island, EvolutionArchiveEntry<TGenome> entry)
    {
        EvolutionEvaluation evaluation = entry.Evaluation.WithDescriptors(
            MergeRebasable(entry.Evaluation.Descriptors, entry.Candidate.CanonicalGenome.Genome));
        EvolutionCellKey? cell = TryCreateCellKey(_islands[island], evaluation.Descriptors);
        return cell is null ? null : new EvolutionArchiveEntry<TGenome>(cell, entry.Candidate, evaluation);
    }
}
