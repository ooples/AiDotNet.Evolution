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

    /// <summary>Resolves the complete custom pair or language defaults.</summary>
    public EvolveBlockMarkers ResolveEvolveBlockMarkers()
    {
        bool startMissing = string.IsNullOrWhiteSpace(EvolveBlockStartMarker);
        bool endMissing = string.IsNullOrWhiteSpace(EvolveBlockEndMarker);
        if (startMissing != endMissing)
            throw new ArgumentException("Set both EvolveBlockStartMarker and EvolveBlockEndMarker, or neither.",
                startMissing ? nameof(EvolveBlockStartMarker) : nameof(EvolveBlockEndMarker));
        return startMissing ? EvolveBlockMarkers.ForLanguage(Language)
            : new EvolveBlockMarkers(EvolveBlockStartMarker!, EvolveBlockEndMarker!);
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
        ResourceAccounting = ResourceAccounting
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
    }
}
