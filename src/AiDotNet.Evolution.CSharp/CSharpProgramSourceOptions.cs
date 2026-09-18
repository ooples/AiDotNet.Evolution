using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.CSharp;

/// <summary>Source boundaries for a standalone compiler arm; no AiModelBuilder dependency.</summary>
public sealed class CSharpProgramSourceOptions
{
    public ProgramLanguage Language { get; set; } = ProgramLanguage.CSharp;
    public string? TaskDescription { get; set; }
    public int MaxProgramChars { get; set; } = 65_536;
    public bool EnforceEvolveBlocks { get; set; }
    public string? EvolveBlockStartMarker { get; set; }
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
