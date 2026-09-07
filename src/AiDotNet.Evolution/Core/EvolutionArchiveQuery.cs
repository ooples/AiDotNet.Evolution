namespace AiDotNet.Evolution;

/// <summary>Queries an archive or a finished run by a named metric rather than by the single scalar quality.</summary>
/// <remarks>
/// <para>
/// A run optimises one number, and that number is what <see cref="IEvolutionArchiveView{TGenome}.Best"/> ranks by.
/// An evaluation usually reports several: accuracy and latency, compression and retention, a score and the seconds
/// it cost. These extensions rank by any one of them, so a search driven by a blended objective can still answer
/// which candidate was the most accurate or fastest without reimplementing stable ordering.
/// </para>
/// <para>
/// A candidate that never reported the metric is absent from the answer rather than treated as having scored zero.
/// Ranking breaks ties on the same chain the archive itself uses, so deterministic runs return deterministic queries.
/// Direction defaults to the archive's own; callers must pass it explicitly for a secondary metric that points the
/// other way, because a metric name cannot encode whether high or low is better.
/// </para>
/// </remarks>
public static class EvolutionArchiveQuery
{
    /// <summary>Returns the archive's best elite by a named metric.</summary>
    public static EvolutionArchiveEntry<TGenome>? BestBy<TGenome>(
        this IEvolutionArchiveView<TGenome> archive,
        string metric,
        EvolutionOptimizationDirection? direction = null)
    {
        Guard.NotNull(archive);
        Guard.NotNullOrWhiteSpace(metric);

        EvolutionOptimizationDirection resolved = ResolveDirection(direction, archive.Direction);
        EvolutionArchiveEntry<TGenome>? best = null;
        foreach (EvolutionArchiveEntry<TGenome> entry in archive.Entries)
        {
            if (!Reports(entry, metric)) continue;
            if (best is null || EvolutionEntryOrdering.CompareByMetric(resolved, metric, entry, best) < 0) best = entry;
        }

        return best;
    }

    /// <summary>Returns the archive's best elites by a named metric, best first.</summary>
    public static IReadOnlyList<EvolutionArchiveEntry<TGenome>> TopBy<TGenome>(
        this IEvolutionArchiveView<TGenome> archive,
        string metric,
        int count,
        EvolutionOptimizationDirection? direction = null)
    {
        Guard.NotNull(archive);
        Guard.NotNullOrWhiteSpace(metric);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "Value cannot be negative.");

        return Rank(archive.Entries, metric, count, ResolveDirection(direction, archive.Direction), deduplicate: false);
    }

    /// <summary>Returns elites that reported a named metric, in the archive's stable order.</summary>
    public static IReadOnlyList<EvolutionArchiveEntry<TGenome>> WithMetric<TGenome>(
        this IEvolutionArchiveView<TGenome> archive,
        string metric)
    {
        Guard.NotNull(archive);
        Guard.NotNullOrWhiteSpace(metric);

        var reporting = new List<EvolutionArchiveEntry<TGenome>>();
        foreach (EvolutionArchiveEntry<TGenome> entry in archive.Entries)
        {
            if (Reports(entry, metric)) reporting.Add(entry);
        }

        return reporting;
    }

    /// <summary>Returns every metric name any elite reported, ordered for stable display.</summary>
    public static IReadOnlyList<string> MetricNames<TGenome>(this IEvolutionArchiveView<TGenome> archive)
    {
        Guard.NotNull(archive);
        return CollectNames(archive.Entries);
    }

    /// <summary>Returns the run's best elite by a named metric across every island.</summary>
    public static EvolutionArchiveEntry<TGenome>? BestBy<TGenome>(
        this EvolutionRunResult<TGenome> result,
        string metric,
        EvolutionOptimizationDirection? direction = null)
    {
        Guard.NotNull(result);
        Guard.NotNullOrWhiteSpace(metric);

        EvolutionOptimizationDirection resolved = ResolveDirection(direction, RunDirection(result));
        EvolutionArchiveEntry<TGenome>? best = null;
        foreach (IEvolutionArchiveView<TGenome> island in result.Islands)
        {
            EvolutionArchiveEntry<TGenome>? candidate = island.BestBy(metric, resolved);
            if (candidate is null) continue;
            if (best is null || EvolutionEntryOrdering.CompareByMetric(resolved, metric, candidate, best) < 0)
                best = candidate;
        }

        return best;
    }

    /// <summary>Returns the run's best distinct elites by a named metric across every island, best first.</summary>
    public static IReadOnlyList<EvolutionArchiveEntry<TGenome>> TopBy<TGenome>(
        this EvolutionRunResult<TGenome> result,
        string metric,
        int count,
        EvolutionOptimizationDirection? direction = null)
    {
        Guard.NotNull(result);
        Guard.NotNullOrWhiteSpace(metric);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "Value cannot be negative.");

        var everywhere = new List<EvolutionArchiveEntry<TGenome>>();
        foreach (IEvolutionArchiveView<TGenome> island in result.Islands) everywhere.AddRange(island.Entries);

        return Rank(everywhere, metric, count, ResolveDirection(direction, RunDirection(result)), deduplicate: true);
    }

    /// <summary>Returns every metric name any elite of the run reported, ordered for stable display.</summary>
    public static IReadOnlyList<string> MetricNames<TGenome>(this EvolutionRunResult<TGenome> result)
    {
        Guard.NotNull(result);

        var everywhere = new List<EvolutionArchiveEntry<TGenome>>();
        foreach (IEvolutionArchiveView<TGenome> island in result.Islands) everywhere.AddRange(island.Entries);

        return CollectNames(everywhere);
    }

    private static IReadOnlyList<EvolutionArchiveEntry<TGenome>> Rank<TGenome>(
        IEnumerable<EvolutionArchiveEntry<TGenome>> entries,
        string metric,
        int count,
        EvolutionOptimizationDirection direction,
        bool deduplicate)
    {
        if (count == 0) return Array.Empty<EvolutionArchiveEntry<TGenome>>();

        var reporting = new List<EvolutionArchiveEntry<TGenome>>();
        foreach (EvolutionArchiveEntry<TGenome> entry in entries)
        {
            if (Reports(entry, metric)) reporting.Add(entry);
        }

        reporting.Sort((x, y) => EvolutionEntryOrdering.CompareByMetric(direction, metric, x, y));

        var top = new List<EvolutionArchiveEntry<TGenome>>(Math.Min(count, reporting.Count));
        HashSet<string>? seen = deduplicate ? new HashSet<string>(StringComparer.Ordinal) : null;
        foreach (EvolutionArchiveEntry<TGenome> entry in reporting)
        {
            if (top.Count >= count) break;
            if (seen is not null && !seen.Add(entry.Evaluation.GenomeId)) continue;
            top.Add(entry);
        }

        return top;
    }

    private static IReadOnlyList<string> CollectNames<TGenome>(IEnumerable<EvolutionArchiveEntry<TGenome>> entries)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (EvolutionArchiveEntry<TGenome> entry in entries)
        {
            foreach (KeyValuePair<string, double> metric in entry.Evaluation.Metrics)
            {
                if (EvolutionDescriptorDefinition.IsFinite(metric.Value)) names.Add(metric.Key);
            }
        }

        return new List<string>(names);
    }

    private static bool Reports<TGenome>(EvolutionArchiveEntry<TGenome> entry, string metric) =>
        entry.Evaluation.Metrics.TryGetValue(metric, out double value) &&
        EvolutionDescriptorDefinition.IsFinite(value);

    private static EvolutionOptimizationDirection ResolveDirection(
        EvolutionOptimizationDirection? requested,
        EvolutionOptimizationDirection fallback)
    {
        EvolutionOptimizationDirection resolved = requested ?? fallback;
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), resolved))
            throw new ArgumentOutOfRangeException(nameof(requested), resolved, "Unknown optimization direction.");
        return resolved;
    }

    private static EvolutionOptimizationDirection RunDirection<TGenome>(EvolutionRunResult<TGenome> result) =>
        result.Islands.Count > 0 ? result.Islands[0].Direction : EvolutionOptimizationDirection.Maximize;
}
