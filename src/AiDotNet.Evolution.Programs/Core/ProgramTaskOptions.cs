namespace AiDotNet.Evolution.Programs;

/// <summary>Compiler-neutral program bounds and edit rules, independent of model-builder configuration.</summary>
public class ProgramTaskOptions
{
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
    /// evaluation must report each one as a finite metric; a descriptor the evaluator sets itself takes precedence.
    /// </summary>
    public IList<string> MetricDescriptors { get; set; } = new List<string>();
    /// <summary>Name of a descriptor that carries the evaluated quality itself (OpenEvolve's built-in "score"), or null for none.</summary>
    public string? QualityDescriptorName { get; set; }

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
        MetricDescriptors = new List<string>(MetricDescriptors ?? throw new ArgumentException("Metric descriptors cannot be null.", nameof(MetricDescriptors))),
        QualityDescriptorName = QualityDescriptorName
    };

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
        if (MetricDescriptors.Any(string.IsNullOrWhiteSpace) || (QualityDescriptorName is { } quality && string.IsNullOrWhiteSpace(quality)))
            throw new ArgumentException("Descriptor names must be non-blank.", nameof(MetricDescriptors));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in MetricDescriptors.Concat(QualityDescriptorName is null ? Array.Empty<string>() : new[] { QualityDescriptorName }))
            if (!names.Add(name)) throw new ArgumentException("Descriptor name '" + name + "' is declared twice.", nameof(MetricDescriptors));
    }
}
