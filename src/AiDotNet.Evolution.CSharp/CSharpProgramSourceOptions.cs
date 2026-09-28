using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.CSharp;

/// <summary>Source boundaries for a standalone compiler arm; no AiModelBuilder dependency.</summary>
public sealed class CSharpProgramSourceOptions
{
    /// <summary>Gets or sets the program language. Only <see cref="ProgramLanguage.CSharp"/> is accepted.</summary>
    public ProgramLanguage Language { get; set; } = ProgramLanguage.CSharp;

    /// <summary>Gets or sets what the program should do, shown to the model; at most 4096 characters.</summary>
    public string? TaskDescription { get; set; }

    /// <summary>Gets or sets the longest accepted program, in characters. Defaults to 65,536.</summary>
    public int MaxProgramChars { get; set; } = 65_536;

    /// <summary>Gets or sets whether edits must stay inside EVOLVE-BLOCK regions.</summary>
    public bool EnforceEvolveBlocks { get; set; }

    /// <summary>Gets or sets the start marker of an editable region, or <c>null</c> for <see cref="EvolveBlock.SlashStartMarker"/>.</summary>
    public string? EvolveBlockStartMarker { get; set; }

    /// <summary>Gets or sets the end marker of an editable region, or <c>null</c> for <see cref="EvolveBlock.SlashEndMarker"/>.</summary>
    public string? EvolveBlockEndMarker { get; set; }

    internal CSharpProgramSourceOptions Clone() => (CSharpProgramSourceOptions)MemberwiseClone();
    internal EvolveBlockMarkers ResolveEvolveBlockMarkers() => new(
        EvolveBlockStartMarker ?? EvolveBlock.SlashStartMarker,
        EvolveBlockEndMarker ?? EvolveBlock.SlashEndMarker);

    internal void Validate()
    {
        if (Language != ProgramLanguage.CSharp) throw new ArgumentException("CSharp program language is required.");
        if (MaxProgramChars is < 1 or > ProgramGenome.MaxSourceLength) throw new ArgumentOutOfRangeException(nameof(MaxProgramChars));
        if (TaskDescription?.Length > 4096) throw new ArgumentException("Task descriptions are bounded to 4096 characters.");
        _ = new System.Text.UTF8Encoding(false, true).GetByteCount(TaskDescription ?? string.Empty);
        _ = ResolveEvolveBlockMarkers();
    }
}
