using System.Globalization;

namespace AiDotNet.Evolution.Programs;

/// <summary>Compiler-neutral program bounds and edit rules, independent of model-builder configuration.</summary>
public class ProgramTaskOptions
{
    /// <summary>The most metric names that may be promoted to archive descriptors.</summary>
    /// <remarks>
    /// Every name is copied into each options snapshot and becomes an archive coordinate, so an unbounded list is
    /// memory spent before the first evaluation runs. The number matches the descriptor limit the distributed host
    /// protocol accepts, so a configuration this surface allows is one a distributed run can also carry.
    /// </remarks>
    public const int MaxMetricDescriptors = 4096;

    /// <summary>Required candidate language; Generic allows any explicitly identified language.</summary>
    public ProgramLanguage Language { get; set; } = ProgramLanguage.Generic;
    /// <summary>Optional custom start marker, supplied together with the end marker.</summary>
    public string? EvolveBlockStartMarker { get; set; }
    /// <summary>Optional custom end marker, supplied together with the start marker.</summary>
    public string? EvolveBlockEndMarker { get; set; }
    /// <summary>Requires complete markers and prevents edits to protected source.</summary>
    public bool EnforceEvolveBlocks { get; set; }
    /// <summary>Maximum source length accepted before evaluator dispatch.</summary>
    public int MaxProgramChars { get; set; } = 100_000;
    /// <summary>SEARCH/REPLACE parser and matching rules.</summary>
    public ProgramDiffOptions Diff { get; set; } = new();
    /// <summary>Optional cost-unit identity; the caller's evaluator owns ledger accounting.</summary>
    public ProgramEvolutionResourceOptions? ResourceAccounting { get; set; }
    /// <summary>
    /// Evaluator metrics promoted to archive descriptors (OpenEvolve's custom feature dimensions). Every completed
    /// evaluation must report each one as a finite metric; a descriptor the evaluator sets itself takes precedence. For
    /// OpenEvolve's built-in "score" dimension use <see cref="EvolutionEngineOptions.QualityDescriptorName"/>.
    /// </summary>
    public IList<string> MetricDescriptors { get; set; } = new List<string>();

    /// <summary>Resolves the complete custom pair or language defaults.</summary>
    public EvolveBlockMarkers ResolveEvolveBlockMarkers()
    {
        bool startMissing = string.IsNullOrWhiteSpace(EvolveBlockStartMarker);
        bool endMissing = string.IsNullOrWhiteSpace(EvolveBlockEndMarker);
        if (startMissing != endMissing)
            throw new ArgumentException("Set both EvolveBlockStartMarker and EvolveBlockEndMarker, or neither.",
                startMissing ? nameof(EvolveBlockStartMarker) : nameof(EvolveBlockEndMarker));
        return startMissing ? EvolveBlockMarkers.ForLanguage(Language)
            : new EvolveBlockMarkers(EvolveBlockStartMarker ?? string.Empty, EvolveBlockEndMarker ?? string.Empty);
    }

    /// <summary>Copies mutable settings; the caller-owned live resource ledger remains shared.</summary>
    public virtual ProgramTaskOptions Clone() => new()
    {
        Language = Language,
        EvolveBlockStartMarker = EvolveBlockStartMarker,
        EvolveBlockEndMarker = EvolveBlockEndMarker,
        EnforceEvolveBlocks = EnforceEvolveBlocks,
        MaxProgramChars = MaxProgramChars,
        Diff = Diff?.Clone() ?? throw new ArgumentException("Diff options cannot be null.", nameof(Diff)),
        ResourceAccounting = ResourceAccounting,
        MetricDescriptors = CopyMetricDescriptors(MetricDescriptors)
    };

    /// <summary>Copies the descriptor list for a clone, refusing a null or oversized one before allocating.</summary>
    /// <remarks>
    /// Checked here rather than only in <see cref="Validate"/> because a task snapshots its options through
    /// <see cref="Clone"/> and validates the snapshot afterwards, so a bound enforced only by validation would be
    /// reached after the allocation it exists to prevent.
    /// </remarks>
    protected static List<string> CopyMetricDescriptors(IList<string>? descriptors)
    {
        if (descriptors is null)
            throw new ArgumentException("Metric descriptors cannot be null.", nameof(MetricDescriptors));
        if (descriptors.Count > MaxMetricDescriptors)
            throw new ArgumentException(DescriptorCountMessage(descriptors.Count), nameof(MetricDescriptors));
        return new List<string>(descriptors);
    }

    private static string DescriptorCountMessage(int count) =>
        "MetricDescriptors declares " + count.ToString(CultureInfo.InvariantCulture) + " descriptors, more than the "
        + MaxMetricDescriptors.ToString(CultureInfo.InvariantCulture) + " limit. Every name is copied into each "
        + "options snapshot and becomes an archive coordinate, so an unbounded list is memory spent before the "
        + "first evaluation runs.";

    /// <summary>Rejects invalid bounds, languages, and marker settings.</summary>
    public virtual void Validate()
    {
        if (!Enum.IsDefined(Language)) throw new ArgumentOutOfRangeException(nameof(Language));
        if (MaxProgramChars <= 0 || MaxProgramChars > ProgramGenome.MaxSourceLength)
            throw new ArgumentOutOfRangeException(nameof(MaxProgramChars));
        if (Diff is null) throw new ArgumentException("Diff options cannot be null.", nameof(Diff));
        Diff.Validate();
        ResolveEvolveBlockMarkers();
        if (MetricDescriptors is null) throw new ArgumentException("Metric descriptors cannot be null.", nameof(MetricDescriptors));
        if (MetricDescriptors.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Descriptor names must be non-blank.", nameof(MetricDescriptors));
        if (MetricDescriptors.Count > MaxMetricDescriptors)
            throw new ArgumentException(DescriptorCountMessage(MetricDescriptors.Count), nameof(MetricDescriptors));

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in MetricDescriptors)
        {
            // Bounded by the diagnostic that has to report it: when the evaluator omits the metric, the name travels
            // in EvolutionDiagnostic.Data, which refuses a longer value - so a longer name would make the task throw
            // on the one path that exists to report the name instead of returning it.
            if (name.Length > EvolutionDiagnostic.MaximumDataValueLength)
            {
                throw new ArgumentException(
                    "Descriptor name of " + name.Length.ToString(CultureInfo.InvariantCulture)
                    + " characters exceeds the " + EvolutionDiagnostic.MaximumDataValueLength.ToString(CultureInfo.InvariantCulture)
                    + " characters a diagnostic can carry, so a missing metric under this name could not be reported.",
                    nameof(MetricDescriptors));
            }

            if (!names.Add(name)) throw new ArgumentException("Descriptor name '" + name + "' is declared twice.", nameof(MetricDescriptors));
        }
    }
}
