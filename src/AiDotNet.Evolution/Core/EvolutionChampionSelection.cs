namespace AiDotNet.Evolution;

/// <summary>Chooses a run's champion only among entries that beat a reference by more than their measurement noise.</summary>
/// <remarks>
/// <para>
/// An archive's best entry is the one with the highest measured quality. When quality is measured on a small sample -
/// a few dozen rows of a regression task, a handful of test cases - the highest measured quality is often the program
/// that happened to fit that sample's noise. E-4's pilot measured this: on 80 selection rows, candidates whose
/// selection score beat the seed's did worse than the seed on 400 held-out rows in 3 of 6 cells, while each cell's
/// archive still held a program as good as the seed.
/// </para>
/// <para>
/// This rule asks the evaluator for a standard error alongside each quality, reported as a metric. An entry is
/// eligible when its gain over the reference exceeds <c>standardErrors</c> times the combined standard error
/// <c>sqrt(se_entry^2 + se_reference^2)</c>; the champion is the best eligible entry, or none when no entry is
/// eligible, in which case the reference (usually the seed) stands. Combining the two errors as if independent
/// overstates the noise of a difference measured on the same rows, so the rule errs towards keeping the reference.
/// It depends only on the set of entries, never on the order they were found, so a resumed run chooses the same.
/// </para>
/// <para><b>For Beginners:</b> If a score comes from a small test, a slightly higher number may mean nothing. This
/// picks a new winner only when it beats the starting program by clearly more than the test's own wobble, and
/// otherwise keeps the starting program.</para>
/// </remarks>
public static class EvolutionChampionSelection
{
    /// <summary>Returns the best entry whose quality beats the reference by more than the given standard errors.</summary>
    /// <typeparam name="TGenome">The genome type.</typeparam>
    /// <param name="entries">The candidate entries, typically every island's archive entries.</param>
    /// <param name="referenceQuality">The reference's quality, usually the seed's.</param>
    /// <param name="referenceStandardError">The reference quality's standard error; finite and non-negative.</param>
    /// <param name="standardErrorMetric">The metric under which each entry's evaluation reports its quality's standard error.</param>
    /// <param name="standardErrors">How many combined standard errors a gain must exceed; finite and non-negative.</param>
    /// <returns>The best eligible entry, or <c>null</c> when none beats the reference by enough.</returns>
    /// <remarks>
    /// An entry that is not completed, has no quality, or reports no finite, non-negative standard error under
    /// <paramref name="standardErrorMetric"/> is never eligible: a gain whose noise is unknown cannot clear it. Gains
    /// follow each entry's own optimization direction. Among eligible entries the order is the archive's own
    /// best-first order, so ties resolve as they do everywhere else in the engine.
    /// </remarks>
    public static EvolutionArchiveEntry<TGenome>? SelectSignificant<TGenome>(
        IEnumerable<EvolutionArchiveEntry<TGenome>> entries, double referenceQuality, double referenceStandardError,
        string standardErrorMetric, double standardErrors)
    {
        Guard.NotNull(entries);
        if (!EvolutionDescriptorDefinition.IsFinite(referenceQuality)) throw new ArgumentOutOfRangeException(nameof(referenceQuality));
        if (!IsStandardError(referenceStandardError)) throw new ArgumentOutOfRangeException(nameof(referenceStandardError));
        if (string.IsNullOrWhiteSpace(standardErrorMetric))
            throw new ArgumentException("Name the metric that reports each quality's standard error.", nameof(standardErrorMetric));
        if (!IsStandardError(standardErrors)) throw new ArgumentOutOfRangeException(nameof(standardErrors));

        EvolutionArchiveEntry<TGenome>? champion = null;
        IComparer<EvolutionArchiveEntry<TGenome>>? order = null;
        foreach (EvolutionArchiveEntry<TGenome> entry in entries)
        {
            Guard.NotNull(entry);
            EvolutionEvaluation evaluation = entry.Evaluation;
            if (evaluation.Status != EvolutionEvaluationStatus.Completed || evaluation.Quality is not double quality ||
                !evaluation.Metrics.TryGetValue(standardErrorMetric, out double standardError) || !IsStandardError(standardError))
                continue;
            double gain = evaluation.Direction == EvolutionOptimizationDirection.Maximize
                ? quality - referenceQuality
                : referenceQuality - quality;
            double noise = standardErrors * Math.Sqrt(standardError * standardError + referenceStandardError * referenceStandardError);
            if (!(gain > noise)) continue;
            order ??= EvolutionEntryOrdering.BestFirst<TGenome>(evaluation.Direction);
            if (champion is null || order.Compare(entry, champion) < 0) champion = entry;
        }
        return champion;
    }

    private static bool IsStandardError(double value) => EvolutionDescriptorDefinition.IsFinite(value) && value >= 0;
}